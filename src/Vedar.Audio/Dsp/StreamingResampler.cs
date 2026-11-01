namespace Vedar.Audio.Dsp;

/// <summary>
/// Streaming band-limited resampler (windowed-sinc interpolation, Blackman window).
/// Includes an anti-aliasing low-pass when downsampling (e.g. 48 kHz microphone → 16 kHz for Whisper).
/// Keeps state between calls so chunk boundaries are seamless.
/// </summary>
public sealed class StreamingResampler
{
    private const int HalfTaps = 16;
    private readonly double _step;      // input samples per output sample
    private readonly double _cutoff;    // normalized to input Nyquist
    private readonly List<float> _history = [];
    private double _position;           // position of next output sample, relative to _history[0]

    public StreamingResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputRate));
        }

        InputRate = inputRate;
        OutputRate = outputRate;
        _step = inputRate / (double)outputRate;
        _cutoff = Math.Min(1.0, outputRate / (double)inputRate) * 0.95;
        // Prime with silence so the first outputs have full filter support.
        _history.AddRange(new float[HalfTaps]);
        _position = HalfTaps;
    }

    public int InputRate { get; }
    public int OutputRate { get; }

    public float[] Process(ReadOnlySpan<float> input)
    {
        if (InputRate == OutputRate)
        {
            return input.ToArray();
        }

        foreach (var s in input)
        {
            _history.Add(s);
        }

        var output = new List<float>((int)(input.Length / _step) + 2);
        while (_position + HalfTaps < _history.Count)
        {
            output.Add(Interpolate(_position));
            _position += _step;
        }

        // Drop consumed history, keep enough for the filter.
        int drop = (int)Math.Floor(_position) - HalfTaps;
        if (drop > 0)
        {
            _history.RemoveRange(0, drop);
            _position -= drop;
        }

        return output.ToArray();
    }

    /// <summary>Resamples a complete buffer in one go.</summary>
    public static float[] Resample(ReadOnlySpan<float> input, int inputRate, int outputRate)
    {
        if (inputRate == outputRate)
        {
            return input.ToArray();
        }

        var r = new StreamingResampler(inputRate, outputRate);
        var head = r.Process(input);
        var tail = r.Process(new float[HalfTaps * 2]);
        var expected = (int)Math.Round(input.Length * (double)outputRate / inputRate);
        var all = new float[expected];
        var combined = head.Concat(tail).ToArray();
        Array.Copy(combined, all, Math.Min(expected, combined.Length));
        return all;
    }

    private float Interpolate(double pos)
    {
        int center = (int)Math.Floor(pos);
        double frac = pos - center;
        double sum = 0, weightSum = 0;
        for (int k = -HalfTaps + 1; k <= HalfTaps; k++)
        {
            int idx = center + k;
            if (idx < 0 || idx >= _history.Count)
            {
                continue;
            }

            double x = k - frac;
            double sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x * _cutoff) / (Math.PI * x * _cutoff);
            double w = 0.42 + 0.5 * Math.Cos(Math.PI * x / HalfTaps) + 0.08 * Math.Cos(2 * Math.PI * x / HalfTaps);
            if (Math.Abs(x) >= HalfTaps)
            {
                w = 0;
            }

            double coeff = sinc * w;
            sum += _history[idx] * coeff;
            weightSum += coeff;
        }

        return weightSum == 0 ? 0f : (float)(sum / weightSum);
    }
}
