using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Vedar.Core.Abstractions;
using Whisper.net;

namespace Vedar.Speech.Recognition;

/// <summary>
/// Local speech-to-text with whisper.cpp (via Whisper.net). Audio never leaves the computer.
/// Uses a multilingual model with the language fixed to Bulgarian. A smaller model scans for the wake word.
/// </summary>
public sealed class WhisperSpeechRecognizer : ISpeechRecognizer
{
    /// <summary>Whisper's encoder window is 30 s (1500). For short utterances a smaller window is ~2× faster with similar accuracy.</summary>
    internal const int ShortAudioContext = 768;
    internal const double ShortAudioMaxSeconds = 14.0;
    internal const double WakeScanSeconds = 4.0;
    private const int PadSamples = AudioFrameEventArgs.SampleRate / 4; // 250 ms

    private readonly ISettingsStore _settings;
    private readonly Func<string, string?> _resolveModel;
    private readonly ILogger<WhisperSpeechRecognizer> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<(bool Wake, string? Prompt, int Ctx), WhisperProcessor> _processors = [];
    private WhisperFactory? _main;
    private WhisperFactory? _wake;

    public WhisperSpeechRecognizer(ISettingsStore settings, Func<string, string?> resolveModel, ILogger<WhisperSpeechRecognizer> logger)
    {
        _settings = settings;
        _resolveModel = resolveModel;
        _logger = logger;
    }

    public string EngineName => "whisper.cpp";
    public bool IsReady => _main is not null;
    /// <summary>Encoder window for the wake-word scan (whisper.cpp audio_ctx; 1500 = 30 s). Smaller is faster.</summary>
    public int WakeAudioContext { get; set; } = ShortAudioContext;

