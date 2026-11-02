using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Vedar.Audio.Dsp;
using Vedar.Audio.Testing;
using Vedar.Commands;
using Vedar.Core.Abstractions;
using Vedar.Core.Localization;
using Vedar.Core.Settings;
using Vedar.Core.Text;
using Vedar.Infrastructure.Configuration;
using Vedar.Speech.Recognition;
using Vedar.Speech.Synthesis;
using Vedar.Speech.Vad;
using Vedar.Speech.WakeWord;

// VEDAR developer tools: model preparation for the build and honest speech benchmarks.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0)
{
    Console.WriteLine("""
        vedar-tools <command>
          prepare-piper <voice.onnx> <outDir>          Add sherpa-onnx metadata (copy) + tokens.txt
          synthesize "<text>" <out.wav> [rate]         Piper TTS to WAV (rate 0.5–2.0)
          transcribe <in.wav> [model]                  Whisper STT
          bench-commands [prompt=wake|vocab|none] [threads]   Piper → Whisper → intent round trip (synthetic voice)
          bench-wake <fleurs-wav-dir> [limit] [wakeModel] [ctx]   False wake-word activations on real speech without "Ведар"
          bench-wakedetect [wakeModel] [ctx]           Wake-word detection rate on synthetic commands (with 300 ms pre-roll)
          bench-vad [minutes]                          CPU cost of continuous listening (VAD only)
          bench-rate [repeats]                         Speech duration at each speaking rate (Piper is stochastic: averaged)
          check-bggpt [endpoint] [model]               Connection check exactly as VEDAR does it; key from env VEDAR_BGGPT_KEY
          check-groq [model]                           The same for the Groq backup; key from env VEDAR_GROQ_KEY
        Models are searched in ./models, the app folder and VEDAR_MODELS_DIR.
        """);
    return 1;
}

// "@file.txt" reads the text argument from a UTF-8 file (avoids code page conversion of command lines, e.g. under Wine).
for (int i = 1; i < args.Length; i++)
{
    if (args[i].StartsWith('@') && File.Exists(args[i][1..]))
    {
        args[i] = File.ReadAllText(args[i][1..], Encoding.UTF8).Trim();
    }
}

var paths = new AppPaths(dataRoot: Path.Combine(Path.GetTempPath(), "vedar-tools"));
Func<string, string?> resolve = paths.FindModel;
var settings = new MemorySettings();

