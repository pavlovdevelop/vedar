using System.Text.Json.Serialization;

namespace Vedar.Core.Settings;

/// <summary>
/// All user settings. Serialized to %LOCALAPPDATA%\VEDAR\settings.json.
/// Secrets (API keys) are NEVER stored here; see <c>ISecretStore</c>.
/// </summary>
public sealed class VedarSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GeneralSettings General { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public MicrophoneSettings Microphone { get; set; } = new();
    public RecognitionSettings Recognition { get; set; } = new();
    public ActivationSettings Activation { get; set; } = new();
    public AiSettings Ai { get; set; } = new();
    public AccessibilitySettings Accessibility { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public List<WebsiteShortcut> Websites { get; set; } = WebsiteShortcut.CreateDefaults();

    /// <summary>Clamps out-of-range values and fills missing sections, so a hand-edited file cannot break the app.</summary>
    public VedarSettings Normalize()
    {
        General ??= new();
        Voice ??= new();
        Microphone ??= new();
        Recognition ??= new();
        Activation ??= new();
        Ai ??= new();
        Accessibility ??= new();
        Privacy ??= new();
        Websites ??= WebsiteShortcut.CreateDefaults();

        General.UserName = (General.UserName ?? string.Empty).Trim();
        if (General.UserName.Length > 40)
        {
            General.UserName = General.UserName[..40];
        }

        General.UiLanguage = string.IsNullOrWhiteSpace(General.UiLanguage) ? "bg" : General.UiLanguage;
        Voice.Rate = Math.Clamp(Voice.Rate, 0.5f, 2.0f);
        Voice.Volume = Math.Clamp(Voice.Volume, 0.05f, 1.0f);
        Voice.VoiceId = string.IsNullOrWhiteSpace(Voice.VoiceId) ? VoiceSettings.DefaultVoiceId : Voice.VoiceId;
        Microphone.Gain = Math.Clamp(Microphone.Gain, 0.25f, 8.0f);
        Microphone.Sensitivity = Math.Clamp(Microphone.Sensitivity, 0.0f, 1.0f);
        Recognition.ModelFile = string.IsNullOrWhiteSpace(Recognition.ModelFile) ? RecognitionSettings.DefaultModel : Recognition.ModelFile;
        Recognition.WakeModelFile = string.IsNullOrWhiteSpace(Recognition.WakeModelFile) ? RecognitionSettings.DefaultWakeModel : Recognition.WakeModelFile;
        Recognition.Threads = Math.Clamp(Recognition.Threads, 0, 32);
        Recognition.MaxCommandSeconds = Math.Clamp(Recognition.MaxCommandSeconds, 3, 30);
        Recognition.MaxConversationSeconds = Math.Clamp(Recognition.MaxConversationSeconds, 5, 60);
        Activation.WakeWord = string.IsNullOrWhiteSpace(Activation.WakeWord) ? "Ведар" : Activation.WakeWord.Trim();
        Activation.PushToTalkHotkey = string.IsNullOrWhiteSpace(Activation.PushToTalkHotkey) ? ActivationSettings.DefaultPushToTalk : Activation.PushToTalkHotkey;
        Activation.StopHotkey = string.IsNullOrWhiteSpace(Activation.StopHotkey) ? ActivationSettings.DefaultStop : Activation.StopHotkey;
        Activation.MicrophoneToggleHotkey = string.IsNullOrWhiteSpace(Activation.MicrophoneToggleHotkey) ? ActivationSettings.DefaultMicToggle : Activation.MicrophoneToggleHotkey;
        Activation.FollowUpSeconds = Math.Clamp(Activation.FollowUpSeconds, 0, 30);
        Activation.CommandListenTimeoutSeconds = Math.Clamp(Activation.CommandListenTimeoutSeconds, 3, 20);
        Ai.MaxResponseTokens = Math.Clamp(Ai.MaxResponseTokens, 50, 4000);
        Ai.Temperature = Math.Clamp(Ai.Temperature, 0f, 1.5f);
        Ai.RequestTimeoutSeconds = Math.Clamp(Ai.RequestTimeoutSeconds, 10, 300);
        Ai.MaxHistoryMessages = Math.Clamp(Ai.MaxHistoryMessages, 2, 100);
        Ai.BgGptEndpoint = string.IsNullOrWhiteSpace(Ai.BgGptEndpoint) ? AiSettings.DefaultBgGptEndpoint : Ai.BgGptEndpoint.Trim();
        Ai.BgGptModel = string.IsNullOrWhiteSpace(Ai.BgGptModel) ? AiSettings.DefaultBgGptModel : Ai.BgGptModel.Trim();
        Ai.OllamaEndpoint = string.IsNullOrWhiteSpace(Ai.OllamaEndpoint) ? AiSettings.DefaultOllamaEndpoint : Ai.OllamaEndpoint.Trim();
        Ai.GroqEndpoint = string.IsNullOrWhiteSpace(Ai.GroqEndpoint) ? AiSettings.DefaultGroqEndpoint : Ai.GroqEndpoint.Trim();
        Ai.GroqModel = string.IsNullOrWhiteSpace(Ai.GroqModel) ? AiSettings.DefaultGroqModel : Ai.GroqModel.Trim();
        Accessibility.FontScale = Math.Clamp(Accessibility.FontScale, 0.8, 2.0);
        Websites = Websites.Where(w => w is not null && !string.IsNullOrWhiteSpace(w.Name) && WebsiteShortcut.IsSafeUrl(w.Url)).ToList();
        SchemaVersion = CurrentSchemaVersion;
        return this;
    }
}

public sealed class GeneralSettings
{
    /// <summary>Name used in greetings ("Здравей, Стевко!"). Empty means no name.</summary>
    public string UserName { get; set; } = string.Empty;
    public string UiLanguage { get; set; } = "bg";
    public bool FirstRunCompleted { get; set; }
    public bool GreetOnStartup { get; set; } = true;
    /// <summary>Informational mirror of the HKCU Run entry. The registry is the source of truth.</summary>
    public bool StartWithWindows { get; set; }
    public bool StartMinimizedToTray { get; set; } = true;
}

public sealed class VoiceSettings
{
    public const string DefaultVoiceId = "bg_BG-dimitar-medium";

