namespace Vedar.Core.Audio;

/// <summary>
/// Is there a human voice in the sound at all? Voiced speech (vowels and voiced consonants) repeats itself at the
/// pitch of the voice, 70–400 times a second; breath, clicks, rustling, rumble and silence do not. Whisper turns such
/// sounds into confident "words" ("Слъпи.", "Ви.", "Към пършата."); this tells them apart without any model.
/// Pure arithmetic on 16 kHz mono samples; a few milliseconds per second of sound.
/// </summary>
public static class VoicePresence
{
    public const int SampleRate = 16000;

    /// <summary>Normalized autocorrelation at the pitch period above which a frame counts as voiced.</summary>
    public const double PeriodicityThreshold = 0.6;

    /// <summary>A frame must be this much louder than the quiet part of the recording (dB).</summary>
    public const double AboveBackgroundDb = 9;

    private const int FrameSamples = 640; // 40 ms: two periods of the lowest voice
    private const int HopSamples = 320;   // 20 ms
    private const int MinLag = SampleRate / 400;
    private const int MaxLag = SampleRate / 70;
    private const double QuietestDbfs = -60;

    /// <summary>Milliseconds of the sound that are voiced: periodic at a voice pitch and above the background.</summary>
    public static int VoicedMilliseconds(ReadOnlySpan<float> samples)
    {
        if (samples.Length < FrameSamples)
        {
            return 0;
        }

        var x = BandPass(samples);
        var frames = (x.Length - FrameSamples) / HopSamples + 1;
        var levels = new double[frames];
        for (int k = 0; k < frames; k++)
        {
            levels[k] = LevelDb(x.AsSpan(k * HopSamples, FrameSamples));
        }

        var sorted = (double[])levels.Clone();
        Array.Sort(sorted);
        var background = sorted[(int)(0.1 * (sorted.Length - 1))];
        var gate = Math.Max(background + AboveBackgroundDb, QuietestDbfs);
        var voiced = 0;
        for (int k = 0; k < frames; k++)
        {
            if (levels[k] >= gate && Periodicity(x.AsSpan(k * HopSamples, FrameSamples)) >= PeriodicityThreshold)
            {
                voiced++;
            }
        }

        return voiced * HopSamples * 1000 / SampleRate;
    }

    /// <summary>
    /// The strongest repetition of the frame at a voice pitch: the highest local peak of the normalized
    /// autocorrelation between 2.5 and 14 ms. A smooth low rumble has no peak there (its correlation only falls).
    /// </summary>
    internal static double Periodicity(ReadOnlySpan<float> frame)
    {
        var maxLag = Math.Min(MaxLag + 1, frame.Length / 2);
        double previous = Correlation(frame, MinLag - 1), current = Correlation(frame, MinLag), best = 0;
        for (int lag = MinLag; lag < maxLag; lag++)
        {
            var next = Correlation(frame, lag + 1);
            if (current > best && current >= previous && current >= next)
            {
                best = current;
            }

            previous = current;
            current = next;
        }

        return best;
    }

    private static double Correlation(ReadOnlySpan<float> x, int lag)
    {
        double sum = 0, e1 = 0, e2 = 0;
        for (int n = 0; n + lag < x.Length; n++)
        {
            double a = x[n], b = x[n + lag];
            sum += a * b;
            e1 += a * a;
            e2 += b * b;
        }

        return e1 <= 1e-12 || e2 <= 1e-12 ? 0 : sum / Math.Sqrt(e1 * e2);
    }

    private static double LevelDb(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (var s in x)
        {
            sum += s * s;
        }

        return 10 * Math.Log10(sum / x.Length + 1e-12);
    }

    /// <summary>70 Hz – 1 kHz (second-order Butterworth sections): the voice's pitch and first harmonics.</summary>
    private static float[] BandPass(ReadOnlySpan<float> input)
    {
        var y = new float[input.Length];
        var high = Biquad.HighPass(70, SampleRate);
        var low = Biquad.LowPass(1000, SampleRate);
        for (int n = 0; n < input.Length; n++)
        {
            y[n] = (float)low.Process(high.Process(input[n]));
        }

        return y;
    }

    private struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

        public static Biquad LowPass(double f0, double fs)
        {
            var (cos, alpha) = Coefficients(f0, fs);
            return Normalize((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public static Biquad HighPass(double f0, double fs)
        {
            var (cos, alpha) = Coefficients(f0, fs);
            return Normalize((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        private static (double Cos, double Alpha) Coefficients(double f0, double fs)
        {
            var w0 = 2 * Math.PI * f0 / fs;
            return (Math.Cos(w0), Math.Sin(w0) / (2 * Math.Sqrt(0.5)));
        }

        private static Biquad Normalize(double b0, double b1, double b2, double a0, double a1, double a2) =>
            new() { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0 };

        public double Process(double x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }
}