switch (args[0])
{
    case "prepare-piper":
    {
        var onnx = Path.GetFullPath(args[1]);
        var outDir = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(outDir);
        var config = PiperModelPreparer.ReadConfig(onnx + ".json");
        var target = Path.Combine(outDir, Path.GetFileNameWithoutExtension(onnx) + PiperModelPreparer.PreparedSuffix);
        PiperModelPreparer.WritePrepared(onnx, config, target, Path.Combine(outDir, "tokens.txt"));
        var meta = PiperModelPreparer.ReadMetadata(target);
        Console.WriteLine($"Prepared {target}: {string.Join(", ", meta.Select(kv => kv.Key + "=" + kv.Value))}");
        return 0;
    }

    case "normalize":
    {
        var normalized = Vedar.Speech.Text.BulgarianTextNormalizer.Normalize(args[1]);
        Console.WriteLine(normalized);
        Console.WriteLine(string.Join(" ", normalized.Take(12).Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture))));
        return 0;
    }

    case "synthesize":
    {
        using var tts = await CreateTtsAsync();
        var all = new List<float>();
        var sw = Stopwatch.StartNew();
        var rate = args.Length > 3 ? float.Parse(args[3], CultureInfo.InvariantCulture) : 1.0f;
        await foreach (var chunk in tts.SynthesizeAsync(args[1], new SynthesisOptions(rate), CancellationToken.None))
        {
            all.AddRange(chunk.Samples);
        }

        WavFile.WriteMono16(args[2], all.ToArray(), tts.SampleRate);
        Console.WriteLine($"{all.Count / (double)tts.SampleRate:F2} s audio in {sw.ElapsedMilliseconds} ms → {args[2]}");
        return 0;
    }

    case "bench-rate":
    {
        var repeats = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 5;
        using var tts = await CreateTtsAsync();
        string[] sentences =
        [
            "Това е тест за скоростта на говорене.",
            "Днес е понеделник, втори ноември две хиляди двадесет и шеста година.",
            "Ведар отваря браузъра и чете избрания текст на глас.",
        ];
        double? baseline = null;
        foreach (var rate in new[] { 1.0f, 0.6f, 0.8f, 1.3f, 1.6f, 2.0f })
        {
            var durations = new List<double>();
            for (int r = 0; r < repeats; r++)
            {
                foreach (var sentence in sentences)
                {
                    durations.Add(tts.Generate(sentence, rate, CancellationToken.None).Length / (double)tts.SampleRate);
                }
            }

            var total = durations.Sum() / repeats;
            baseline ??= total;
            var perSentence = Enumerable.Range(0, sentences.Length)
                .Select(i => durations.Where((_, k) => k % sentences.Length == i).ToList()).ToList();
            var spread = perSentence.Max(d => (d.Max() - d.Min()) / d.Average());
            Console.WriteLine($"rate {rate:F1}: {total:F2} s for 3 sentences = {baseline / total:F2}x faster than 1.0 (max run-to-run spread {spread:P0})");
        }

        return 0;
    }

    case "check-bggpt":
    {
        // The key is read from the environment only, so it never lands in the shell history or in a log.
        var key = Environment.GetEnvironmentVariable("VEDAR_BGGPT_KEY");
        var secrets = new Vedar.Infrastructure.Security.InMemorySecretStore();
        if (!string.IsNullOrWhiteSpace(key))
        {
            secrets.Set(SecretNames.BgGptApiKey, key);
        }

        if (args.Length > 1) { settings.Current.Ai.BgGptEndpoint = args[1]; }
        if (args.Length > 2) { settings.Current.Ai.BgGptModel = args[2]; }
        using var http = Vedar.AI.OpenAi.OpenAiCompatibleChatClient.CreateHttpClient();
        var client = new Vedar.AI.OpenAi.OpenAiCompatibleChatClient(http, NullLogger.Instance);
        var provider = new Vedar.AI.Providers.BgGptProvider(client, settings, secrets, NullLogger<Vedar.AI.Providers.BgGptProvider>.Instance);
        var status = await provider.CheckStatusAsync(CancellationToken.None);
        Console.WriteLine($"{settings.Current.Ai.BgGptEndpoint} ({settings.Current.Ai.BgGptModel}): {status.Availability} — {status.Detail}");
        return status.IsAvailable ? 0 : 2;
    }

    case "check-groq":
    {
        var key = Environment.GetEnvironmentVariable("VEDAR_GROQ_KEY");
        var secrets = new Vedar.Infrastructure.Security.InMemorySecretStore();
        if (!string.IsNullOrWhiteSpace(key))
        {
            secrets.Set(SecretNames.GroqApiKey, key);
        }

        if (args.Length > 1) { settings.Current.Ai.GroqModel = args[1]; }
        using var http = Vedar.AI.OpenAi.OpenAiCompatibleChatClient.CreateHttpClient();
        var client = new Vedar.AI.OpenAi.OpenAiCompatibleChatClient(http, NullLogger.Instance);
        var provider = new Vedar.AI.Providers.GroqProvider(client, settings, secrets, NullLogger<Vedar.AI.Providers.GroqProvider>.Instance);
        var status = await provider.CheckStatusAsync(CancellationToken.None);
        Console.WriteLine($"{settings.Current.Ai.GroqEndpoint} ({settings.Current.Ai.GroqModel}): {status.Availability} — {status.Detail}");
        return status.IsAvailable ? 0 : 2;
    }

    case "transcribe":
    {
        if (args.Length > 2)
        {
            settings.Current.Recognition.ModelFile = args[2];
        }

        using var stt = new WhisperSpeechRecognizer(settings, resolve, NullLogger<WhisperSpeechRecognizer>.Instance);
        await stt.InitializeAsync(CancellationToken.None);
        var wav = WavFile.ReadMono(args[1]);
        var samples = StreamingResampler.Resample(wav.Samples, wav.SampleRate, 16000);
        var r = await stt.RecognizeAsync(samples, new RecognitionRequest(RecognitionPurpose.Command), CancellationToken.None);
        Console.WriteLine($"[{r.ProcessingTime.TotalMilliseconds:F0} ms, conf {r.Confidence:F2}] {r.Text}");
        return 0;
    }

    case "bench-commands":
    {
        var promptMode = args.Length > 1 ? args[1] : "vocab";
        if (args.Length > 2)
        {
            settings.Current.Recognition.Threads = int.Parse(args[2], CultureInfo.InvariantCulture);
        }

        string? prompt = promptMode switch { "none" => null, "wake" => "Ведар", _ => RecognitionPrompts.Commands };
        using var tts = await CreateTtsAsync();
        using var stt = new WhisperSpeechRecognizer(settings, resolve, NullLogger<WhisperSpeechRecognizer>.Instance);
        await stt.InitializeAsync(CancellationToken.None);
        var wake = new WakeWordMatcher();
        var dispatcher = new CommandDispatcher(settings, new NoActions(), new NoUi(), new NoAi(), new NoHistory(), Localizer.Load("bg"),
            NullLogger<CommandDispatcher>.Instance, isWakeToken: wake.IsWakeToken);
        var cases = BenchmarkData.Commands;
        int wakeOk = 0, intentOk = 0, total = 0;
        double wakeMs = 0, fullMs = 0;
        foreach (var rate in new[] { 0.9f, 1.0f, 1.15f })
        {
            foreach (var (spoken, intent) in cases)
            {
                var audio = new List<float>();
                await foreach (var chunk in tts.SynthesizeAsync(spoken, new SynthesisOptions(rate), CancellationToken.None))
                {
                    audio.AddRange(chunk.Samples);
                }

                var samples = StreamingResampler.Resample(audio.ToArray(), tts.SampleRate, 16000);
                var w = await stt.RecognizeAsync(samples, new RecognitionRequest(RecognitionPurpose.WakeWord, "Ведар"), CancellationToken.None);
                var f = await stt.RecognizeAsync(samples, new RecognitionRequest(RecognitionPurpose.Command, prompt), CancellationToken.None);
                var m = wake.Match(f.Text);
                var got = dispatcher.Recognizer.Recognize(m.IsMatch ? m.Command : f.Text)?.IntentId ?? "-";
                var wakeHit = wake.Match(w.Text).IsMatch;
                var ok = got == intent || (intent.StartsWith("startConversation") && got.StartsWith("startConversation"));
                wakeOk += wakeHit ? 1 : 0;
                intentOk += ok ? 1 : 0;
                total++;
                wakeMs += w.ProcessingTime.TotalMilliseconds;
                fullMs += f.ProcessingTime.TotalMilliseconds;
                Console.WriteLine($"{(ok ? "OK " : "ERR")} x{rate:F2} {spoken} → '{f.Text}' → {got}{(wakeHit ? "" : "   [wake missed: '" + w.Text + "']")}");
            }
        }

        Console.WriteLine($"\nprompt={promptMode} threads={WhisperSpeechRecognizer.DefaultThreads()}/{settings.Current.Recognition.Threads} " +
                          $"wake-word detected {wakeOk}/{total} ({100.0 * wakeOk / total:F0}%), intent correct {intentOk}/{total} ({100.0 * intentOk / total:F0}%), " +
                          $"avg wake scan {wakeMs / total:F0} ms, avg command recognition {fullMs / total:F0} ms");
        return 0;
    }

    case "bench-wakedetect":
    {
        if (args.Length > 1) { settings.Current.Recognition.WakeModelFile = args[1]; }
        using var tts = await CreateTtsAsync();
        using var stt = new WhisperSpeechRecognizer(settings, resolve, NullLogger<WhisperSpeechRecognizer>.Instance);
        if (args.Length > 2) { stt.WakeAudioContext = int.Parse(args[2], CultureInfo.InvariantCulture); }
        await stt.InitializeAsync(CancellationToken.None);
        var wake = new WakeWordMatcher();
        int hits = 0, total = 0;
        double ms = 0;
        foreach (var rate in new[] { 0.9f, 1.0f, 1.15f })
        {
            foreach (var (spoken, _) in BenchmarkData.Commands)
            {
                var audio = new List<float>(new float[(int)(tts.SampleRate * 0.3)]); // VAD pre-roll
                await foreach (var chunk in tts.SynthesizeAsync(spoken, new SynthesisOptions(rate), CancellationToken.None))
                {
                    audio.AddRange(chunk.Samples);
                }

                var samples = StreamingResampler.Resample(audio.ToArray(), tts.SampleRate, 16000);
                var w = await stt.RecognizeAsync(samples, new RecognitionRequest(RecognitionPurpose.WakeWord, RecognitionPrompts.WakeWord), CancellationToken.None);
                var hit = wake.Match(w.Text).IsMatch;
                hits += hit ? 1 : 0;
                total++;
                ms += w.ProcessingTime.TotalMilliseconds;
                if (!hit) { Console.WriteLine($"miss x{rate:F2} {spoken} → '{w.Text}'"); }
            }
        }

        Console.WriteLine($"wakeModel={Path.GetFileName(stt.LoadedWakeModelPath)} ctx={stt.WakeAudioContext}: detected {hits}/{total} ({100.0 * hits / total:F0}%), avg {ms / total:F0} ms per scan");
        return 0;
    }

    case "bench-wake":
    {
        var dir = args[1];
        var limit = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : int.MaxValue;
        if (args.Length > 3) { settings.Current.Recognition.WakeModelFile = args[3]; }
        using var stt = new WhisperSpeechRecognizer(settings, resolve, NullLogger<WhisperSpeechRecognizer>.Instance);
        if (args.Length > 4) { stt.WakeAudioContext = int.Parse(args[4], CultureInfo.InvariantCulture); }
        await stt.InitializeAsync(CancellationToken.None);
        var wake = new WakeWordMatcher();
        var files = Directory.EnumerateFiles(dir, "*.wav").OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();
        int activations = 0;
        double audioSeconds = 0;
        var proc = Process.GetCurrentProcess();
        var cpuStart = proc.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        foreach (var file in files)
        {
            var wav = WavFile.ReadMono(file);
            var samples = StreamingResampler.Resample(wav.Samples, wav.SampleRate, 16000);
            audioSeconds += samples.Length / 16000.0;
            var r = await stt.RecognizeAsync(samples, new RecognitionRequest(RecognitionPurpose.WakeWord, "Ведар"), CancellationToken.None);
            if (wake.Match(r.Text).IsMatch)
            {
                activations++;
                Console.WriteLine($"FALSE ACTIVATION: {Path.GetFileName(file)} → '{r.Text}'");
            }
        }

        proc.Refresh();
        var cpu = (proc.TotalProcessorTime - cpuStart).TotalSeconds;
        Console.WriteLine($"\nUtterances: {files.Count} ({audioSeconds / 60:F1} min of real Bulgarian speech without the wake word)");
        Console.WriteLine($"False activations: {activations} ({100.0 * activations / Math.Max(1, files.Count):F2}% of utterances)");
        Console.WriteLine($"Wake scan cost: {1000 * wall.Elapsed.TotalSeconds / files.Count:F0} ms wall, {cpu / files.Count:F2} CPU-seconds per utterance (model {Path.GetFileName(stt.LoadedWakeModelPath)})");
        return 0;
    }

    case "bench-vad":
    {
        var minutes = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 10;
        var input = new SimulatedAudioInput();
        using var listener = new VoiceListener(input, settings, NullLogger<VoiceListener>.Instance);
        await listener.SetModeAsync(ListeningMode.WakeWord, CancellationToken.None);
        var r = new Random(1);
        var noise = Enumerable.Range(0, 16000 * 60).Select(_ => (float)((r.NextDouble() * 2 - 1) * 0.01)).ToArray();
        var proc = Process.GetCurrentProcess();
        var cpuStart = proc.TotalProcessorTime;
        for (int m = 0; m < minutes; m++)
        {
            await input.FeedAsync(noise);
        }

        proc.Refresh();
        var cpu = (proc.TotalProcessorTime - cpuStart).TotalSeconds;
        Console.WriteLine($"VAD + filtering of {minutes} min audio: {cpu:F2} CPU-seconds → {100 * cpu / (minutes * 60):F3}% of one core while listening.");
        return 0;
    }

    default:
        Console.Error.WriteLine("Unknown command.");
        return 1;
}

