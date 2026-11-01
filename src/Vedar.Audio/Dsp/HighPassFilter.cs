namespace Vedar.Audio.Dsp;

/// <summary>Second-order Butterworth high-pass (biquad). Removes mains hum, desk rumble and DC offset.</summary>
public sealed class HighPassFilter
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    public HighPassFilter(int sampleRate, double cutoffHz = 100)
    {
        double w0 = 2 * Math.PI * cutoffHz / sampleRate;
        double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));
        double a0 = 1 + alpha;
        _b0 = (1 + cos) / 2 / a0;
        _b1 = -(1 + cos) / a0;
        _b2 = (1 + cos) / 2 / a0;
        _a1 = -2 * cos / a0;
        _a2 = (1 - alpha) / a0;
    }

    public void Process(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples[i];
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            samples[i] = (float)y;
        }
    }

    public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;
}
