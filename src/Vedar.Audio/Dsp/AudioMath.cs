namespace Vedar.Audio.Dsp;

public static class AudioMath
{
    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    public static float Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (var s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        return peak;
    }

    /// <summary>Linear amplitude to dBFS (−100 for silence).</summary>
    public static float ToDecibels(float amplitude) => amplitude <= 1e-5f ? -100f : 20f * MathF.Log10(amplitude);

    public static void ApplyGain(Span<float> samples, float gain)
    {
        if (Math.Abs(gain - 1f) < 1e-4f)
        {
            return;
        }

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
        }
    }

    /// <summary>Interleaved multi-channel → mono by averaging.</summary>
    public static float[] DownmixToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 1)
        {
            return interleaved.ToArray();
        }

        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                sum += interleaved[f * channels + c];
            }

            mono[f] = sum / channels;
        }

        return mono;
    }

    public static float[] Pcm16ToFloat(ReadOnlySpan<byte> bytes)
    {
        var result = new float[bytes.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8)) / 32768f;
        }

        return result;
    }

    public static byte[] FloatToPcm16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            var v = (short)Math.Clamp((int)Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
            bytes[2 * i] = (byte)(v & 0xFF);
            bytes[2 * i + 1] = (byte)((v >> 8) & 0xFF);
        }

        return bytes;
    }
}
