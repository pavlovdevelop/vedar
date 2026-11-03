using Vedar.Audio.Dsp;
using Vedar.Speech.Vad;

/// <summary>
/// Simulated rooms with real human voices (FLEURS): how often VEDAR's own voice from the loudspeakers would be
/// taken for the user (false alarms), and how fast the user speaking over VEDAR is noticed.
/// </summary>
internal static class BargeInBench
{
    private const int Rate = 16000;

    public static void Run(string dir, int voices)
    {
        var files = Directory.EnumerateFiles(dir, "*.wav").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var rnd = new Random(11);
        var load = new Dictionary<string, float[]>();
        float[] Load(string f)
        {
            if (!load.TryGetValue(f, out var x))
            {
                var wav = WavFile.ReadMono(f);
                x = wav.SampleRate == Rate ? wav.Samples : StreamingResampler.Resample(wav.Samples, wav.SampleRate, Rate);
                load[f] = x;
            }

            return x;
        }

        var options = new (string Name, EchoAwareSpeechDetector.Options O)[]
        {
            ("default", new()),
        };

        // Scenarios: VEDAR = 3 sentences of one set of speakers; the user = another file.
        var scenarios = new List<(float[] Vedar, float[] User, int Seed)>();
        for (int v = 0; v < voices; v++)
        {
            var pick = Enumerable.Range(0, 4).Select(_ => files[rnd.Next(files.Count)]).ToArray();
            var vedar = Concat(Load(pick[0]), new float[Rate / 4], Load(pick[1]), new float[Rate / 4], Load(pick[2]));
            scenarios.Add((AtLevel(vedar, -20), Load(pick[3]), v));
        }

        foreach (var (name, o) in options)
        {
            int falseAlarms = 0, echoRuns = 0;
            double echoMinutes = 0;
            foreach (var (vedar, _, seed) in scenarios)
            {
                foreach (var echoDb in new[] { -30.0, -20, -12, -6, -3, 0 })
                {
                    foreach (var (rt60, delay) in new[] { (0.3, 40), (0.6, 220), (0.9, 120) })
                    {
                        var echo = AtLevel(Room(vedar, rt60, delay, seed), -20 + echoDb);
                        var mic = Mix(echo.Length, seed, (echo, 0));
                        echoRuns++;
                        echoMinutes += mic.Length / (double)Rate / 60;
                        if (Detect(o, vedar, mic) is { } at)
                        {
                            falseAlarms++;
                            if (Environment.GetEnvironmentVariable("BENCH_VERBOSE") == "1")
                            {
                                Console.WriteLine($"   false alarm: voice {seed}, echo {echoDb} dB, RT60 {rt60}, delay {delay} ms at {at:F2} s");
                            }
                        }
                    }
                }
            }

            var results = new Dictionary<string, (int Hit, int Total, List<double> Latency)>();
            foreach (var (vedar, userVoice, seed) in scenarios)
            {
                foreach (var echoDb in new[] { -20.0, -12, -6, double.NegativeInfinity })
                {
                    foreach (var uer in new[] { -3.0, 3, 6, 10 })
                    {
                        var echoLevel = -20 + echoDb;
                        var userDb = double.IsNegativeInfinity(echoDb) ? -36 + uer : echoLevel + uer;
                        var echo = double.IsNegativeInfinity(echoDb) ? [] : AtLevel(Room(vedar, 0.5, 150, seed), echoLevel);
                        var user = AtLevel(userVoice, userDb);
                        var start = (int)(5.0 * Rate);
                        var onset = start + Onset(user);
                        var mic = Mix(Math.Max(vedar.Length, start + user.Length) + Rate, seed, (echo, 0), (user, start));
                        var hit = Detect(o, vedar, mic);
                        var key = double.IsNegativeInfinity(echoDb) ? $"headphones, user {userDb:F0} dBFS" : $"echo {echoDb:F0} dB, user +{uer:F0} dB over echo";
                        var r = results.TryGetValue(key, out var x) ? x : (Hit: 0, Total: 0, Latency: new List<double>());
                        r.Total++;
                        if (hit is { } t && t * Rate >= onset - Rate / 10 && t * Rate <= onset + Rate)
                        {
                            r.Hit++;
                            r.Latency.Add(t - onset / (double)Rate);
                        }
                        else if (Environment.GetEnvironmentVariable("BENCH_VERBOSE") == "1")
                        {
                            Console.WriteLine($"   missed: voice {seed}, {key}: detected {(hit is { } h ? h.ToString("F2") : "never")}, onset {onset / (double)Rate:F2}");
                        }

                        results[key] = r;
                    }
                }
            }

            Console.WriteLine($"\n{name}: false alarms {falseAlarms}/{echoRuns} runs ({falseAlarms / echoMinutes:F2} per minute of VEDAR's voice)");
            foreach (var (key, r) in results.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var lat = r.Latency.Count > 0 ? $"median {r.Latency.OrderBy(l => l).ElementAt(r.Latency.Count / 2) * 1000:F0} ms" : "-";
                Console.WriteLine($"   {key,-34} noticed within 1 s: {r.Hit}/{r.Total}  {lat}");
            }
        }
    }

    private static double? Detect(EchoAwareSpeechDetector.Options o, float[] reference, float[] mic)
    {
        var d = new EchoAwareSpeechDetector(o);
        d.AddReference(reference, Rate, 0);
        for (int k = 0; (k + 1) * 480 <= mic.Length; k++)
        {
            if (d.Process(mic.AsSpan(k * 480, 480), k * 30.0))
            {
                return k * 0.03;
            }
        }

        return null;
    }

    /// <summary>The start of sustained speech: three frames in a row within 20 dB of the loudest.</summary>
    private static int Onset(float[] x)
    {
        var frames = Enumerable.Range(0, x.Length / 480).Select(i => AudioMath.ToDecibels(AudioMath.Rms(x.AsSpan(i * 480, 480)))).ToArray();
        var peak = frames.Max();
        for (int k = 0; k + 2 < frames.Length; k++)
        {
            if (frames[k] > peak - 20 && frames[k + 1] > peak - 20 && frames[k + 2] > peak - 20)
            {
                return k * 480;
            }
        }

        return 0;
    }

    private static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    private static float[] AtLevel(float[] x, double dbfs)
    {
        var frames = Enumerable.Range(0, x.Length / 480).Select(i => AudioMath.Rms(x.AsSpan(i * 480, 480))).ToArray();
        var loud = frames.Where(r => r > frames.Max() * 0.05).ToArray();
        var speechRms = Math.Sqrt(loud.Select(r => (double)r * r).Average());
        var gain = (float)(Math.Pow(10, dbfs / 20) / speechRms);
        return x.Select(s => s * gain).ToArray();
    }

    private static float[] Room(float[] dry, double rt60, int delayMs, int seed)
    {
        var random = new Random(seed);
        var taps = new List<(int Delay, float Gain)> { (0, 1f) };
        var length = (int)(rt60 * Rate);
        for (int i = 0; i < 60; i++)
        {
            var d = random.Next(Rate / 200, length);
            taps.Add((d, (float)(Math.Pow(10, -3.0 * d / length) * (random.NextDouble() * 2 - 1) * 0.35)));
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

    private static float[] Mix(int length, int seed, params (float[] Signal, int At)[] parts)
    {
        var mix = new float[length];
        var noise = new Random(seed + 100);
        for (int n = 0; n < length; n++)
        {
            mix[n] = (float)((noise.NextDouble() * 2 - 1) * 0.0017);
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
}