    /// <summary>TTS engine id. "piper" is the built-in offline engine.</summary>
    public string Engine { get; set; } = "piper";
    public string VoiceId { get; set; } = DefaultVoiceId;
    /// <summary>Speaking rate multiplier, 1.0 = normal.</summary>
    public float Rate { get; set; } = 1.0f;
    /// <summary>VEDAR voice volume 0..1 (does not change the Windows master volume).</summary>
    public float Volume { get; set; } = 0.9f;
    /// <summary>Output device id; null = Windows default output.</summary>
    public string? OutputDeviceId { get; set; }
    /// <summary>Short tones that signal "listening" / "done" without words.</summary>
    public bool Earcons { get; set; } = true;
}

public sealed class MicrophoneSettings
{
    /// <summary>Capture device id; null = Windows default microphone.</summary>
    public string? DeviceId { get; set; }
    /// <summary>Software gain applied to the captured signal.</summary>
    public float Gain { get; set; } = 1.0f;
    /// <summary>Voice detection sensitivity 0..1. Higher = reacts to quieter speech (and more noise).</summary>
    public float Sensitivity { get; set; } = 0.5f;
    /// <summary>High-pass filter that removes hum and rumble below ~100 Hz.</summary>
    public bool NoiseFilter { get; set; } = true;
    /// <summary>Privacy switch. When true, no audio is captured at all.</summary>
    public bool Muted { get; set; }
}

public sealed class RecognitionSettings
{
    public const string DefaultModel = "ggml-small-q5_1.bin";
    public const string DefaultWakeModel = "ggml-base-q5_1.bin";

    /// <summary>Whisper model used for commands and conversation.</summary>
    public string ModelFile { get; set; } = DefaultModel;
    /// <summary>Smaller Whisper model used to scan short utterances for the wake word.</summary>
    public string WakeModelFile { get; set; } = DefaultWakeModel;
    public string Language { get; set; } = "bg";
    /// <summary>0 = automatic (half of the logical CPUs, at least 2).</summary>
    public int Threads { get; set; }
    public int MaxCommandSeconds { get; set; } = 15;
    public int MaxConversationSeconds { get; set; } = 30;
}

public sealed class ActivationSettings
{
    public const string DefaultPushToTalk = "Ctrl+Shift+Space";
    public const string DefaultStop = "Ctrl+Shift+Back";
    public const string DefaultMicToggle = "Ctrl+Shift+F8";