async Task<PiperSpeechSynthesizer> CreateTtsAsync()
{
    var tts = new PiperSpeechSynthesizer(settings, resolve, paths.CacheDir, NullLogger<PiperSpeechSynthesizer>.Instance);
    await tts.InitializeAsync(CancellationToken.None);
    return tts;
}

internal static class BenchmarkData
{
    public static readonly (string Spoken, string Intent)[] Commands =
    [
        ("Ведар, колко е часът?", "time"),
        ("Ведар, коя дата сме днес?", "date"),
        ("Ведар, кой ден сме?", "dayOfWeek"),
        ("Ведар, отвори браузъра.", "openBrowser"),
        ("Ведар, отвори бележника.", "openNotepad"),
        ("Ведар, отвори папка документи.", "openDocuments"),
        ("Ведар, отвори Уикипедия.", "openWebsite"),
        ("Ведар, прочети файл.", "readFile"),
        ("Ведар, повтори.", "repeat"),
        ("Ведар, спри.", "stop"),
        ("Ведар, усили звука.", "volumeUp"),
        ("Ведар, намали звука.", "volumeDown"),
        ("Ведар, искам да поговорим.", "startConversation"),
        ("Ведар, затвори разговора.", "endConversation"),
        ("Ведар, отвори настройките.", "openSettings"),
        ("Ведар, помощ.", "help"),
    ];
}

