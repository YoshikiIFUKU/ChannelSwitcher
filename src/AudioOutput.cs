using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ChannelSwitcher
{
    // winmm に渡す録音・再生バッファ（ヘッダとデータをアンマネージドメモリに確保）
    internal sealed class PcmBuffer
    {
        public static readonly int HeaderSize = Marshal.SizeOf(typeof(WinMM.WAVEHDR));
        static readonly int FlagsOffset = Marshal.OffsetOf(typeof(WinMM.WAVEHDR), "dwFlags").ToInt32();
        static readonly int RecordedOffset = Marshal.OffsetOf(typeof(WinMM.WAVEHDR), "dwBytesRecorded").ToInt32();

        public IntPtr Header;
        public IntPtr Data;
        public int Capacity;

        public PcmBuffer(int bytes)
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

        public static void Check(int result, string what)
        {
            if (result != 0)
                throw new InvalidOperationException(what + "に失敗しました: " + WinMM.ErrorText(result));
        }
    }

    // 出力デバイス1つ分。48kHz ステレオ 16bit で開き、20ms 単位で書き込む。
    // Write / Reclaim は音声スレッドからのみ、Close はそのスレッドが止まってから呼ぶこと。
    internal sealed class AudioOutput
    {
        public const int SampleRate = 48000;
        public const int FramesPerBuffer = SampleRate / 50;   // 20ms
        const int BufferCount = 16;
        const int MaxQueued = 8;                             // これ以上たまったら捨てて遅延の伸びを防ぐ

        readonly string label;
        IntPtr handle;
        readonly List<PcmBuffer> buffers = new List<PcmBuffer>();
        readonly Stack<PcmBuffer> free = new Stack<PcmBuffer>();
        readonly List<PcmBuffer> busy = new List<PcmBuffer>();
        readonly short[] samples = new short[FramesPerBuffer * 2];
        double gain = -1;                                     // -1 = 未初期化（最初の書き込みでミュート状態に合わせる）

        public volatile bool Muted;
        public volatile float Volume = 1f;                   // 1 = 100%。1 を超える分は増幅（はみ出た分は頭打ち）
        public volatile int Peak;
        public volatile int Dropped;

        public AudioOutput(int device, string label)
        {
            this.label = label;
            WinMM.WAVEFORMATEX fmt = WinMM.PcmFormat(SampleRate, 2);
            PcmBuffer.Check(WinMM.waveOutOpen(out handle, device, ref fmt, IntPtr.Zero, IntPtr.Zero, WinMM.CALLBACK_NULL),
                label + "のデバイスを開く");
            for (int i = 0; i < BufferCount; i++)
            {
                PcmBuffer b = new PcmBuffer(FramesPerBuffer * 4);
                buffers.Add(b);
                free.Push(b);
            }
        }

        public int Pending { get { return busy.Count; } }

        public void Reclaim()
        {
            for (int i = busy.Count - 1; i >= 0; i--)
            {
                PcmBuffer b = busy[i];
                if (!b.IsDone) continue;
                WinMM.waveOutUnprepareHeader(handle, b.Header, PcmBuffer.HeaderSize);
                busy.RemoveAt(i);
                free.Push(b);
            }
        }

        // モノラルの音を左右両方に書き込む。ミュート中も無音を送り続けて、受け手側のストリームを途切れさせない。
        // 切り替え時のプツッというノイズを避けるため、1バッファ（20ms）かけてゲイン（ミュート×音量）を変える。
        public void WriteMono(short[] mono, int frames)
        {
            double target = Muted ? 0.0 : Volume;
            if (gain < 0) gain = target;
            double step = (target - gain) / frames;
            double g = gain;
            int peak = 0;
            for (int i = 0; i < frames; i++)
            {
                g += step;
                int v = (int)(mono[i] * g);
                if (v > 32767) v = 32767;
                else if (v < -32768) v = -32768;
                samples[2 * i] = (short)v;
                samples[2 * i + 1] = (short)v;
                if (v < 0) v = -v;
                if (v > peak) peak = v;
            }
            gain = target;
            Peak = peak;
            Submit(samples, frames);
        }

        // ステレオの音をそのまま書き込む（モニター用。ミュートは効かない）
        public void WriteStereo(short[] stereo, int frames)
        {
            int peak = 0;
            for (int i = 0; i < frames * 2; i++)
            {
                int v = stereo[i] < 0 ? -stereo[i] : stereo[i];
                if (v > peak) peak = v;
            }
            Peak = peak;
            Submit(stereo, frames);
        }

        void Submit(short[] data, int frames)
        {
            if (free.Count == 0 || busy.Count >= MaxQueued)
            {
                Dropped++;
                return;
            }
            PcmBuffer ob = free.Pop();
            Marshal.Copy(data, 0, ob.Data, frames * 2);
            ob.Reset(frames * 4);
            PcmBuffer.Check(WinMM.waveOutPrepareHeader(handle, ob.Header, PcmBuffer.HeaderSize), label + "の再生バッファの準備");
            PcmBuffer.Check(WinMM.waveOutWrite(handle, ob.Header, PcmBuffer.HeaderSize), label + "への送信（デバイスが外れた可能性があります）");
            busy.Add(ob);
        }

        public void Close()
        {
            if (handle != IntPtr.Zero)
            {
                WinMM.waveOutReset(handle);
                foreach (PcmBuffer b in buffers) WinMM.waveOutUnprepareHeader(handle, b.Header, PcmBuffer.HeaderSize);
                WinMM.waveOutClose(handle);
                handle = IntPtr.Zero;
            }
            foreach (PcmBuffer b in buffers) b.Free();
            buffers.Clear();
            free.Clear();
            busy.Clear();
            Peak = 0;
        }
    }
}
