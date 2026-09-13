using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ChannelSwitcher
{
    // Windows 標準の音声API（winmm.dll）の宣言。外部ライブラリなしで録音・再生するために使う。
    internal static class WinMM
    {
        public const int CALLBACK_NULL = 0;
        public const int CALLBACK_EVENT = 0x50000;
        public const int WHDR_DONE = 0x1;
        public const short WAVE_FORMAT_PCM = 1;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WAVEFORMATEX
        {
            public short wFormatTag;
            public short nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags;
            public int dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WAVEINCAPS
        {
            public short wMid;
            public short wPid;
            public int vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public int dwFormats;
            public short wChannels;
            public short wReserved1;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WAVEOUTCAPS
        {
            public short wMid;
            public short wPid;
            public int vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public int dwFormats;
            public short wChannels;
            public short wReserved1;
            public int dwSupport;
        }

        [DllImport("winmm.dll")] public static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "waveInGetDevCapsW", CharSet = CharSet.Unicode)]
        public static extern int waveInGetDevCaps(IntPtr uDeviceID, ref WAVEINCAPS caps, int cbwic);
        [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr phwi, int uDeviceID, ref WAVEFORMATEX fmt, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);
        [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] public static extern int waveInStart(IntPtr hwi);
        [DllImport("winmm.dll")] public static extern int waveInStop(IntPtr hwi);
        [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr hwi);
        [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr hwi);
        [DllImport("winmm.dll", EntryPoint = "waveInGetErrorTextW", CharSet = CharSet.Unicode)]
        public static extern int waveInGetErrorText(int mmrError, StringBuilder text, int cchText);

        [DllImport("winmm.dll")] public static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)]
        public static extern int waveOutGetDevCaps(IntPtr uDeviceID, ref WAVEOUTCAPS caps, int cbwoc);
        [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr phwo, int uDeviceID, ref WAVEFORMATEX fmt, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);
        [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr hwo);

        public static string ErrorText(int code)
        {
            StringBuilder sb = new StringBuilder(256);
            if (waveInGetErrorText(code, sb, sb.Capacity) == 0 && sb.Length > 0)
                return sb.ToString() + " (MMRESULT=" + code + ")";
            return "MMRESULT=" + code;
        }

        public static WAVEFORMATEX PcmFormat(int sampleRate, int channels)
        {
            WAVEFORMATEX f = new WAVEFORMATEX();
            f.wFormatTag = WAVE_FORMAT_PCM;
            f.nChannels = (short)channels;
            f.nSamplesPerSec = sampleRate;
            f.wBitsPerSample = 16;
            f.nBlockAlign = (short)(channels * 2);
            f.nAvgBytesPerSec = sampleRate * channels * 2;
            f.cbSize = 0;
            return f;
        }
    }
}
