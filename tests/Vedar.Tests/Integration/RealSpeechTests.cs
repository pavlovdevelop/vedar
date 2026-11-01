using System.Diagnostics;
using Vedar.Audio.Dsp;
using Vedar.Commands;
using Vedar.Core.Abstractions;
using Vedar.Core.Localization;
using Vedar.Core.Settings;
using Vedar.Speech.Recognition;
using Vedar.Speech.Synthesis;
using Vedar.Speech.WakeWord;
using Vedar.Tests.Support;

namespace Vedar.Tests.Integration;

/// <summary>Loads the real Whisper and Piper models once for all real-speech tests.</summary>
public sealed class RealSpeechFixture : IAsyncLifetime
{
    public WhisperSpeechRecognizer? Recognizer { get; private set; }
    public PiperSpeechSynthesizer? Synthesizer { get; private set; }
    public string? SkipReason { get; private set; }
    public TestSettingsStore Settings { get; } = new();

    public async ValueTask InitializeAsync()
    {
        if (TestModels.WhisperSmall is null || TestModels.PiperVoice is null || TestModels.EspeakData is null)
        {
            SkipReason = TestModels.MissingMessage("Whisper small-q5_1 / Piper bg_BG-dimitar-medium");
            return;
        }

        Recognizer = new WhisperSpeechRecognizer(Settings, TestModels.Resolver, Log.Null<WhisperSpeechRecognizer>());
        Synthesizer = new PiperSpeechSynthesizer(Settings, TestModels.Resolver, TestModels.NewTempDir(), Log.Null<PiperSpeechSynthesizer>());
        await Recognizer.InitializeAsync(CancellationToken.None);
        await Synthesizer.InitializeAsync(CancellationToken.None);
    }

    public async Task<float[]> SynthesizeAsync(string text, float rate = 1f)
    {
        var all = new List<float>();
        await foreach (var chunk in Synthesizer!.SynthesizeAsync(text, new SynthesisOptions(rate), CancellationToken.None))
        {
            all.AddRange(chunk.Samples);
        }

        return all.ToArray();
    }

