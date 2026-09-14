using System;
using System.Threading;

namespace ChannelSwitcher
{
    public enum SourceChannel { Left = 0, Right = 1 }

    // ステレオの WAV を再生し、出力ごとに左右どちらのチャンネルを流すか選べるプレーヤー。
    // 必要ならモニター用デバイスにステレオのまま同時再生する。
    public sealed class FilePlayer : IDisposable
    {
        const int OutputCount = AudioRouter.OutputCount;
        const int FramesPerBuffer = AudioOutput.FramesPerBuffer;
        const int TargetQueued = 4;          // 各出力に 80ms 分先読みして送っておく
        const int ReadBlockFrames = 8192;

        readonly string path;
        readonly int[] outputDevices;
        readonly int monitorDevice;
        readonly int[] sources = new int[OutputCount];
        readonly bool[] muted = new bool[OutputCount];

        WavReader reader;
        readonly AudioOutput[] outputs = new AudioOutput[OutputCount];
        AudioOutput monitor;
        Thread thread;
        bool timerRaised;

        volatile bool running, paused, finished, completed;
        long seekRequest = -1;
        long positionFrames;
        volatile int sourcePeak;
        volatile string lastError;

        public FilePlayer(string path, int[] outputDevices, int monitorDevice)
        {
            this.path = path;
            this.outputDevices = outputDevices;
            this.monitorDevice = monitorDevice;
            sources[0] = (int)SourceChannel.Left;
            sources[1] = (int)SourceChannel.Right;
        }

        public void SetSource(int output, SourceChannel channel) { sources[output] = (int)channel; }

        public void SetMuted(int output, bool value)
        {
            muted[output] = value;
            AudioOutput o = outputs[output];
            if (o != null) o.Muted = value;
        }

        public int GetOutputPeak(int output) { AudioOutput o = outputs[output]; return o == null ? 0 : o.Peak; }
        public int SourcePeak { get { return sourcePeak; } }
        public string LastError { get { return lastError; } }
        public bool IsPaused { get { return paused; } }
        public bool IsCompleted { get { return completed; } }
        public double PositionSeconds { get { return reader == null ? 0 : (double)Interlocked.Read(ref positionFrames) / reader.SampleRate; } }

        public void Start(double startSeconds)
        {
            try
            {
                reader = new WavReader(path);
                for (int i = 0; i < OutputCount; i++)
                {
                    outputs[i] = new AudioOutput(outputDevices[i], "出力" + (i + 1));
                    outputs[i].Muted = muted[i];
                }
                if (monitorDevice >= 0) monitor = new AudioOutput(monitorDevice, "モニター");

                long start = (long)(startSeconds * reader.SampleRate);
                if (start < 0 || start >= reader.TotalFrames) start = 0;
                positionFrames = start;

                WinMM.timeBeginPeriod(1);   // Sleep の精度を 1ms にして、送り出しの間隔を安定させる
                timerRaised = true;

                running = true;
                thread = new Thread(Loop);
                thread.IsBackground = true;
                thread.Priority = ThreadPriority.AboveNormal;
                thread.Name = "FilePlayer";
                thread.Start();
            }
            catch
            {
                Stop();
                throw;
            }
        }

        public void Pause() { paused = true; }
        public void Resume() { paused = false; }

        public void Seek(double seconds)
        {
            if (reader == null) return;
            long frame = (long)(seconds * reader.SampleRate);
            if (frame < 0) frame = 0;
            if (frame >= reader.TotalFrames) frame = reader.TotalFrames - 1;
            Interlocked.Exchange(ref seekRequest, frame);
        }

        void Loop()
        {
            float[] bufL = new float[ReadBlockFrames];
            float[] bufR = new float[ReadBlockFrames];
            long bufStart = 0;
            int bufLen = 0;
            short[] outL = new short[FramesPerBuffer];
            short[] outR = new short[FramesPerBuffer];
            short[] stereo = new short[FramesPerBuffer * 2];
            double step = (double)reader.SampleRate / AudioOutput.SampleRate;   // 48kHz へ線形補間で変換
            double pos = positionFrames;

            try
            {
                while (running)
                {
                    Thread.Sleep(5);

                    long seek = Interlocked.Exchange(ref seekRequest, -1);
                    if (seek >= 0)
                    {
                        pos = seek;
                        finished = false;
                        Interlocked.Exchange(ref positionFrames, seek);
                    }

                    foreach (AudioOutput o in outputs) o.Reclaim();
                    if (monitor != null) monitor.Reclaim();

                    if (paused)
                    {
                        sourcePeak = 0;
                        foreach (AudioOutput o in outputs) o.Peak = 0;
                        continue;
                    }

                    while (running && !finished && MinPending() < TargetQueued)
                    {
                        int frames = 0, peak = 0;
                        for (; frames < FramesPerBuffer; frames++)
                        {
                            long i = (long)pos;
                            if (i >= reader.TotalFrames) { finished = true; break; }
                            if (i < bufStart || i + 1 >= bufStart + bufLen)
                            {
                                bufLen = reader.Read(i, ReadBlockFrames, bufL, bufR);
                                bufStart = i;
                                if (bufLen == 0) { finished = true; break; }
                            }
                            int k = (int)(i - bufStart);
                            float l = bufL[k], r = bufR[k];
                            if (k + 1 < bufLen)
                            {
                                float frac = (float)(pos - i);
                                l += (bufL[k + 1] - l) * frac;
                                r += (bufR[k + 1] - r) * frac;
                            }
                            short sl = ToShort(l), sr = ToShort(r);
                            outL[frames] = sl;
                            outR[frames] = sr;
                            stereo[2 * frames] = sl;
                            stereo[2 * frames + 1] = sr;
                            int al = sl < 0 ? -sl : sl, ar = sr < 0 ? -sr : sr;
                            if (al > peak) peak = al;
                            if (ar > peak) peak = ar;
                            pos += step;
                        }
                        if (frames == 0) break;

                        for (int n = 0; n < OutputCount; n++)
                            outputs[n].WriteMono(sources[n] == (int)SourceChannel.Right ? outR : outL, frames);
                        if (monitor != null) monitor.WriteStereo(stereo, frames);
                        sourcePeak = peak;
                        Interlocked.Exchange(ref positionFrames, Math.Min((long)pos, reader.TotalFrames));
                    }

                    // 最後まで送り切って、各デバイスが鳴らし終えたら完了
                    if (finished && MinPending() == 0 && MaxPending() == 0)
                    {
                        sourcePeak = 0;
                        completed = true;
                    }
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                running = false;
            }
        }

        static short ToShort(float v)
        {
            int s = (int)(v * 32767f);
            if (s > 32767) return 32767;
            if (s < -32768) return -32768;
            return (short)s;
        }

        int MinPending()
        {
            int m = int.MaxValue;
            foreach (AudioOutput o in outputs) m = Math.Min(m, o.Pending);
            return m;
        }

        int MaxPending()
        {
            int m = 0;
            foreach (AudioOutput o in outputs) m = Math.Max(m, o.Pending);
            return m;
        }

        public void Stop()
        {
            running = false;
            if (thread != null)
            {
                thread.Join(2000);
                thread = null;
            }
            for (int i = 0; i < OutputCount; i++)
            {
                if (outputs[i] == null) continue;
                outputs[i].Close();
                outputs[i] = null;
            }
            if (monitor != null)
            {
                monitor.Close();
                monitor = null;
            }
            if (reader != null)
            {
                reader.Dispose();
                reader = null;
            }
            if (timerRaised)
            {
                WinMM.timeEndPeriod(1);
                timerRaised = false;
            }
            sourcePeak = 0;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
