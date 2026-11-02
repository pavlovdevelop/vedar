using Microsoft.Extensions.Logging;
using Vedar.AI;
using Vedar.AI.OpenAi;
using Vedar.AI.Providers;
using Vedar.Audio.Windows;
using Vedar.Commands;
using Vedar.Commands.Actions;
using Vedar.Core.Abstractions;
using Vedar.Core.Localization;
using Vedar.Core.Orchestration;
using Vedar.Infrastructure.Configuration;
using Vedar.Infrastructure.History;
using Vedar.Infrastructure.Logging;
using Vedar.Infrastructure.Models;
using Vedar.Infrastructure.Security;
using Vedar.Infrastructure.Startup;
using Vedar.Speech.Recognition;
using Vedar.Speech.Synthesis;
using Vedar.Speech.Vad;
using Vedar.Speech.WakeWord;

namespace Vedar.Desktop.Hosting;

/// <summary>
/// Composition root: creates every component and wires the Windows implementations to the core.
/// Each part can be replaced here (e.g. another TTS engine) without touching the rest.
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    private AppServices() { }

    public required AppPaths Paths { get; init; }
    public required FileLoggerProvider FileLogger { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
    public required JsonSettingsStore Settings { get; init; }
    public required Localizer Localizer { get; init; }
    public required ISecretStore Secrets { get; init; }
    public required AutoStartManager AutoStart { get; init; }
    public required EncryptedHistoryStore History { get; init; }
    public required WasapiAudioInput AudioInput { get; init; }
    public required WasapiAudioOutput AudioOutput { get; init; }
    public required VoiceListener Listener { get; init; }
    public required WhisperSpeechRecognizer Recognizer { get; init; }
    public required PiperSpeechSynthesizer Synthesizer { get; init; }
    public required WakeWordMatcher WakeWord { get; init; }
    public required HttpClient Http { get; init; }
    public required BgGptProvider BgGpt { get; init; }
    public required OllamaProvider Ollama { get; init; }
    public required AiRouter Ai { get; init; }
    public required DesktopUiHost UiHost { get; init; }
    public required CommandDispatcher Commands { get; init; }
    public required AssistantOrchestrator Orchestrator { get; init; }
    public required ModelDownloader Downloader { get; init; }
    public required UserDataEraser Eraser { get; init; }

    public static AppServices Create(AppPaths paths, DesktopUiHost uiHost)
    {
        paths.EnsureCreated();
        var fileLogger = new FileLoggerProvider(paths.LogsDir);
        var settings = new JsonSettingsStore(paths, NullFactory.CreateLogger<JsonSettingsStore>());
        var current = settings.Load();
        fileLogger.MinimumLevel = current.Privacy.DiagnosticLogging ? LogLevel.Debug : LogLevel.Information;
        var factory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(fileLogger).SetMinimumLevel(LogLevel.Debug));
        var loc = Localizer.Load(current.General.UiLanguage);
        Localizer.Current = loc;

        var secrets = new DpapiSecretStore(paths, factory.CreateLogger<DpapiSecretStore>());
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "VEDAR.exe");
        var autoStart = new AutoStartManager(new WindowsRunKeyStore(), exe);
        var history = new EncryptedHistoryStore(paths, settings, factory.CreateLogger<EncryptedHistoryStore>());

        var input = new WasapiAudioInput(factory.CreateLogger<WasapiAudioInput>());
        var output = new WasapiAudioOutput(factory.CreateLogger<WasapiAudioOutput>());
        var listener = new VoiceListener(input, settings, factory.CreateLogger<VoiceListener>());
        var recognizer = new WhisperSpeechRecognizer(settings, paths.FindModel, factory.CreateLogger<WhisperSpeechRecognizer>());
        var synthesizer = new PiperSpeechSynthesizer(settings, paths.FindModel, paths.CacheDir, factory.CreateLogger<PiperSpeechSynthesizer>());
        var wake = new WakeWordMatcher(current.Activation.WakeWord);

        var http = OpenAiCompatibleChatClient.CreateHttpClient();
        var chatClient = new OpenAiCompatibleChatClient(http, factory.CreateLogger<OpenAiCompatibleChatClient>());
        var bggpt = new BgGptProvider(chatClient, settings, secrets, factory.CreateLogger<BgGptProvider>());
        var ollama = new OllamaProvider(chatClient, http, settings, factory.CreateLogger<OllamaProvider>());
        var groq = new GroqProvider(chatClient, settings, secrets, factory.CreateLogger<GroqProvider>());
        var ai = new AiRouter(settings, bggpt, ollama, factory.CreateLogger<AiRouter>(), groq);

        var system = new WindowsSystemActions(new WindowsSystemVolume(), factory.CreateLogger<WindowsSystemActions>());
        var commands = new CommandDispatcher(settings, system, uiHost, ai, history, loc, factory.CreateLogger<CommandDispatcher>(), isWakeToken: wake.IsWakeToken);
        var orchestrator = new AssistantOrchestrator(factory.CreateLogger<AssistantOrchestrator>(), settings, listener, recognizer, wake, synthesizer, output, commands, ai, loc, history: history);
        var downloader = new ModelDownloader(http, factory.CreateLogger<ModelDownloader>());
        var eraser = new UserDataEraser(paths, secrets, autoStart, history, factory.CreateLogger<UserDataEraser>());

        return new AppServices
        {
            Paths = paths,
            FileLogger = fileLogger,
            LoggerFactory = factory,
            Settings = settings,
            Localizer = loc,
            Secrets = secrets,
            AutoStart = autoStart,
            History = history,
            AudioInput = input,
            AudioOutput = output,
            Listener = listener,
            Recognizer = recognizer,
            Synthesizer = synthesizer,
            WakeWord = wake,
            Http = http,
            BgGpt = bggpt,
            Ollama = ollama,
            Ai = ai,
            UiHost = uiHost,
            Commands = commands,
            Orchestrator = orchestrator,
            Downloader = downloader,
            Eraser = eraser,
        };
    }

    private static readonly ILoggerFactory NullFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

    public async ValueTask DisposeAsync()
    {
        await Orchestrator.DisposeAsync().ConfigureAwait(false);
        Listener.Dispose();
        AudioInput.Dispose();
        AudioOutput.Dispose();
        Recognizer.Dispose();
        Synthesizer.Dispose();
        Http.Dispose();
        LoggerFactory.Dispose();
        FileLogger.Dispose();
    }
}