    public ValueTask DisposeAsync()
    {
        Recognizer?.Dispose();
        Synthesizer?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// These tests use the REAL whisper.cpp and Piper models (no mocks). They are skipped, with an explicit message,
/// when the models have not been downloaded.
/// </summary>
[Collection("RealSpeech")]
public class RealSpeechTests(RealSpeechFixture fx, ITestOutputHelper output) : IClassFixture<RealSpeechFixture>
{
    private void SkipIfNoModels() => Assert.SkipWhen(fx.SkipReason is not null, fx.SkipReason ?? string.Empty);

    [Fact]
    public async Task Piper_generates_a_bulgarian_male_voice_wav_file()
    {
        SkipIfNoModels();
        var sw = Stopwatch.StartNew();
        var samples = await fx.SynthesizeAsync("Здравей! Аз съм Ведар. Днес е първи ноември.");
        sw.Stop();
        var path = Path.Combine(TestModels.NewTempDir(), "piper-test.wav");
        WavFile.WriteMono16(path, samples, fx.Synthesizer!.SampleRate);

        var wav = WavFile.ReadMono(path);
        output.WriteLine($"Generated {wav.Duration.TotalSeconds:F2} s of audio in {sw.ElapsedMilliseconds} ms → {path}");
        Assert.Equal(22050, wav.SampleRate);
        Assert.InRange(wav.Duration.TotalSeconds, 1.5, 8);
        Assert.True(AudioMath.Rms(wav.Samples) > 0.02f, "audio must not be silent");
        Assert.True(AudioMath.Peak(wav.Samples) <= 1.0f);
        Assert.True(new FileInfo(path).Length > 40_000);
    }

    [Fact]
    public async Task Piper_speaking_rate_changes_duration()
    {
        SkipIfNoModels();
        var normal = await fx.SynthesizeAsync("Това е тест за скоростта на говорене.");
        var fast = await fx.SynthesizeAsync("Това е тест за скоростта на говорене.", 1.6f);
        var slow = await fx.SynthesizeAsync("Това е тест за скоростта на говорене.", 0.6f);
        output.WriteLine($"normal {normal.Length / 22050.0:F2} s, fast {fast.Length / 22050.0:F2} s, slow {slow.Length / 22050.0:F2} s");
        // Piper/VITS quantizes phoneme durations to whole frames, so speeding up has a smaller effect than slowing down.
        Assert.True(fast.Length < normal.Length * 0.95);
        Assert.True(slow.Length > normal.Length * 1.3);
    }

    [Fact]
    public async Task Speech_synthesis_can_be_interrupted_immediately()
    {
        SkipIfNoModels();
        var longText = string.Join(" ", Enumerable.Repeat("Това е дълго изречение, което Ведар чете на глас, за да проверим спирането.", 30));
        using var cts = new CancellationTokenSource();
        var chunks = 0;
        var sw = new Stopwatch();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in fx.Synthesizer!.SynthesizeAsync(longText, new SynthesisOptions(), cts.Token))
            {
                chunks++;
                if (chunks == 1)
                {
                    sw.Start();
                    cts.Cancel();
                }
            }
        });
        sw.Stop();
        output.WriteLine($"Stopped after {chunks} chunk(s), {sw.ElapsedMilliseconds} ms after cancel.");
        Assert.Equal(1, chunks);
        Assert.True(sw.ElapsedMilliseconds < 1500);
    }

    [Fact]
    public async Task Whisper_transcribes_real_human_bulgarian_speech()
    {
        SkipIfNoModels();
        var dir = Path.Combine(TestModels.TestDataDir, "fleurs");
        var rows = File.ReadAllLines(Path.Combine(dir, "transcripts.tsv")).Select(l => l.Split('\t')).ToList();
        double totalCer = 0, totalWer = 0;
        foreach (var row in rows)
        {
            var wav = WavFile.ReadMono(Path.Combine(dir, row[0]));
            var result = await fx.Recognizer!.RecognizeAsync(wav.Samples, new RecognitionRequest(RecognitionPurpose.Command), CancellationToken.None);
            var cer = TextMetrics.Cer(row[2], result.Text);
            var wer = TextMetrics.Wer(row[2], result.Text);
            totalCer += cer;
            totalWer += wer;
            output.WriteLine($"{row[0]} ({result.ProcessingTime.TotalMilliseconds:F0} ms) CER {cer:P0} WER {wer:P0}\n  REF: {row[2]}\n  HYP: {result.Text}");
            Assert.True(cer < 0.5, $"{row[0]}: CER {cer:P0}");
        }

        var meanCer = totalCer / rows.Count;
        output.WriteLine($"Mean CER {meanCer:P1}, mean WER {totalWer / rows.Count:P1} over {rows.Count} FLEURS utterances (real speakers).");
        Assert.True(meanCer < 0.25, $"mean CER {meanCer:P1}");
    }

    [Fact]
    public async Task Whisper_returns_nothing_for_background_noise()
    {
        SkipIfNoModels();
        var r = new Random(3);
        var noise = Enumerable.Range(0, 16000 * 3).Select(_ => (float)((r.NextDouble() * 2 - 1) * 0.003)).ToArray();
        var result = await fx.Recognizer!.RecognizeAsync(noise, new RecognitionRequest(RecognitionPurpose.WakeWord, "Ведар"), CancellationToken.None);
        output.WriteLine($"Noise → '{result.Text}'");
        Assert.False(new WakeWordMatcher().Match(result.Text).IsMatch);
    }

    [Fact]
    public async Task Spoken_commands_go_from_voice_to_recognized_intent()
    {
        SkipIfNoModels();
        // Synthetic speech (Piper voice) → real Whisper → real wake word matcher → real command recognizer.
        var cases = new (string Spoken, string Intent)[]
        {
            ("Ведар, колко е часът?", "time"),
            ("Ведар, коя дата сме днес?", "date"),
            ("Ведар, отвори браузъра.", "openBrowser"),
            ("Ведар, отвори бележника.", "openNotepad"),
            ("Ведар, прочети файл.", "readFile"),
            ("Ведар, повтори.", "repeat"),
            ("Ведар, искам да поговорим.", "startConversation"),
            ("Ведар, отвори настройките.", "openSettings"),
            ("Ведар, затвори разговора.", "endConversation"),
            ("Ведар, намали звука.", "volumeDown"),
        };
        var wake = new WakeWordMatcher();
        var dispatcher = new CommandDispatcher(new TestSettingsStore(), new FakeSystemActions(), new FakeUiHost(), new FakeConversationAi(), new FakeHistoryStore(),
            Localizer.Load("bg"), Log.Null<CommandDispatcher>(), isWakeToken: wake.IsWakeToken);
        int wakeOk = 0, intentOk = 0;
        foreach (var (spoken, intent) in cases)
        {
            // 300 ms of silence before the speech, as the voice activity detector's pre-roll provides in the app.
            var audio = new float[4800].Concat(StreamingResampler.Resample(await fx.SynthesizeAsync(spoken), 22050, 16000)).ToArray();
            var wakeScan = await fx.Recognizer!.RecognizeAsync(audio, new RecognitionRequest(RecognitionPurpose.WakeWord, RecognitionPrompts.WakeWord), CancellationToken.None);
            var full = await fx.Recognizer!.RecognizeAsync(audio, new RecognitionRequest(RecognitionPurpose.Command, RecognitionPrompts.Commands), CancellationToken.None);
            var match = wake.Match(full.Text);
            var wakeDetected = wake.Match(wakeScan.Text).IsMatch;
            var recognized = dispatcher.Recognizer.Recognize(match.IsMatch ? match.Command : full.Text)?.IntentId;
            wakeOk += wakeDetected ? 1 : 0;
            intentOk += recognized == intent || (intent == "startConversation" && recognized?.StartsWith("startConversation") == true) ? 1 : 0;
            output.WriteLine($"{spoken} → wake[{wakeScan.ProcessingTime.TotalMilliseconds:F0} ms]: '{wakeScan.Text}' ({wakeDetected}); full[{full.ProcessingTime.TotalMilliseconds:F0} ms]: '{full.Text}' → {recognized ?? "-"} (expected {intent})");
        }

        output.WriteLine($"Wake word detected {wakeOk}/{cases.Length}, intent correct {intentOk}/{cases.Length}.");
        Assert.True(intentOk >= cases.Length * 0.8, $"intent accuracy {intentOk}/{cases.Length}");
        Assert.True(wakeOk >= cases.Length * 0.8, $"wake word detection {wakeOk}/{cases.Length}");
    }
}

[CollectionDefinition("RealSpeech", DisableParallelization = true)]
public class RealSpeechCollection;
