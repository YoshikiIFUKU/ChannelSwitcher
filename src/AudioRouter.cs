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

        const int SampleRate = 48000;
        const int FramesPerBuffer = SampleRate / 50;   // 20ms
        const int InputBufferCount = 8;
        const int OutputBufferCount = 16;
        const int MaxQueuedOutput = 6;                // これ以上たまったら捨てて遅延の伸びを防ぐ

        static readonly int HeaderSize = Marshal.SizeOf(typeof(WinMM.WAVEHDR));
        static readonly int FlagsOffset = Marshal.OffsetOf(typeof(WinMM.WAVEHDR), "dwFlags").ToInt32();
        static readonly int RecordedOffset = Marshal.OffsetOf(typeof(WinMM.WAVEHDR), "dwBytesRecorded").ToInt32();

        sealed class Buffer
        {
            public IntPtr Header;
            public IntPtr Data;
            public int Capacity;

            public Buffer(int bytes)
            {
                Capacity = bytes;
                Data = Marshal.AllocHGlobal(bytes);
                Header = Marshal.AllocHGlobal(HeaderSize);
                Reset(bytes);
            }

            public void Reset(int length)
            {
                WinMM.WAVEHDR h = new WinMM.WAVEHDR();
                h.lpData = Data;
                h.dwBufferLength = length;
                Marshal.StructureToPtr(h, Header, false);
            }

            public bool IsDone { get { return (Marshal.ReadInt32(Header, FlagsOffset) & WinMM.WHDR_DONE) != 0; } }
            public int BytesRecorded { get { return Marshal.ReadInt32(Header, RecordedOffset); } }

            public void Free()
            {
                Marshal.FreeHGlobal(Data);
                Marshal.FreeHGlobal(Header);
            }
        }

        // 出力デバイス1つ分の状態
        sealed class Output
        {
            public int Device;
            public IntPtr Handle = IntPtr.Zero;
            public readonly List<Buffer> Buffers = new List<Buffer>();
            public readonly Stack<Buffer> Free = new Stack<Buffer>();
            public readonly List<Buffer> Busy = new List<Buffer>();
            public readonly short[] Samples = new short[FramesPerBuffer * 2];
            public volatile bool Muted;
            public double Gain = 1.0;
            public volatile int Peak;
            public volatile int Dropped;
        }

        readonly int inputDevice;
        readonly Output[] outputs = new Output[OutputCount];
        IntPtr hIn = IntPtr.Zero;
        int inChannels;
        readonly List<Buffer> inBuffers = new List<Buffer>();
        readonly Queue<Buffer> inPending = new Queue<Buffer>();
        readonly AutoResetEvent inEvent = new AutoResetEvent(false);
        Thread thread;
        volatile bool running;
        volatile int inputPeak;
        volatile string lastError;

        public AudioRouter(int inputDevice, int[] outputDevices)
        {
            this.inputDevice = inputDevice;
            for (int i = 0; i < OutputCount; i++)
            {
                outputs[i] = new Output();
                outputs[i].Device = outputDevices[i];
            }
        }

        public void SetMuted(int output, bool muted) { outputs[output].Muted = muted; }
        public int GetOutputPeak(int output) { return outputs[output].Peak; }
        public int InputPeak { get { return inputPeak; } }
        public int DroppedBuffers { get { int n = 0; foreach (Output o in outputs) n += o.Dropped; return n; } }
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
                for (int i = 0; i < OutputCount; i++) OpenOutput(outputs[i], i + 1);

                for (int i = 0; i < InputBufferCount; i++)
                {
                    Buffer b = new Buffer(FramesPerBuffer * inChannels * 2);
                    inBuffers.Add(b);
                    Check(WinMM.waveInPrepareHeader(hIn, b.Header, HeaderSize), "録音バッファの準備");
                    Check(WinMM.waveInAddBuffer(hIn, b.Header, HeaderSize), "録音バッファの登録");
                    inPending.Enqueue(b);
                }

                running = true;
                Check(WinMM.waveInStart(hIn), "録音の開始");
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
            if (r != 0) { hIn = IntPtr.Zero; Check(r, "入力デバイスを開く"); }
        }

        static void OpenOutput(Output o, int number)
        {
            WinMM.WAVEFORMATEX fmt = WinMM.PcmFormat(SampleRate, 2);
            IntPtr h;
            int r = WinMM.waveOutOpen(out h, o.Device, ref fmt, IntPtr.Zero, IntPtr.Zero, WinMM.CALLBACK_NULL);
            Check(r, "出力" + number + "のデバイスを開く");
            o.Handle = h;
            o.Gain = o.Muted ? 0.0 : 1.0;
            for (int i = 0; i < OutputBufferCount; i++)
            {
                Buffer b = new Buffer(FramesPerBuffer * 4);
                o.Buffers.Add(b);
                o.Free.Push(b);
            }
        }

        static void Check(int result, string what)
        {
            if (result != 0)
                throw new InvalidOperationException(what + "に失敗しました: " + WinMM.ErrorText(result));
        }

        void Loop()
        {
            short[] inSamples = new short[FramesPerBuffer * 2];
            try
            {
                while (running)
                {
                    inEvent.WaitOne(50);
                    foreach (Output o in outputs) Reclaim(o);
                    while (running && inPending.Count > 0 && inPending.Peek().IsDone)
                    {
                        Buffer b = inPending.Dequeue();
                        int bytes = b.BytesRecorded;
                        if (bytes > 0) Process(b, bytes, inSamples);

                        WinMM.waveInUnprepareHeader(hIn, b.Header, HeaderSize);
                        b.Reset(b.Capacity);
                        Check(WinMM.waveInPrepareHeader(hIn, b.Header, HeaderSize), "録音バッファの準備");
                        Check(WinMM.waveInAddBuffer(hIn, b.Header, HeaderSize), "録音の継続（デバイスが外れた可能性があります）");
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

        void Process(Buffer b, int bytes, short[] inSamples)
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

            for (int n = 0; n < OutputCount; n++)
                Send(outputs[n], inSamples, frames, n + 1);
        }

        static void Send(Output o, short[] mono, int frames, int number)
        {
            // ミュートしても無音を送り続けて、受け手側のストリームを途切れさせない。
            // 切り替え時のプツッというノイズを避けるため、1バッファ（20ms）かけてゲインを変える。
            double target = o.Muted ? 0.0 : 1.0;
            double step = (target - o.Gain) / frames;
            double g = o.Gain;
            int peak = 0;
            short[] s = o.Samples;
            for (int i = 0; i < frames; i++)
            {
                g += step;
                int v = (int)(mono[i] * g);
                s[2 * i] = (short)v;
                s[2 * i + 1] = (short)v;
                if (v < 0) v = -v;
                if (v > peak) peak = v;
            }
            o.Gain = target;
            o.Peak = peak;

            if (o.Free.Count == 0 || o.Busy.Count >= MaxQueuedOutput)
            {
                o.Dropped++;
                return;
            }
            Buffer ob = o.Free.Pop();
            Marshal.Copy(s, 0, ob.Data, frames * 2);
            ob.Reset(frames * 4);
            Check(WinMM.waveOutPrepareHeader(o.Handle, ob.Header, HeaderSize), "出力" + number + "の再生バッファの準備");
            Check(WinMM.waveOutWrite(o.Handle, ob.Header, HeaderSize), "出力" + number + "への送信（デバイスが外れた可能性があります）");
            o.Busy.Add(ob);
        }

        static void Reclaim(Output o)
        {
            for (int i = o.Busy.Count - 1; i >= 0; i--)
            {
                Buffer b = o.Busy[i];
                if (!b.IsDone) continue;
                WinMM.waveOutUnprepareHeader(o.Handle, b.Header, HeaderSize);
                o.Busy.RemoveAt(i);
                o.Free.Push(b);
            }
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
                foreach (Buffer b in inBuffers) WinMM.waveInUnprepareHeader(hIn, b.Header, HeaderSize);
                WinMM.waveInClose(hIn);
                hIn = IntPtr.Zero;
            }
            foreach (Buffer b in inBuffers) b.Free();
            inBuffers.Clear();
            inPending.Clear();
            inputPeak = 0;

            foreach (Output o in outputs)
            {
                if (o.Handle != IntPtr.Zero)
                {
                    WinMM.waveOutReset(o.Handle);
                    foreach (Buffer b in o.Buffers) WinMM.waveOutUnprepareHeader(o.Handle, b.Header, HeaderSize);
                    WinMM.waveOutClose(o.Handle);
                    o.Handle = IntPtr.Zero;
                }
                foreach (Buffer b in o.Buffers) b.Free();
                o.Buffers.Clear();
                o.Free.Clear();
                o.Busy.Clear();
                o.Peak = 0;
            }
        }

        public void Dispose()
        {
            Stop();
            inEvent.Close();
        }
    }
}
