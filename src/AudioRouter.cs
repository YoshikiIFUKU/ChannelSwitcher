using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ChannelSwitcher
{
    public enum ChannelMode { Left = 0, Both = 1, Right = 2 }

    // マイクの音をモノラルで取り込み、選択中のモードに合わせたステレオにして出力デバイス（仮想ケーブル）へ流す。
    public sealed class AudioRouter : IDisposable
    {
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

        readonly int inputDevice;
        readonly int outputDevice;
        IntPtr hIn = IntPtr.Zero;
        IntPtr hOut = IntPtr.Zero;
        int inChannels;
        readonly List<Buffer> inBuffers = new List<Buffer>();
        readonly List<Buffer> outBuffers = new List<Buffer>();
        readonly Queue<Buffer> inPending = new Queue<Buffer>();
        readonly Stack<Buffer> outFree = new Stack<Buffer>();
        readonly List<Buffer> outBusy = new List<Buffer>();
        readonly AutoResetEvent inEvent = new AutoResetEvent(false);
        Thread thread;
        volatile bool running;
        volatile int mode = (int)ChannelMode.Both;
        double gainL = 1.0, gainR = 1.0;

        volatile int peakL, peakR;
        volatile int droppedBuffers;
        volatile string lastError;

        public AudioRouter(int inputDevice, int outputDevice)
        {
            this.inputDevice = inputDevice;
            this.outputDevice = outputDevice;
        }

        public ChannelMode Mode
        {
            get { return (ChannelMode)mode; }
            set { mode = (int)value; }
        }

        public int PeakLeft { get { return peakL; } }
        public int PeakRight { get { return peakR; } }
        public int DroppedBuffers { get { return droppedBuffers; } }
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
                OpenDevices();
                ChannelMode m = Mode;
                gainL = m == ChannelMode.Right ? 0.0 : 1.0;
                gainR = m == ChannelMode.Left ? 0.0 : 1.0;

                for (int i = 0; i < InputBufferCount; i++)
                {
                    Buffer b = new Buffer(FramesPerBuffer * inChannels * 2);
                    inBuffers.Add(b);
                    Check(WinMM.waveInPrepareHeader(hIn, b.Header, HeaderSize), "録音バッファの準備");
                    Check(WinMM.waveInAddBuffer(hIn, b.Header, HeaderSize), "録音バッファの登録");
                    inPending.Enqueue(b);
                }
                for (int i = 0; i < OutputBufferCount; i++)
                {
                    Buffer b = new Buffer(FramesPerBuffer * 4);
                    outBuffers.Add(b);
                    outFree.Push(b);
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

        void OpenDevices()
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

            WinMM.WAVEFORMATEX outFmt = WinMM.PcmFormat(SampleRate, 2);
            r = WinMM.waveOutOpen(out hOut, outputDevice, ref outFmt, IntPtr.Zero, IntPtr.Zero, WinMM.CALLBACK_NULL);
            if (r != 0) { hOut = IntPtr.Zero; Check(r, "出力デバイスを開く"); }
        }

        static void Check(int result, string what)
        {
            if (result != 0)
                throw new InvalidOperationException(what + "に失敗しました: " + WinMM.ErrorText(result));
        }

        void Loop()
        {
            short[] inSamples = new short[FramesPerBuffer * 2];
            short[] outSamples = new short[FramesPerBuffer * 2];
            try
            {
                while (running)
                {
                    inEvent.WaitOne(50);
                    ReclaimOutput();
                    while (running && inPending.Count > 0 && inPending.Peek().IsDone)
                    {
                        Buffer b = inPending.Dequeue();
                        int bytes = b.BytesRecorded;
                        if (bytes > 0) Process(b, bytes, inSamples, outSamples);

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

        void Process(Buffer b, int bytes, short[] inSamples, short[] outSamples)
        {
            int frames = bytes / (2 * inChannels);
            if (frames > FramesPerBuffer) frames = FramesPerBuffer;
            Marshal.Copy(b.Data, inSamples, 0, frames * inChannels);

            ChannelMode m = Mode;
            double targetL = m == ChannelMode.Right ? 0.0 : 1.0;
            double targetR = m == ChannelMode.Left ? 0.0 : 1.0;
            // 切り替え時のプツッというノイズを避けるため、1バッファ（20ms）かけてゲインを変える
            double stepL = (targetL - gainL) / frames;
            double stepR = (targetR - gainR) / frames;

            int pl = 0, pr = 0;
            for (int i = 0; i < frames; i++)
            {
                int s = inChannels == 1 ? inSamples[i] : (inSamples[2 * i] + inSamples[2 * i + 1]) / 2;
                gainL += stepL;
                gainR += stepR;
                int l = (int)(s * gainL);
                int r = (int)(s * gainR);
                outSamples[2 * i] = (short)l;
                outSamples[2 * i + 1] = (short)r;
                if (l < 0) l = -l;
                if (r < 0) r = -r;
                if (l > pl) pl = l;
                if (r > pr) pr = r;
            }
            gainL = targetL;
            gainR = targetR;
            peakL = pl;
            peakR = pr;

            if (outFree.Count == 0 || outBusy.Count >= MaxQueuedOutput)
            {
                droppedBuffers++;
                return;
            }
            Buffer ob = outFree.Pop();
            Marshal.Copy(outSamples, 0, ob.Data, frames * 2);
            ob.Reset(frames * 4);
            Check(WinMM.waveOutPrepareHeader(hOut, ob.Header, HeaderSize), "再生バッファの準備");
            Check(WinMM.waveOutWrite(hOut, ob.Header, HeaderSize), "出力（デバイスが外れた可能性があります）");
            outBusy.Add(ob);
        }

        void ReclaimOutput()
        {
            for (int i = outBusy.Count - 1; i >= 0; i--)
            {
                Buffer b = outBusy[i];
                if (!b.IsDone) continue;
                WinMM.waveOutUnprepareHeader(hOut, b.Header, HeaderSize);
                outBusy.RemoveAt(i);
                outFree.Push(b);
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
            if (hOut != IntPtr.Zero)
            {
                WinMM.waveOutReset(hOut);
                foreach (Buffer b in outBuffers) WinMM.waveOutUnprepareHeader(hOut, b.Header, HeaderSize);
                WinMM.waveOutClose(hOut);
                hOut = IntPtr.Zero;
            }

            foreach (Buffer b in inBuffers) b.Free();
            foreach (Buffer b in outBuffers) b.Free();
            inBuffers.Clear();
            outBuffers.Clear();
            inPending.Clear();
            outFree.Clear();
            outBusy.Clear();
            peakL = 0;
            peakR = 0;
        }

        public void Dispose()
        {
            Stop();
            inEvent.Close();
        }
    }
}
