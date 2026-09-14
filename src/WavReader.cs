using System;
using System.IO;
using System.Text;

namespace ChannelSwitcher
{
    // WAV ファイルを必要な範囲だけ読み出す。PCM 8/16/24/32bit と 32bit float に対応。
    public sealed class WavReader : IDisposable
    {
        const int WAVE_FORMAT_PCM = 1;
        const int WAVE_FORMAT_IEEE_FLOAT = 3;
        const int WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

        readonly FileStream stream;
        readonly long dataStart;
        readonly int blockAlign;
        readonly int bytesPerSample;
        readonly bool isFloat;
        byte[] raw = new byte[0];

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public int BitsPerSample { get; private set; }
        public bool IsFloat { get { return isFloat; } }
        public long TotalFrames { get; private set; }
        public double Duration { get { return (double)TotalFrames / SampleRate; } }

        public WavReader(string path)
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                BinaryReader br = new BinaryReader(stream);
                if (ReadId(br) != "RIFF") throw new InvalidDataException("WAV ファイルではありません（RIFF ヘッダがありません）。");
                br.ReadUInt32();
                if (ReadId(br) != "WAVE") throw new InvalidDataException("WAV ファイルではありません（WAVE ヘッダがありません）。");

                int tag = -1;
                long dataPos = -1, dataSize = 0;
                while (stream.Position + 8 <= stream.Length && (tag < 0 || dataPos < 0))
                {
                    string id = ReadId(br);
                    long size = br.ReadUInt32();
                    long next = stream.Position + size + (size & 1);
                    if (id == "fmt ")
                    {
                        tag = br.ReadUInt16();
                        Channels = br.ReadUInt16();
                        SampleRate = br.ReadInt32();
                        br.ReadInt32();
                        blockAlign = br.ReadUInt16();
                        BitsPerSample = br.ReadUInt16();
                        if (tag == WAVE_FORMAT_EXTENSIBLE && size >= 40)
                        {
                            br.ReadUInt16();   // cbSize
                            br.ReadUInt16();   // valid bits
                            br.ReadUInt32();   // channel mask
                            tag = br.ReadUInt16();   // SubFormat GUID の先頭が形式タグ
                        }
                    }
                    else if (id == "data")
                    {
                        dataPos = stream.Position;
                        dataSize = size;
                    }
                    if (next > stream.Length) break;
                    stream.Position = next;
                }

                if (tag < 0) throw new InvalidDataException("fmt チャンクが見つかりません。");
                if (dataPos < 0) throw new InvalidDataException("data チャンクが見つかりません。");
                isFloat = tag == WAVE_FORMAT_IEEE_FLOAT;
                bool supported = (tag == WAVE_FORMAT_PCM && (BitsPerSample == 8 || BitsPerSample == 16 || BitsPerSample == 24 || BitsPerSample == 32))
                              || (isFloat && BitsPerSample == 32);
                if (!supported || Channels < 1 || SampleRate <= 0 || blockAlign <= 0)
                    throw new InvalidDataException("対応していない WAV 形式です（形式タグ " + tag + "、" + BitsPerSample + "bit）。PCM または 32bit float の WAV を使ってください。");

                bytesPerSample = BitsPerSample / 8;
                if (dataSize <= 0 || dataPos + dataSize > stream.Length) dataSize = stream.Length - dataPos;   // 録音途中のファイルなど
                dataStart = dataPos;
                TotalFrames = dataSize / blockAlign;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        static string ReadId(BinaryReader br)
        {
            return Encoding.ASCII.GetString(br.ReadBytes(4));
        }

        public string Description
        {
            get
            {
                TimeSpan t = TimeSpan.FromSeconds(Duration);
                return SampleRate + "Hz / " + Channels + "ch / " + BitsPerSample + "bit" + (isFloat ? " float" : "")
                    + " / " + string.Format("{0:00}:{1:00}", (int)t.TotalMinutes, t.Seconds);
            }
        }

        // startFrame から最大 count フレームを読み、左右を -1〜1 の値で返す。モノラルなら左右に同じ値を入れる。
        public int Read(long startFrame, int count, float[] left, float[] right)
        {
            if (startFrame >= TotalFrames) return 0;
            if (startFrame + count > TotalFrames) count = (int)(TotalFrames - startFrame);
            int bytes = count * blockAlign;
            if (raw.Length < bytes) raw = new byte[bytes];

            stream.Position = dataStart + startFrame * blockAlign;
            int got = 0;
            while (got < bytes)
            {
                int n = stream.Read(raw, got, bytes - got);
                if (n <= 0) break;
                got += n;
            }
            int frames = got / blockAlign;

            for (int f = 0; f < frames; f++)
            {
                int off = f * blockAlign;
                left[f] = Sample(off);
                right[f] = Channels > 1 ? Sample(off + bytesPerSample) : left[f];
            }
            return frames;
        }

        float Sample(int off)
        {
            switch (BitsPerSample)
            {
                case 8: return (raw[off] - 128) / 128f;
                case 16: return BitConverter.ToInt16(raw, off) / 32768f;
                case 24: return (raw[off] | (raw[off + 1] << 8) | ((sbyte)raw[off + 2] << 16)) / 8388608f;
                default: return isFloat ? BitConverter.ToSingle(raw, off) : BitConverter.ToInt32(raw, off) / 2147483648f;
            }
        }

        public void Dispose()
        {
            stream.Dispose();
        }
    }
}
