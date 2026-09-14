using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ChannelSwitcher
{
    // マイクの音をモノラルで取り込み、2つの出力デバイス（仮想ケーブル）へ同時に流す。出力ごとにミュートできる。
    public sealed class AudioRouter : IDisposable
    {
        public const int OutputCount = 2;

        const int SampleRate = AudioOutput.SampleRate;
        const int FramesPerBuffer = AudioOutput.FramesPerBuffer;
        const int InputBufferCount = 8;

        readonly int inputDevice;
        readonly int[] outputDevices;
        readonly bool[] initialMuted = new bool[OutputCount];
        readonly float[] initialVolume = { 1f, 1f };
        readonly AudioOutput[] outputs = new AudioOutput[OutputCount];
        IntPtr hIn = IntPtr.Zero;
        int inChannels;
        readonly List<PcmBuffer> inBuffers = new List<PcmBuffer>();
        readonly Queue<PcmBuffer> inPending = new Queue<PcmBuffer>();
        readonly AutoResetEvent inEvent = new AutoResetEvent(false);
        Thread thread;
        volatile bool running;
        volatile int inputPeak;
        volatile string lastError;

        public AudioRouter(int inputDevice, int[] outputDevices)
        {
            this.inputDevice = inputDevice;
            this.outputDevices = outputDevices;
        }

        public void SetMuted(int output, bool muted)
        {
            initialMuted[output] = muted;
            AudioOutput o = outputs[output];
            if (o != null) o.Muted = muted;
        }

        public void SetVolume(int output, float volume)
        {
            initialVolume[output] = volume;
            AudioOutput o = outputs[output];
            if (o != null) o.Volume = volume;
        }

        public int GetOutputPeak(int output) { AudioOutput o = outputs[output]; return o == null ? 0 : o.Peak; }
        public int InputPeak { get { return inputPeak; } }
        public string LastError { get { return lastError; } }
        public bool IsRunning { get { return running; } }

        public static string[] GetInputDevices()
        {
            int n = WinMM.waveInGetNumDevs();
            string[] names = new string[n];
            for (int i = 0; i < n; i++)
            {
                WinMM.WAVEINCAPS caps = new WinMM.WAVEINCAPS();
                WinMM.waveInGetDevCaps(new IntPtr(i), ref caps, Marshal.SizeOf(caps));
                names[i] = caps.szPname;
            }
            return names;
        }

        public static string[] GetOutputDevices()
        {
            int n = WinMM.waveOutGetNumDevs();
            string[] names = new string[n];
            for (int i = 0; i < n; i++)
            {
                WinMM.WAVEOUTCAPS caps = new WinMM.WAVEOUTCAPS();
                WinMM.waveOutGetDevCaps(new IntPtr(i), ref caps, Marshal.SizeOf(caps));
                names[i] = caps.szPname;
            }
            return names;
        }

        public void Start()
        {
            if (running) return;
            try
            {
                OpenInput();
                for (int i = 0; i < OutputCount; i++)
                {
                    outputs[i] = new AudioOutput(outputDevices[i], "出力" + (i + 1));
                    outputs[i].Muted = initialMuted[i];
                    outputs[i].Volume = initialVolume[i];
                }

                for (int i = 0; i < InputBufferCount; i++)
                {
                    PcmBuffer b = new PcmBuffer(FramesPerBuffer * inChannels * 2);
                    inBuffers.Add(b);
                    PcmBuffer.Check(WinMM.waveInPrepareHeader(hIn, b.Header, PcmBuffer.HeaderSize), "録音バッファの準備");
                    PcmBuffer.Check(WinMM.waveInAddBuffer(hIn, b.Header, PcmBuffer.HeaderSize), "録音バッファの登録");
                    inPending.Enqueue(b);
                }

                running = true;
                PcmBuffer.Check(WinMM.waveInStart(hIn), "録音の開始");
                thread = new Thread(Loop);
                thread.IsBackground = true;
                thread.Priority = ThreadPriority.AboveNormal;
                thread.Name = "AudioRouter";
                thread.Start();
            }
            catch
            {
                Stop();
                throw;
            }
        }

        void OpenInput()
        {
            IntPtr evt = inEvent.SafeWaitHandle.DangerousGetHandle();

            // まずモノラルで開き、ダメならステレオで開いて平均を取る
            WinMM.WAVEFORMATEX fmt = WinMM.PcmFormat(SampleRate, 1);
            int r = WinMM.waveInOpen(out hIn, inputDevice, ref fmt, evt, IntPtr.Zero, WinMM.CALLBACK_EVENT);
            inChannels = 1;
            if (r != 0)
            {
                fmt = WinMM.PcmFormat(SampleRate, 2);
                r = WinMM.waveInOpen(out hIn, inputDevice, ref fmt, evt, IntPtr.Zero, WinMM.CALLBACK_EVENT);
                inChannels = 2;
            }
            if (r != 0) { hIn = IntPtr.Zero; PcmBuffer.Check(r, "入力デバイスを開く"); }
        }

        void Loop()
        {
            short[] inSamples = new short[FramesPerBuffer * 2];
            try
            {
                while (running)
                {
                    inEvent.WaitOne(50);
                    foreach (AudioOutput o in outputs) o.Reclaim();
                    while (running && inPending.Count > 0 && inPending.Peek().IsDone)
                    {
                        PcmBuffer b = inPending.Dequeue();
                        int bytes = b.BytesRecorded;
                        if (bytes > 0) Process(b, bytes, inSamples);

                        WinMM.waveInUnprepareHeader(hIn, b.Header, PcmBuffer.HeaderSize);
                        b.Reset(b.Capacity);
                        PcmBuffer.Check(WinMM.waveInPrepareHeader(hIn, b.Header, PcmBuffer.HeaderSize), "録音バッファの準備");
                        PcmBuffer.Check(WinMM.waveInAddBuffer(hIn, b.Header, PcmBuffer.HeaderSize), "録音の継続（デバイスが外れた可能性があります）");
                        inPending.Enqueue(b);
                    }
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                running = false;
            }
        }

        void Process(PcmBuffer b, int bytes, short[] inSamples)
        {
            int frames = bytes / (2 * inChannels);
            if (frames > FramesPerBuffer) frames = FramesPerBuffer;
            Marshal.Copy(b.Data, inSamples, 0, frames * inChannels);

            if (inChannels == 2)
                for (int i = 0; i < frames; i++)
                    inSamples[i] = (short)((inSamples[2 * i] + inSamples[2 * i + 1]) / 2);

            int ip = 0;
            for (int i = 0; i < frames; i++)
            {
                int a = inSamples[i] < 0 ? -inSamples[i] : inSamples[i];
                if (a > ip) ip = a;
            }
            inputPeak = ip;

            foreach (AudioOutput o in outputs) o.WriteMono(inSamples, frames);
        }

        public void Stop()
        {
            running = false;
            inEvent.Set();
            if (thread != null)
            {
                thread.Join(2000);
                thread = null;
            }

            if (hIn != IntPtr.Zero)
            {
                WinMM.waveInReset(hIn);
                WinMM.waveInStop(hIn);
                foreach (PcmBuffer b in inBuffers) WinMM.waveInUnprepareHeader(hIn, b.Header, PcmBuffer.HeaderSize);
                WinMM.waveInClose(hIn);
                hIn = IntPtr.Zero;
            }
            foreach (PcmBuffer b in inBuffers) b.Free();
            inBuffers.Clear();
            inPending.Clear();
            inputPeak = 0;

            for (int i = 0; i < OutputCount; i++)
            {
                if (outputs[i] == null) continue;
                outputs[i].Close();
                outputs[i] = null;
            }
        }

        public void Dispose()
        {
            Stop();
            inEvent.Close();
        }
    }
}
