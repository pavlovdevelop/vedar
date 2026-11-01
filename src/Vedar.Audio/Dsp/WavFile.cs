using System.Text;

namespace Vedar.Audio.Dsp;

public sealed record WavData(float[] Samples, int SampleRate, int Channels, int BitsPerSample)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);
}

/// <summary>Reads PCM 8/16/24/32-bit and IEEE float WAV (any channel count → mono); writes 16-bit mono PCM.</summary>
public static class WavFile
{
    public static WavData ReadMono(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadMono(stream);
    }

    public static WavData ReadMono(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        int channels = 0, rate = 0, bits = 0, format = 0;
        byte[]? data = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (id == "fmt ")
            {
                format = reader.ReadInt16();
                channels = reader.ReadInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bits = reader.ReadInt16();
                var remaining = size - 16;
                if (format == unchecked((short)0xFFFE) && remaining >= 10)
                {
                    reader.ReadInt16();
                    reader.ReadInt16();
                    reader.ReadInt32();
                    format = reader.ReadInt16(); // first two bytes of SubFormat GUID = format tag
                    remaining -= 10;
                }

                if (remaining > 0)
                {
                    reader.ReadBytes(remaining);
                }
            }
            else if (id == "data")
            {
                data = reader.ReadBytes(size);
            }
            else
            {
                reader.ReadBytes(size);
            }

            if ((size & 1) == 1 && stream.Position < stream.Length)
            {
                reader.ReadByte();
            }
        }

        if (data is null || channels == 0 || rate == 0)
        {
            throw new InvalidDataException("Missing fmt or data chunk.");
        }

        var interleaved = Decode(data, format, bits);
        return new WavData(AudioMath.DownmixToMono(interleaved, channels), rate, channels, bits);
    }

    private static float[] Decode(byte[] data, int format, int bits)
    {
        if (format == 3 && bits == 32)
        {
            var f = new float[data.Length / 4];
            Buffer.BlockCopy(data, 0, f, 0, f.Length * 4);
            return f;
        }

        if (format != 1)
        {
            throw new NotSupportedException($"WAV format {format} is not supported.");
        }

        switch (bits)
        {
            case 8:
                return data.Select(b => (b - 128) / 128f).ToArray();
            case 16:
                return AudioMath.Pcm16ToFloat(data);
            case 24:
            {
                var r = new float[data.Length / 3];
                for (int i = 0; i < r.Length; i++)
                {
                    int v = data[3 * i] | (data[3 * i + 1] << 8) | (data[3 * i + 2] << 16);
                    if ((v & 0x800000) != 0)
                    {
                        v |= unchecked((int)0xFF000000);
                    }

                    r[i] = v / 8388608f;
                }

                return r;
            }

            case 32:
            {
                var r = new float[data.Length / 4];
                for (int i = 0; i < r.Length; i++)
                {
                    r[i] = BitConverter.ToInt32(data, 4 * i) / 2147483648f;
                }

                return r;
            }

            default:
                throw new NotSupportedException($"{bits}-bit PCM is not supported.");
        }
    }

    public static void WriteMono16(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using var stream = File.Create(path);
        WriteMono16(stream, samples, sampleRate);
    }

    public static void WriteMono16(Stream stream, ReadOnlySpan<float> samples, int sampleRate)
    {
        var pcm = AudioMath.FloatToPcm16(samples);
        using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
    }
}
