using Vedar.Audio.Dsp;
using Vedar.Core.Abstractions;
using Vedar.Core.Audio;
using Vedar.Speech.Synthesis;

/// <summary>
/// How well <see cref="VoicePresence"/> tells a human voice from the noises Whisper turns into "words": short pieces
/// of real Bulgarian speech (FLEURS, many speakers and microphones), the same in noise, VEDAR's synthetic voice, and
/// noises (breath, clicks, rumble, hum, silence). Each sound is padded like a real utterance: 0.6 s before it and the
/// 1.4 s end-of-speech pause after it, in a quiet room (−60 dBFS).
/// </summary>
internal static class VoicingBench
{
    private const int Rate = 16000;

    public static async Task RunAsync(string fleursDir, PiperSpeechSynthesizer? tts)
    {
        var rnd = new Random(11);
        var files = Directory.GetFiles(fleursDir, "*.wav").OrderBy(f => f).ToArray();
        var speech = files.Select(f =>
        {
            var wav = WavFile.ReadMono(f);
            return wav.SampleRate == Rate ? wav.Samples : StreamingResampler.Resample(wav.Samples, wav.SampleRate, Rate);
        }).ToArray();

        float[] Room(float[] sound, double noiseDb = -60)
        {
            var x = new float[(int)(0.6 * Rate) + sound.Length + (int)(1.4 * Rate)];
            for (int i = 0; i < x.Length; i++)
            {
                x[i] = (float)((rnd.NextDouble() * 2 - 1) * Math.Pow(10, noiseDb / 20) * 1.7);
            }

            for (int i = 0; i < sound.Length; i++)
            {
                x[(int)(0.6 * Rate) + i] += sound[i];
            }

            return x;
        }

        // A "short word": 0.3–0.7 s of real speech, cut where the speech is (not in a pause).
        float[] ShortPiece(float[] s, double seconds)
        {
            var len = (int)(seconds * Rate);
            for (int attempt = 0; attempt < 50; attempt++)
            {
                var start = rnd.Next(0, Math.Max(1, s.Length - len));
                var piece = s.AsSpan(start, Math.Min(len, s.Length - start)).ToArray();
                if (AudioMath.ToDecibels(AudioMath.Rms(piece)) > -40)
                {
                    return piece;
                }
            }

            return s[..Math.Min(len, s.Length)];
        }

        float[] Scale(float[] s, double dbfs)
        {
            var rms = AudioMath.Rms(s);
            var g = (float)(Math.Pow(10, dbfs / 20) / Math.Max(rms, 1e-9));
            return s.Select(v => v * g).ToArray();
        }

        float[] Noise(double seconds, double db, string kind)
        {
            var x = new float[(int)(seconds * Rate)];
            double b = 0, p0 = 0, p1 = 0, p2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                var w = rnd.NextDouble() * 2 - 1;
                double v = kind switch
                {
                    "pink" => (p0 = 0.99765 * p0 + w * 0.0990460) + (p1 = 0.96300 * p1 + w * 0.2965164) + (p2 = 0.57000 * p2 + w * 1.0526913) + w * 0.1848,
                    "brown" => b = Math.Clamp(b + w * 0.02, -1, 1),
                    _ => w,
                };
                x[i] = (float)v;
            }

            return Scale(x, db);
        }

        float[] Envelope(float[] x, double attack, double release)
        {
            for (int i = 0; i < x.Length; i++)
            {
                var t = i / (double)Rate;
                var total = x.Length / (double)Rate;
                x[i] *= (float)Math.Min(1, Math.Min(t / attack, (total - t) / release));
            }

            return x;
        }

        float[] Clicks(double seconds)
        {
            var x = new float[(int)(seconds * Rate)];
            for (int t = 800; t < x.Length - 400; t += rnd.Next(1200, 4000))
            {
                for (int i = 0; i < 300; i++)
                {
                    x[t + i] += (float)((rnd.NextDouble() * 2 - 1) * Math.Exp(-i / 30.0) * 0.3);
                }
            }

            return x;
        }

        float[] Tone(double seconds, double db, params double[] freqs)
        {
            var x = new float[(int)(seconds * Rate)];
            for (int i = 0; i < x.Length; i++)
            {
                x[i] = (float)freqs.Select((f, k) => Math.Sin(2 * Math.PI * f * i / Rate + k) / (k + 1)).Sum();
            }

            return Scale(x, db);
        }

        float[] NarrowRumble(double seconds, double db, double centre)
        {
            // White noise through a resonator at the centre frequency (a truck, a fan's drone).
            var x = Noise(seconds, 0, "white");
            var r = 0.995;
            var w0 = 2 * Math.PI * centre / Rate;
            double y1 = 0, y2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                var y = x[i] + 2 * r * Math.Cos(w0) * y1 - r * r * y2;
                y2 = y1;
                y1 = y;
                x[i] = (float)y;
            }

