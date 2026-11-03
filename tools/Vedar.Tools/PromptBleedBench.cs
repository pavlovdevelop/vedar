using Microsoft.Extensions.Logging.Abstractions;
using Vedar.Audio.Dsp;
using Vedar.Core.Abstractions;
using Vedar.Core.Text;
using Vedar.Speech.Recognition;
using Vedar.Speech.Synthesis;
using Vedar.Speech.WakeWord;

/// <summary>
/// Does Whisper, primed with the example commands, "hear" a command in noise or in clipped speech — and does the
/// check with the fast model (no command examples) catch it without rejecting commands that were really said?
/// </summary>
internal static class PromptBleedBench
{
    public static async Task RunAsync(WhisperSpeechRecognizer stt, PiperSpeechSynthesizer tts)
    {
        var wake = new WakeWordMatcher();
        var prompt = RecognitionPrompts.Commands;
        var sentences = prompt.Split(['.', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => { var m = wake.Match(p); return BulgarianText.NormalizeForMatching(m.IsMatch ? m.Request : p); }).ToHashSet();
        bool IsPromptSentence(string text)
        {
            var m = wake.Match(text);
            var heard = BulgarianText.NormalizeForMatching(m.IsMatch ? m.Request : text);
            return heard.Length > 0 && sentences.Contains(heard);
        }

        async Task<float[]> Say(string text)
        {
            var audio = new List<float>();
            await foreach (var c in tts.SynthesizeAsync(text, new SynthesisOptions(), CancellationToken.None))
            {
                audio.AddRange(c.Samples);
            }

            return StreamingResampler.Resample(audio.ToArray(), tts.SampleRate, 16000);
        }

        var rnd = new Random(5);
        float[] Noise(double seconds, double db, bool pink = false)
        {
            var x = new float[(int)(seconds * 16000)];
            double last = 0;
            for (int i = 0; i < x.Length; i++)
            {
                var w = rnd.NextDouble() * 2 - 1;
                last = pink ? 0.97 * last + 0.03 * w * 8 : w;
                x[i] = (float)(last * Math.Pow(10, db / 20));
            }

            return x;
        }

        float[] Clicks(double seconds)
        {
            var x = Noise(seconds, -70);
            for (int t = 1600; t < x.Length - 200; t += rnd.Next(1500, 4000))
            {
                for (int i = 0; i < 120; i++)
                {
                    x[t + i] += (float)((rnd.NextDouble() * 2 - 1) * Math.Exp(-i / 25.0) * 0.3);
                }
            }

            return x;
        }

        var noises = new List<(string Name, float[] Audio)>
        {
            ("white noise 0.6 s −40 dB", Noise(0.6, -40)),
            ("white noise 1.5 s −35 dB", Noise(1.5, -35)),
            ("breath-like 0.8 s", Noise(0.8, -38, pink: true)),
            ("rumble 2 s", Noise(2.0, -33, pink: true)),
            ("keyboard clicks 2 s", Clicks(2.0)),
            ("silence 1.2 s", Noise(1.2, -75)),
        };
        foreach (var (spoken, cut) in new[] { ("Как си?", 0.15), ("Как си?", 0.3), ("Здравей.", 0.25), ("Добре съм.", 0.3), ("Благодаря.", 0.35) })
        {
            var a = await Say(spoken);
            noises.Add(($"'{spoken}' without its first {cut * 1000:F0} ms", a[(int)(cut * 16000)..]));
        }

        Console.WriteLine("Noise and clipped speech (nothing should run):");
        int bled = 0, caught = 0;
        foreach (var (name, audio) in noises)
        {
            var main = await stt.RecognizeAsync(audio, new RecognitionRequest(RecognitionPurpose.Command, prompt), CancellationToken.None);
            var isCommand = IsPromptSentence(main.Text);
            var verdict = "";
            if (isCommand)
            {
                bled++;
                var fast = await stt.RecognizeAsync(audio, new RecognitionRequest(RecognitionPurpose.WakeWord, RecognitionPrompts.WakeWord), CancellationToken.None);
                var m = wake.Match(fast.Text);
                var confirmed = !fast.IsEmpty && (!m.IsMatch || BulgarianText.NormalizeForMatching(m.Request).Length >= 2);
                caught += confirmed ? 0 : 1;
                verdict = $" → fast model: '{fast.Text}' → {(confirmed ? "WOULD RUN" : "blocked")}";
            }

            var quick = await stt.RecognizeAsync(audio, new RecognitionRequest(RecognitionPurpose.WakeWord, RecognitionPrompts.WakeWord), CancellationToken.None);
            Console.WriteLine($"   {name,-40} main: '{main.Text}' (conf {main.Confidence:F2}){(isCommand ? " [command phrase]" : "")}{verdict} | fast: '{quick.Text}' (conf {quick.Confidence:F2}) sim {SpeechAgreement.Similarity(BulgarianText.NormalizeForMatching(main.Text), BulgarianText.NormalizeForMatching(quick.Text)):F2}");
        }

        Console.WriteLine($"Command phrases invented: {bled}/{noises.Count}; blocked by the check: {caught}/{bled}\n");

        Console.WriteLine("Commands really said (all should run):");
        int said = 0, confirmedSaid = 0;
        foreach (var command in new[] { "Колко е часът?", "Коя дата сме?", "Отвори браузъра.", "Отвори бележника.", "Отвори настройките.", "Прочети файл.", "Повтори.", "Спри.", "Усили звука.", "Намали звука.", "Искам да поговорим.", "Затвори разговора.", "Помощ." })
        {
            var audio = new float[4800].Concat(await Say(command)).ToArray();
            var main = await stt.RecognizeAsync(audio, new RecognitionRequest(RecognitionPurpose.Command, prompt), CancellationToken.None);
            said++;
            var fast = await stt.RecognizeAsync(audio, new RecognitionRequest(RecognitionPurpose.WakeWord, RecognitionPrompts.WakeWord), CancellationToken.None);
            var m = wake.Match(fast.Text);
            var ok = !fast.IsEmpty && (!m.IsMatch || BulgarianText.NormalizeForMatching(m.Request).Length >= 2);
            confirmedSaid += ok ? 1 : 0;
            Console.WriteLine($"   {command,-24} main: '{main.Text}' → fast model: '{fast.Text}' → {(ok ? "runs" : "REJECTED")} sim {SpeechAgreement.Similarity(BulgarianText.NormalizeForMatching(main.Text), BulgarianText.NormalizeForMatching(fast.Text)):F2}");
        }

        Console.WriteLine($"Real commands confirmed by the check: {confirmedSaid}/{said}\n");

        Console.WriteLine("Short real replies (should be accepted):");
        foreach (var reply in new[] { "Да.", "Не.", "Вали.", "Защо?", "Кога?", "Добре съм.", "Може би.", "Разбира се.", "Нищо.", "Утре.", "Как си?", "Какво правиш?", "Здравей." })
        {
            var audio = new float[4800].Concat(await Say(reply)).ToArray();
            var main = await stt.RecognizeAsync(audio, new RecognitionRequest(RecognitionPurpose.Command, prompt), CancellationToken.None);
            var quick = await stt.RecognizeAsync(audio, new RecognitionRequest(RecognitionPurpose.WakeWord, RecognitionPrompts.WakeWord), CancellationToken.None);
            Console.WriteLine($"   {reply,-14} main: '{main.Text}' (conf {main.Confidence:F2}) | fast: '{quick.Text}' (conf {quick.Confidence:F2}) sim {SpeechAgreement.Similarity(BulgarianText.NormalizeForMatching(main.Text), BulgarianText.NormalizeForMatching(quick.Text)):F2}");
        }
    }
}