    /// <summary>Hands-free mode: VEDAR listens locally for the wake word.</summary>
    public bool HandsFreeEnabled { get; set; } = true;
    public string WakeWord { get; set; } = "Ведар";
    public string PushToTalkHotkey { get; set; } = DefaultPushToTalk;
    public string StopHotkey { get; set; } = DefaultStop;
    public string MicrophoneToggleHotkey { get; set; } = DefaultMicToggle;
    /// <summary>After VEDAR answers in a conversation, listen this many seconds for a follow-up without the wake word.</summary>
    public int FollowUpSeconds { get; set; } = 8;
    /// <summary>How long to wait for a command after the wake word / push-to-talk before giving up.</summary>
    public int CommandListenTimeoutSeconds { get; set; } = 7;
}

[JsonConverter(typeof(JsonStringEnumConverter<AiProviderKind>))]
public enum AiProviderKind
{
    /// <summary>No AI. Only local commands work.</summary>
    None,
    /// <summary>BgGPT cloud API (requires API key and internet).</summary>
    BgGpt,
    /// <summary>Local Ollama server.</summary>
    Ollama,
    /// <summary>BgGPT when online and configured, otherwise Ollama when available.</summary>
    Auto,
    /// <summary>Groq cloud API (free plan with a personal key), normally used as the backup for BgGPT.</summary>
    Groq,
}

public sealed class AiSettings
{
    public const string DefaultBgGptEndpoint = "https://api.bggpt.ai/v1";
    public const string DefaultBgGptModel = "bggpt-gemma-3-27b-fp8";
    public const string DefaultOllamaEndpoint = "http://localhost:11434";
    public const string DefaultGroqEndpoint = "https://api.groq.com/openai/v1";
    /// <summary>On Groq's free plan (30 requests per minute, 1000 per day — checked November 2026).</summary>
    public const string DefaultGroqModel = "openai/gpt-oss-120b";

    public AiProviderKind Provider { get; set; } = AiProviderKind.BgGpt;
    public string BgGptEndpoint { get; set; } = DefaultBgGptEndpoint;
    public string BgGptModel { get; set; } = DefaultBgGptModel;
    public string OllamaEndpoint { get; set; } = DefaultOllamaEndpoint;
    public string OllamaModel { get; set; } = string.Empty;
    public string GroqEndpoint { get; set; } = DefaultGroqEndpoint;
    public string GroqModel { get; set; } = DefaultGroqModel;
    /// <summary>When BgGPT does not answer (no connection, error, rejected key), ask Groq instead — if a Groq key is saved.</summary>
    public bool UseGroqFallback { get; set; } = true;
    public int MaxResponseTokens { get; set; } = 400;
    public float Temperature { get; set; } = 0.4f;
    public int RequestTimeoutSeconds { get; set; } = 90;
    /// <summary>Messages kept as context in the current conversation.</summary>
    public int MaxHistoryMessages { get; set; } = 20;
    /// <summary>Say that text is sent to a cloud service when a conversation starts.</summary>
    public bool AnnounceCloudUse { get; set; } = true;
    /// <summary>Persist conversations locally (DPAPI-encrypted). Off by default.</summary>
    public bool SaveHistory { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ScreenReaderNotificationMode>))]
public enum ScreenReaderNotificationMode
{
    /// <summary>Notify the screen reader only about things VEDAR does not already say aloud.</summary>
    Auto,
    /// <summary>Never send notifications to the screen reader.</summary>
    Off,
    /// <summary>Only errors and important state changes.</summary>
    Important,
    /// <summary>All status changes and responses (may duplicate VEDAR's own voice).</summary>
    All,
}

public sealed class AccessibilitySettings
{
    public ScreenReaderNotificationMode ScreenReaderNotifications { get; set; } = ScreenReaderNotificationMode.Auto;
    /// <summary>VEDAR says short status phrases ("Слушам", "Обработвам").</summary>
    public bool SpeakStatusChanges { get; set; } = true;
    public double FontScale { get; set; } = 1.0;
}

public sealed class PrivacySettings
{
    /// <summary>When true, recognized text is written to the local log for troubleshooting. Off by default.</summary>
    public bool DiagnosticLogging { get; set; }
}

public sealed class WebsiteShortcut
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    /// <summary>Other spoken names for the site.</summary>
    public List<string> Aliases { get; set; } = [];

    public static bool IsSafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public static List<WebsiteShortcut> CreateDefaults() =>
    [
        new() { Name = "Гугъл", Url = "https://www.google.com", Aliases = ["google", "гугъл търсачка", "търсачката"] },
        new() { Name = "Ютюб", Url = "https://www.youtube.com", Aliases = ["youtube", "ютуб", "ю тюб"] },
        new() { Name = "Уикипедия", Url = "https://bg.wikipedia.org", Aliases = ["wikipedia", "википедия", "уикипедия"] },
        new() { Name = "БНР", Url = "https://bnr.bg", Aliases = ["радиото", "българско национално радио", "бнр"] },
        new() { Name = "БНТ", Url = "https://bnt.bg", Aliases = ["българска национална телевизия", "бнт"] },
        new() { Name = "Синоптик", Url = "https://www.sinoptik.bg", Aliases = ["времето", "прогнозата за времето", "sinoptik"] },
        new() { Name = "Съюз на слепите", Url = "https://www.ssb-bg.net", Aliases = ["съюза на слепите", "съюз на слепите в българия"] },
    ];
}