            return Scale(x, db);
        }

        void Report(string name, IEnumerable<int> values)
        {
            var v = values.OrderBy(x => x).ToArray();
            int P(double q) => v[(int)Math.Round(q * (v.Length - 1))];
            Console.WriteLine($"   {name,-46} n={v.Length,4}  min {v[0],4}  P5 {P(0.05),4}  median {P(0.5),4}  max {v[^1],4} ms  | under 60 ms: {v.Count(x => x < 60),3}, under 100 ms: {v.Count(x => x < 100),3}");
        }

        Console.WriteLine("Real speech (FLEURS) — voiced milliseconds; should be high:");
        Report("whole utterances", speech.Select(s => VoicePresence.VoicedMilliseconds(Room(s))));
        foreach (var seconds in new[] { 0.3, 0.45, 0.7 })
        {
            Report($"pieces of {seconds:F2} s at −30 dBFS", speech.Select(s => VoicePresence.VoicedMilliseconds(Room(Scale(ShortPiece(s, seconds), -30)))));
        }

        Report("pieces of 0.45 s, quiet speaker −45 dBFS", speech.Select(s => VoicePresence.VoicedMilliseconds(Room(Scale(ShortPiece(s, 0.45), -45), -70))));
        foreach (var (kind, snr) in new[] { ("white", 10.0), ("white", 5.0), ("pink", 10.0), ("pink", 5.0), ("brown", 5.0) })
        {
            Report($"pieces of 0.45 s in {kind} noise, SNR {snr} dB", speech.Select(s =>
            {
                var piece = Scale(ShortPiece(s, 0.45), -30);
                var bg = Noise(0.6 + piece.Length / (double)Rate + 1.4, -30 - snr, kind);
                var x = Room(piece, -90);
                for (int i = 0; i < x.Length && i < bg.Length; i++)
                {
                    x[i] += bg[i];
                }

                return VoicePresence.VoicedMilliseconds(x);
            }));
        }

        if (tts is not null)
        {
            var phrases = new[] { "Да.", "Не.", "Спри.", "Помощ.", "Как си?", "Защо?", "Кога?", "Утре.", "Нищо.", "Добре.", "Повтори.", "Колко е часът?" };
            var values = new List<int>();
            foreach (var phrase in phrases)
            {
                for (int r = 0; r < 3; r++)
                {
                    var audio = new List<float>();
                    await foreach (var c in tts.SynthesizeAsync(phrase, new SynthesisOptions(), CancellationToken.None))
                    {
                        audio.AddRange(c.Samples);
                    }

                    var s = Scale(StreamingResampler.Resample(audio.ToArray(), tts.SampleRate, Rate), -30);
                    values.Add(VoicePresence.VoicedMilliseconds(Room(s)));
                }
            }

            Report("VEDAR's voice (Piper), short phrases ×3", values);
        }

        Console.WriteLine("\nNoises — voiced milliseconds; should be low:");
        var noises = new List<(string, Func<float[]>)>
        {
            ("silence", () => new float[Rate]),
            ("white noise 0.6 s −40 dBFS", () => Noise(0.6, -40, "white")),
            ("white noise 1.5 s −30 dBFS", () => Noise(1.5, -30, "white")),
            ("pink noise 1 s −35 dBFS", () => Noise(1.0, -35, "pink")),
            ("brown noise (rumble) 2 s −30 dBFS", () => Noise(2.0, -30, "brown")),
            ("breath (pink, swelling) 0.8 s −38 dBFS", () => Envelope(Noise(0.8, -38, "pink"), 0.3, 0.4)),
            ("breath (white, swelling) 0.8 s −40 dBFS", () => Envelope(Noise(0.8, -40, "white"), 0.3, 0.4)),
            ("keyboard clicks 2 s", () => Clicks(2.0)),
            ("drone at 100 Hz (resonant noise) 1.5 s", () => NarrowRumble(1.5, -35, 100)),
            ("drone at 180 Hz (resonant noise) 1.5 s", () => NarrowRumble(1.5, -35, 180)),
            ("mains hum 50 Hz + harmonics 2 s −40 dBFS", () => Tone(2.0, -40, 50, 100, 150, 200)),
        };
        foreach (var (name, make) in noises)
        {
            Report(name, Enumerable.Range(0, 20).Select(_ => VoicePresence.VoicedMilliseconds(Room(make()))));
        }

        Report("hum −40 dBFS under a breath (hum in background)", Enumerable.Range(0, 20).Select(_ =>
        {
            var hum = Tone(2.8, -40, 50, 100, 150, 200);
            var breath = Envelope(Noise(0.8, -36, "pink"), 0.3, 0.4);
            var x = Room(breath, -90);
            for (int i = 0; i < x.Length && i < hum.Length; i++)
            {
                x[i] += hum[i];
            }

            return VoicePresence.VoicedMilliseconds(x);
        }));
    }
}
