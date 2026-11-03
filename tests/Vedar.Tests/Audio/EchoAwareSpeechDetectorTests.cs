using Vedar.Audio.Dsp;
using Vedar.Speech.Vad;
using Vedar.Tests.Support;

namespace Vedar.Tests.Audio;

/// <summary>
/// Simulated rooms with real human voices (FLEURS): "VEDAR" is one set of speakers played through a room with
/// reverberation and delay, "the user" another speaker close to the microphone. No models are needed.
/// </summary>
public class EchoAwareSpeechDetectorTests(ITestOutputHelper output)
{
    private const int Rate = 16000;

    private static float[] Voice(params int[] files)
    {
        var all = new List<float>();
        foreach (var i in files)
        {
            var wav = WavFile.ReadMono(Path.Combine(TestModels.TestDataDir, "fleurs", $"fleurs_bg_{i}.wav"));
            all.AddRange(wav.SampleRate == Rate ? wav.Samples : StreamingResampler.Resample(wav.Samples, wav.SampleRate, Rate));
            all.AddRange(new float[Rate / 4]); // a short pause between sentences, as VEDAR makes
        }

        return all.ToArray();
    }

    /// <summary>Scales the signal so that its loud (speech) part has the given RMS level.</summary>
    private static float[] AtLevel(float[] x, double dbfs)
    {
        var all = Enumerable.Range(0, x.Length / 480).Select(i => AudioMath.Rms(x.AsSpan(i * 480, 480))).ToArray();
        var frames = all.Where(r => r > all.Max() * 0.05).ToArray();
        var speechRms = Math.Sqrt(frames.Select(r => (double)r * r).Average());
        var gain = (float)(Math.Pow(10, dbfs / 20) / speechRms);
        return x.Select(s => s * gain).ToArray();
    }

    /// <summary>A loudspeaker in a room: direct sound plus decaying reflections (RT60), then the system delay.</summary>
    private static float[] Room(float[] dry, double rt60, int delayMs, int seed)
    {
        var random = new Random(seed);
        var taps = new List<(int Delay, float Gain)> { (0, 1f) };
        var length = (int)(rt60 * Rate);
        for (int i = 0; i < 60; i++)
        {
            var d = random.Next(Rate / 200, length);
            var decay = Math.Pow(10, -3.0 * d / length); // −60 dB at RT60
            taps.Add((d, (float)(decay * (random.NextDouble() * 2 - 1) * 0.35)));
        }

        var offset = delayMs * Rate / 1000;
        var wet = new float[dry.Length + length + offset];
        foreach (var (delay, gain) in taps)
        {
            for (int n = 0; n < dry.Length; n++)
            {
                wet[n + delay + offset] += dry[n] * gain;
            }
        }

        return wet;
    }

    /// <summary>Where sustained speech starts (s): three frames in a row within 20 dB of the loudest.</summary>
    private static double Onset(float[] x)
    {
        var frames = Enumerable.Range(0, x.Length / 480).Select(i => AudioMath.ToDecibels(AudioMath.Rms(x.AsSpan(i * 480, 480)))).ToArray();
        var peak = frames.Max();
        for (int k = 0; k + 2 < frames.Length; k++)
        {
            if (frames[k] > peak - 20 && frames[k + 1] > peak - 20 && frames[k + 2] > peak - 20)
            {
                return k * 0.03;
            }
        }

        return 0;
    }

    private static float[] Mix(int length, params (float[] Signal, int AtSample)[] parts)
    {
        var mix = new float[length];
        var noise = new Random(7);
        for (int n = 0; n < length; n++)
        {
            mix[n] = (float)((noise.NextDouble() * 2 - 1) * 0.0017); // ≈ −60 dBFS background
        }

        foreach (var (signal, at) in parts)
        {
            for (int n = 0; n < signal.Length && at + n < length; n++)
            {
                mix[at + n] += signal[n];
            }
        }

        return mix;
    }

