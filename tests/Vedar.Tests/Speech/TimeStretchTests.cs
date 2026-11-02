using Vedar.Audio.Dsp;

namespace Vedar.Tests.Speech;

/// <summary>Faster speech without a change of pitch (WSOLA), used for speaking rates above 1×.</summary>
public class TimeStretchTests
{
    private const int Rate = 22050;

    /// <summary>A voiced-speech-like test signal: 120 Hz fundamental with decaying harmonics and slow vibrato-free AM.</summary>
    private static float[] Voice(double seconds, double f0 = 120)
    {
        var n = (int)(seconds * Rate);
        var x = new float[n];
        for (int i = 0; i < n; i++)
        {
            var t = i / (double)Rate;
            double v = 0;
            for (int h = 1; h <= 8; h++)
            {
                v += Math.Sin(2 * Math.PI * f0 * h * t) / h;
            }

            x[i] = (float)(0.3 * v * (0.75 + 0.25 * Math.Sin(2 * Math.PI * 3 * t)));
        }

        return x;
    }

    /// <summary>Fundamental frequency from the autocorrelation peak between 60 and 400 Hz.</summary>
    private static double Pitch(ReadOnlySpan<float> x)
    {
        int bestLag = 0;
        double best = double.NegativeInfinity;
        for (int lag = Rate / 400; lag <= Rate / 60; lag++)
        {
            double sum = 0;
            for (int i = 0; i + lag < x.Length; i++)
            {
                sum += x[i] * x[i + lag];
            }

            sum /= x.Length - lag;
            if (sum > best)
            {
                best = sum;
                bestLag = lag;
            }
        }

        return Rate / (double)bestLag;
    }

    private static float MaxStep(ReadOnlySpan<float> x)
    {
        float max = 0;
        for (int i = 1; i < x.Length; i++)
        {
            max = Math.Max(max, Math.Abs(x[i] - x[i - 1]));
        }

        return max;
    }

    [Theory]
    [InlineData(1.3)]
    [InlineData(1.6)]
    [InlineData(2.0)]
    [InlineData(0.7)]
    public void Duration_changes_by_exactly_the_requested_factor(double speed)
    {
        var input = Voice(2.0);
        var output = TimeStretch.ChangeTempo(input, speed, Rate);
        Assert.Equal((int)Math.Round(input.Length / speed), output.Length);
    }

    [Theory]
    [InlineData(1.6, 120)]
    [InlineData(2.0, 120)]
    [InlineData(2.0, 95)]
    public void Pitch_and_loudness_stay_the_same(double speed, double f0)
    {
        var input = Voice(2.0, f0);
        var output = TimeStretch.ChangeTempo(input, speed, Rate);
        // Steady middle part (edges fade in/out by design).
        var middle = output.AsSpan(output.Length / 4, output.Length / 2);
        Assert.InRange(Pitch(middle), f0 * 0.98, f0 * 1.02);
        var rmsIn = AudioMath.Rms(input.AsSpan(input.Length / 4, input.Length / 2));
        Assert.InRange(AudioMath.Rms(middle), rmsIn * 0.85, rmsIn * 1.15);
    }

    [Fact]
    public void Output_has_no_clicks_at_frame_joins()
    {
        var input = Voice(1.5);
        var output = TimeStretch.ChangeTempo(input, 2.0, Rate);
        // Waveform-similarity alignment keeps the joins smooth: no sample step much larger than in the original.
        Assert.True(MaxStep(output) <= MaxStep(input) * 1.25f, $"max step {MaxStep(output):F3} vs {MaxStep(input):F3}");
        Assert.True(AudioMath.Peak(output) <= AudioMath.Peak(input) * 1.1f);
    }

    [Fact]
    public void Normal_speed_and_empty_input_are_passed_through()
    {
        var input = Voice(0.5);
        Assert.Equal(input, TimeStretch.ChangeTempo(input, 1.0, Rate));
        Assert.Empty(TimeStretch.ChangeTempo([], 2.0, Rate));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeStretch.ChangeTempo(input, 10, Rate));
    }

    [Fact]
    public void Very_short_sounds_are_handled()
    {
        var output = TimeStretch.ChangeTempo(Voice(0.01), 2.0, Rate);
        Assert.Equal((int)Math.Round(0.01 * Rate / 2.0), output.Length);
        Assert.All(output, s => Assert.True(float.IsFinite(s)));
    }
}
