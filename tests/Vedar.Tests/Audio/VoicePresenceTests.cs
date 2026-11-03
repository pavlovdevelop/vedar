using Vedar.Audio.Dsp;
using Vedar.Core.Audio;
using Vedar.Tests.Support;

namespace Vedar.Tests.Audio;

/// <summary>
/// The voice check that keeps Whisper's invented "words" from noises out: real human voices (FLEURS) must be found,
/// also in short pieces and in noise; breath, clicks, rumble, hum and silence must not. Padded like a real
/// utterance: 0.6 s before, the 1.4 s end-of-speech pause after, a quiet room (−60 dBFS).
/// </summary>
public class VoicePresenceTests(ITestOutputHelper output)
{
    private const int Rate = 16000;
    private const int MinVoicedMs = 80; // AssistantOrchestrator.MinVoicedMs

    private static float[] Fleurs(int i)
    {
        var wav = WavFile.ReadMono(Path.Combine(TestModels.TestDataDir, "fleurs", $"fleurs_bg_{i}.wav"));
        return wav.SampleRate == Rate ? wav.Samples : StreamingResampler.Resample(wav.Samples, wav.SampleRate, Rate);
    }

    private static float[] Scale(float[] x, double dbfs)
    {
        var g = (float)(Math.Pow(10, dbfs / 20) / Math.Max(AudioMath.Rms(x), 1e-9f));
        return x.Select(v => v * g).ToArray();
    }

    private static float[] Room(float[] sound, Random random, double noiseDb = -60)
    {
        var x = new float[(int)(0.6 * Rate) + sound.Length + (int)(1.4 * Rate)];
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = (float)((random.NextDouble() * 2 - 1) * Math.Pow(10, noiseDb / 20) * 1.7);
        }

        for (int i = 0; i < sound.Length; i++)
        {
            x[(int)(0.6 * Rate) + i] += sound[i];
        }

        return x;
    }

    private static float[] Noise(double seconds, double db, bool pink, Random random)
    {
        var x = new float[(int)(seconds * Rate)];
        double p0 = 0, p1 = 0, p2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            var w = random.NextDouble() * 2 - 1;
            x[i] = (float)(pink ? (p0 = 0.99765 * p0 + w * 0.0990460) + (p1 = 0.96300 * p1 + w * 0.2965164) + (p2 = 0.57000 * p2 + w * 1.0526913) + w * 0.1848 : w);
        }

        return Scale(x, db);
    }

    private static float[] Swell(float[] x)
    {
        for (int i = 0; i < x.Length; i++)
        {
            var t = i / (double)x.Length;
            x[i] *= (float)Math.Min(1, Math.Min(t / 0.35, (1 - t) / 0.5));
        }

        return x;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void A_real_human_voice_is_found_in_whole_sentences_and_in_short_pieces(int file)
    {
        var random = new Random(file);
        var speech = Fleurs(file);
        var whole = VoicePresence.VoicedMilliseconds(Room(speech, random));
        Assert.True(whole > 1000, $"whole sentence: {whole} ms");

        // Short pieces where the speech is loud (like a one-word reply), quiet and in noise.
        var found = 0;
        var pieces = 0;
        for (int start = Rate / 2; start + Rate * 45 / 100 < speech.Length; start += Rate)
        {
            var piece = speech.AsSpan(start, Rate * 45 / 100).ToArray();
            if (AudioMath.ToDecibels(AudioMath.Rms(piece)) < AudioMath.ToDecibels(AudioMath.Rms(speech)) - 6)
            {
                continue; // a pause between words
            }

            foreach (var x in new[]
                     {
                         Room(Scale(piece, -30), random),
                         Room(Scale(piece, -45), random, -70),
                         Mix(Room(Scale(piece, -30), random, -90), Noise(2.45, -38, pink: true, random)),
                     })
            {
                pieces++;
                found += VoicePresence.VoicedMilliseconds(x) >= MinVoicedMs ? 1 : 0;
            }
        }

        output.WriteLine($"fleurs_bg_{file}: whole {whole} ms voiced; short pieces with a voice found: {found}/{pieces}");
        Assert.True(pieces >= 6);
        Assert.True(found >= pieces - 1, $"{found}/{pieces}");
    }

    private static float[] Mix(float[] a, float[] b)
    {
        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            a[i] += b[i];
        }

        return a;
    }

    public static TheoryData<string> Noises => ["silence", "white noise", "loud white noise", "pink noise", "breath", "keyboard clicks", "mains hum", "breath over hum"];

    [Theory]
    [MemberData(nameof(Noises))]
    public void Noises_that_Whisper_turns_into_words_have_no_voice(string kind)
    {
        for (int seed = 0; seed < 10; seed++)
        {
            var random = new Random(seed);
            float[] Hum(double seconds) => Scale(Enumerable.Range(0, (int)(seconds * Rate)).Select(n => (float)new[] { 50.0, 100, 150, 200 }.Select((f, k) => Math.Sin(2 * Math.PI * f * n / Rate + k) / (k + 1)).Sum()).ToArray(), -40);
            float[] Clicks()
            {
                var c = new float[2 * Rate];
                for (int t = 800; t < c.Length - 400; t += random.Next(1200, 4000))
                {
                    for (int i = 0; i < 300; i++)
                    {
                        c[t + i] += (float)((random.NextDouble() * 2 - 1) * Math.Exp(-i / 30.0) * 0.3);
                    }
                }

                return c;
            }

            var sound = kind switch
            {
                "silence" => Room(new float[Rate], random),
                "white noise" => Room(Noise(0.6, -40, pink: false, random), random),
                "loud white noise" => Room(Noise(1.5, -25, pink: false, random), random),
                "pink noise" => Room(Noise(1.0, -35, pink: true, random), random),
                "breath" => Room(Swell(Noise(0.8, -36, pink: true, random)), random),
                "keyboard clicks" => Room(Clicks(), random),
                "mains hum" => Room(Hum(2.0), random),
                _ => Mix(Room(Swell(Noise(0.8, -36, pink: true, random)), random, -90), Hum(2.8)),
            };
            var voiced = VoicePresence.VoicedMilliseconds(sound);
            Assert.True(voiced < MinVoicedMs, $"{kind} (seed {seed}): {voiced} ms of 'voice'");
        }
    }

    [Fact]
    public void A_sustained_vowel_is_voice_for_about_its_length()
    {
        var vowel = TestAudio.Vowel(1.0f); // 0.6 s of vowel in the middle
        var voiced = VoicePresence.VoicedMilliseconds(Room(vowel, new Random(1)));
        Assert.InRange(voiced, 500, 700);
        Assert.Equal(0, VoicePresence.VoicedMilliseconds(new float[100]));
    }
}