internal sealed class MemorySettings : ISettingsStore
{
    public VedarSettings Current { get; } = new VedarSettings().Normalize();
    public event EventHandler<VedarSettings>? SettingsChanged { add { } remove { } }
    public VedarSettings Load() => Current;
    public void Save(VedarSettings settings) { }
}

internal sealed class NoActions : ISystemActions
{
    public bool OpenDefaultBrowser() => true;
    public bool OpenUrl(Uri url) => true;
    public bool OpenNotepad() => true;
    public bool OpenDocumentsFolder() => true;
    public float? ChangeSystemVolume(float delta) => 0.5f;
    public float? GetSystemVolume() => 0.5f;
    public float? SetSystemVolume(float level) => level;
}

internal sealed class NoUi : IUserInterfaceHost
{
    public void ShowMainWindow() { }
    public void ShowSettings() { }
    public void ShowConversation() { }
    public Task<string?> PickTextFileAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<string?> GetClipboardTextAsync() => Task.FromResult<string?>(null);
    public void RequestExit() { }
    public void Notify(UiNotificationKind kind, string text) { }
}

internal sealed class NoAi : IConversationAi
{
    public event EventHandler<AiFallbackEventArgs>? FallbackUsed { add { } remove { } }
    public string ActiveProviderName => "BgGPT";
    public bool ActiveProviderIsCloud => true;
    public bool IsConfigured => false;
    public Task<AiStatus> CheckStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new AiStatus(AiAvailability.NotConfigured, "BgGPT", true, ""));
    public IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken) => AsyncEnumerable.Empty<string>();
}

internal sealed class NoHistory : IConversationHistoryStore
{
    public void Append(string conversationId, ChatMessage message) { }
    public IReadOnlyList<string> ListConversations() => [];
    public void DeleteAll() { }
}
