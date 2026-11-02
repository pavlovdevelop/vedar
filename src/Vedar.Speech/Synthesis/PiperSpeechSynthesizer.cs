using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using Vedar.Audio.Dsp;
using Vedar.Core.Abstractions;
using Vedar.Core.Text;
using Vedar.Speech.Text;

namespace Vedar.Speech.Synthesis;

/// <summary>
/// Offline Bulgarian male voice: the official Piper voice <c>bg_BG-dimitar-medium</c> (rhasspy/piper-voices),
/// run with the Piper-compatible VITS runtime of sherpa-onnx (ONNX Runtime + espeak-ng phonemizer).
/// Text is normalized, split into sentences and synthesized one sentence at a time, so speech starts quickly
/// and can be stopped between (and during) sentences.
/// </summary>
public sealed class PiperSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly ISettingsStore _settings;
    private readonly Func<string, string?> _resolveModel;
    private readonly string _cacheDir;
    private readonly ILogger<PiperSpeechSynthesizer> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private OfflineTts? _tts;

    public PiperSpeechSynthesizer(ISettingsStore settings, Func<string, string?> resolveModel, string cacheDir, ILogger<PiperSpeechSynthesizer> logger)
    {
        _settings = settings;
        _resolveModel = resolveModel;
        _cacheDir = cacheDir;
        _logger = logger;
    }

    public string EngineName => "Piper";
    public string VoiceName { get; private set; } = string.Empty;
    public bool IsReady => _tts is not null;
    public int SampleRate { get; private set; }
    public PreparedPiperModel? Model { get; private set; }

    /// <summary>Pause added after each sentence.</summary>
    public int SentencePauseMs { get; set; } = 150;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _tts?.Dispose();
            _tts = null;
            var voiceId = _settings.Current.Voice.VoiceId;
            var voiceDir = _resolveModel(Path.Combine("piper", voiceId))
                ?? throw new FileNotFoundException($"Piper voice '{voiceId}' is not installed.");
            var espeak = _resolveModel(Path.Combine("piper", "espeak-ng-data"))
                ?? throw new DirectoryNotFoundException("espeak-ng-data is not installed.");

            await Task.Run(() =>
            {
                var prepared = PiperModelPreparer.PrepareVoice(voiceDir, voiceId, espeak, _cacheDir);
                var config = new OfflineTtsConfig();
                config.Model.Vits.Model = prepared.ModelPath;
                config.Model.Vits.Tokens = prepared.TokensPath;
                config.Model.Vits.DataDir = prepared.EspeakDataDir;
                config.Model.Vits.NoiseScale = prepared.Config.NoiseScale;
                config.Model.Vits.NoiseScaleW = prepared.Config.NoiseW;
                config.Model.Vits.LengthScale = prepared.Config.LengthScale;
                config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
                config.Model.Provider = "cpu";
                config.Model.Debug = 0;
                config.MaxNumSentences = 1;
                _tts = new OfflineTts(config);
                SampleRate = _tts.SampleRate;
                Model = prepared;
            }, cancellationToken).ConfigureAwait(false);

            VoiceName = voiceId;
            _logger.LogInformation("Piper voice {Voice} loaded ({Rate} Hz).", voiceId, SampleRate);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async IAsyncEnumerable<SynthesizedAudio> SynthesizeAsync(string text, SynthesisOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var normalized = BulgarianTextNormalizer.Normalize(text);
        foreach (var sentence in SentenceSplitter.Split(normalized))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = await Task.Run(() => Generate(sentence, options.Rate, cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (samples.Length > 0)
            {
                yield return new SynthesizedAudio(samples, SampleRate, sentence);
            }
        }
    }

    /// <summary>Synthesizes one sentence. The native callback aborts generation as soon as the token is cancelled.</summary>
    internal float[] Generate(string sentence, float rate, CancellationToken cancellationToken)
    {
        _lock.Wait(cancellationToken);
        try
        {
            var tts = _tts ?? throw new InvalidOperationException("Speech synthesis is not initialized.");
            OfflineTtsCallback callback = (_, _) => cancellationToken.IsCancellationRequested ? 0 : 1;
            rate = Math.Clamp(rate, 0.5f, 2.0f);
            // Slowing down is done by the model (natural prosody). Speeding up is not: Piper's length scale gives only
            // ~1.3x at speed 2.0, so the sentence is spoken at its natural tempo and compressed by exactly the requested
            // factor without changing the pitch (see TimeStretch and docs/SPEECH_BENCHMARKS.md).
            var audio = tts.GenerateWithCallback(sentence, Math.Min(rate, 1.0f), 0, callback);
            GC.KeepAlive(callback);
            cancellationToken.ThrowIfCancellationRequested();
            var samples = audio.Samples ?? [];
            if (samples.Length == 0)
            {
                return samples;
            }

            if (rate > 1.0f)
            {
                samples = TimeStretch.ChangeTempo(samples, rate, SampleRate);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var pause = (int)(SampleRate * SentencePauseMs / 1000 / Math.Max(1.0f, rate));
            var result = new float[samples.Length + pause];
            samples.CopyTo(result, 0);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Wait();
        try
        {
            _tts?.Dispose();
            _tts = null;
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