    /// <summary>Feeds the microphone signal; returns the time of the first detection (s) or null.</summary>
    private static double? Run(EchoAwareSpeechDetector detector, float[] reference, float[] mic, double referenceAtMs = 0)
    {
        if (reference.Length > 0)
        {
            detector.AddReference(reference, Rate, referenceAtMs);
        }

        for (int k = 0; (k + 1) * EchoAwareSpeechDetector.FrameSamples <= mic.Length; k++)
        {
            if (detector.Process(mic.AsSpan(k * EchoAwareSpeechDetector.FrameSamples, EchoAwareSpeechDetector.FrameSamples), k * 30.0))
            {
                return k * 0.03;
            }
        }

        return null;
    }

    [Theory]
    [InlineData(-30, 0.3, 120)]
    [InlineData(-20, 0.5, 120)]
    [InlineData(-12, 0.5, 250)]
    [InlineData(-6, 0.6, 180)]
    [InlineData(-3, 0.4, 80)]
    public void VEDARs_own_voice_from_the_loudspeakers_is_never_taken_for_the_user(double echoDb, double rt60, int delayMs)
    {
        var vedar = AtLevel(Voice(1, 2, 3), -20);
        var echo = AtLevel(Room(vedar, rt60, delayMs, seed: 1), -20 + echoDb);
        var mic = Mix(echo.Length + Rate, (echo, 0));
        var detector = new EchoAwareSpeechDetector();
        var detected = Run(detector, vedar, mic);
        output.WriteLine($"echo {echoDb} dB, RT60 {rt60} s, delay {delayMs} ms → detected: {detected?.ToString("F2") ?? "no"}; learned gain {detector.EchoGainDb:F1} dB, delay {detector.DelayMs:F0} ms");
        Assert.Null(detected);
    }

    [Theory]
    [InlineData(-15, -26, 0.6)] // speakers across the room, the user near the microphone
    [InlineData(-10, -24, 0.6)]
    [InlineData(-20, -34, 1.2)] // a quiet user, quiet speakers: only the louder syllables stand out
    public void The_user_speaking_over_VEDARs_voice_is_noticed_quickly(double echoDb, double userDb, double withinSeconds)
    {
        var vedar = AtLevel(Voice(1, 2, 3), -20);
        var echo = AtLevel(Room(vedar, 0.5, 150, seed: 2), -20 + echoDb);
        var user = AtLevel(Voice(4), userDb);
        const double userStart = 6.0;
        var mic = Mix(echo.Length + Rate, (echo, 0), (user, (int)(userStart * Rate)));
        var detector = new EchoAwareSpeechDetector();
        var detected = Run(detector, vedar, mic);
        var onset = userStart + Onset(user);
        output.WriteLine($"echo {-20 + echoDb} dBFS, user {userDb} dBFS → detected at {detected?.ToString("F2") ?? "never"} s (speech from {onset:F2} s)");
        Assert.NotNull(detected);
        Assert.InRange(detected!.Value, onset - 0.1, onset + withinSeconds);
    }

    [Fact]
    public void With_headphones_any_speech_of_the_user_is_noticed()
    {
        var vedar = AtLevel(Voice(1, 2, 3), -20);
        var user = AtLevel(Voice(5), -32);
        const double userStart = 4.0;
        var mic = Mix(vedar.Length, (user, (int)(userStart * Rate))); // VEDAR is not heard at all
        var detected = Run(new EchoAwareSpeechDetector(), vedar, mic);
        var onset = userStart + Onset(user);
        output.WriteLine($"headphones → detected at {detected?.ToString("F2") ?? "never"} s (speech from {onset:F2} s)");
        Assert.NotNull(detected);
        Assert.InRange(detected!.Value, onset - 0.1, onset + 0.4);
    }

    [Fact]
    public void While_VEDAR_is_silent_the_user_is_noticed_at_once()
    {
        var user = AtLevel(Voice(4), -30);
        var mic = Mix(5 * Rate, (user, 2 * Rate));
        var detected = Run(new EchoAwareSpeechDetector(), [], mic);
        var onset = 2.0 + Onset(user);
        Assert.NotNull(detected);
        Assert.InRange(detected!.Value, onset - 0.1, onset + 0.3);
    }
}