    public string? LoadedModelPath { get; private set; }
    public string? LoadedWakeModelPath { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisposeModels();
            var rec = _settings.Current.Recognition;
            var mainPath = ResolveWhisper(rec.ModelFile)
                ?? ResolveWhisper(Core.Settings.RecognitionSettings.DefaultModel)
                ?? ResolveWhisper(Core.Settings.RecognitionSettings.DefaultWakeModel)
                ?? throw new FileNotFoundException($"Whisper model '{rec.ModelFile}' was not found.");
            var wakePath = ResolveWhisper(rec.WakeModelFile);

            await Task.Run(() =>
            {
                _main = WhisperFactory.FromPath(mainPath);
                _wake = wakePath is not null && !string.Equals(wakePath, mainPath, StringComparison.OrdinalIgnoreCase)
                    ? WhisperFactory.FromPath(wakePath)
                    : null;
            }, cancellationToken).ConfigureAwait(false);

            LoadedModelPath = mainPath;
            LoadedWakeModelPath = wakePath ?? mainPath;
            _logger.LogInformation("Whisper loaded: main={Main}, wake={Wake}", Path.GetFileName(mainPath), Path.GetFileName(LoadedWakeModelPath));
        }
        finally
        {
            _lock.Release();
        }
    }

    private string? ResolveWhisper(string? file) =>
        string.IsNullOrWhiteSpace(file) ? null : _resolveModel(Path.Combine("whisper", file));

    private int Threads()
    {
        var t = _settings.Current.Recognition.Threads;
        return t > 0 ? t : DefaultThreads();
    }

    /// <summary>All logical CPUs but one (UI and audio stay responsive), between 2 and 8.</summary>
    public static int DefaultThreads() => Math.Clamp(Environment.ProcessorCount - 1, 2, 8);

    public async Task<RecognitionResult> RecognizeAsync(float[] samples16k, RecognitionRequest request, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(samples16k.Length / (double)AudioFrameEventArgs.SampleRate);
        var sw = Stopwatch.StartNew();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_main is null)
            {
                throw new InvalidOperationException("Speech recognition is not initialized.");
            }

            var wake = request.Purpose == RecognitionPurpose.WakeWord;
            var input = samples16k;
            if (wake && input.Length > WakeScanSeconds * AudioFrameEventArgs.SampleRate)
            {
                // The wake word is at the beginning; scanning only the start keeps hands-free mode cheap.
                input = input.AsSpan(0, (int)(WakeScanSeconds * AudioFrameEventArgs.SampleRate)).ToArray();
            }

            input = Pad(input);
            var seconds = input.Length / (double)AudioFrameEventArgs.SampleRate;
            var ctx = wake ? WakeAudioContext : seconds <= ShortAudioMaxSeconds ? ShortAudioContext : 0;
            var processor = GetProcessor(wake, request.Prompt, ctx);

            var text = new StringBuilder();
            float probSum = 0;
            int segments = 0;
            float maxNoSpeech = 0;
            await Task.Run(async () =>
            {
                await foreach (var segment in processor.ProcessAsync(input, cancellationToken).ConfigureAwait(false))
                {
                    text.Append(segment.Text);
                    probSum += segment.Probability;
                    maxNoSpeech = Math.Max(maxNoSpeech, segment.NoSpeechProbability);
                    segments++;
                }
            }, cancellationToken).ConfigureAwait(false);

            var confidence = segments > 0 ? probSum / segments : 0f;
            var cleaned = HallucinationFilter.Clean(text.ToString());
            if (IsPromptEcho(cleaned, request.Prompt))
            {
                cleaned = string.Empty;
            }

            if (maxNoSpeech > 0.8f && confidence < 0.5f)
            {
                cleaned = string.Empty;
            }

            return new RecognitionResult(cleaned, confidence, duration, sw.Elapsed);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Whisper occasionally repeats its initial prompt on unclear audio. A transcript that contains two or more
    /// complete sentences of the prompt is treated as such an echo, so it can never trigger a command.
    /// </summary>
    internal static bool IsPromptEcho(string text, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = Core.Text.BulgarianText.NormalizeForMatching(text);
        var sentences = prompt.Split(['.', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Core.Text.BulgarianText.NormalizeForMatching)
            .Where(p => p.Contains(' '))
            .ToList();
        return sentences.Count(p => normalized.Contains(p, StringComparison.Ordinal)) >= 2;
    }

    /// <summary>
    /// Adds 250 ms of silence before and after the speech (Whisper tends to drop a first word that starts
    /// abruptly — which is exactly where the wake word is) and ensures the 1.1 s minimum whisper.cpp needs.
    /// </summary>
    internal static float[] Pad(float[] input)
    {
        const int min = AudioFrameEventArgs.SampleRate * 11 / 10;
        var length = Math.Max(min, input.Length + 2 * PadSamples);
        var padded = new float[length];
        input.CopyTo(padded, Math.Max(PadSamples, (length - input.Length) / 2));
        return padded;
    }

    private WhisperProcessor GetProcessor(bool wake, string? prompt, int ctx)
    {
        var key = (wake && _wake is not null, prompt, ctx);
        if (_processors.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var factory = key.Item1 ? _wake! : _main!;
        var builder = factory.CreateBuilder()
            .WithLanguage(_settings.Current.Recognition.Language)
            .WithThreads(Threads())
            .WithNoContext()
            .WithSingleSegment()
            .WithTemperature(0f)
            .WithProbabilities();
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            builder = builder.WithPrompt(prompt);
        }

        if (ctx > 0)
        {
            builder = builder.WithAudioContextSize(ctx);
        }

        var processor = builder.Build();
        _processors[key] = processor;
        return processor;
    }

    private void DisposeModels()
    {
        foreach (var p in _processors.Values)
        {
            p.Dispose();
        }

        _processors.Clear();
        _wake?.Dispose();
        _main?.Dispose();
        _wake = null;
        _main = null;
    }

    public void Dispose()
    {
        _lock.Wait();
        try
        {
            DisposeModels();
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
