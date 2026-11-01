using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Vedar.Core.Abstractions;

namespace Vedar.Core.Orchestration;

/// <summary>
/// Pipelines text → synthesis → playback. While one sentence plays the next one is synthesized,
/// so long answers start quickly. Cancelling the token stops synthesis and playback immediately.
/// </summary>
public sealed class SpeechPlaybackQueue : IAsyncDisposable
{
    private readonly Channel<string> _texts = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<SynthesizedAudio> _audio = Channel.CreateBounded<SynthesizedAudio>(new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly IAudioOutput _output;
    private readonly SynthesisOptions _options;
    private readonly Action? _onFirstAudio;
    private readonly ILogger _logger;
    private readonly CancellationToken _token;
    private readonly Task _synthesisTask;
    private readonly Task _playbackTask;
    private int _started;

    public SpeechPlaybackQueue(ISpeechSynthesizer synthesizer, IAudioOutput output, SynthesisOptions options, Action? onFirstAudio, ILogger logger, CancellationToken token)
    {
        _synthesizer = synthesizer;
        _output = output;
        _options = options;
        _onFirstAudio = onFirstAudio;
        _logger = logger;
        _token = token;
        _synthesisTask = Task.Run(SynthesisLoopAsync, CancellationToken.None);
        _playbackTask = Task.Run(PlaybackLoopAsync, CancellationToken.None);
    }

    public bool HasStartedSpeaking => Volatile.Read(ref _started) == 1;

    public Task Completion => Task.WhenAll(_synthesisTask, _playbackTask);

    public void Enqueue(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            _texts.Writer.TryWrite(text);
        }
    }

    /// <summary>Signals that no more text will be added.</summary>
    public void Complete() => _texts.Writer.TryComplete();

    private async Task SynthesisLoopAsync()
    {
        Exception? error = null;
        try
        {
            await foreach (var text in _texts.Reader.ReadAllAsync(_token).ConfigureAwait(false))
            {
                await foreach (var audio in _synthesizer.SynthesizeAsync(text, _options, _token).ConfigureAwait(false))
                {
                    await _audio.Writer.WriteAsync(audio, _token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            _audio.Writer.TryComplete(error);
        }
    }

    private async Task PlaybackLoopAsync()
    {
        await foreach (var audio in _audio.Reader.ReadAllAsync(_token).ConfigureAwait(false))
        {
            if (audio.Samples.Length == 0)
            {
                continue;
            }

            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                try
                {
                    _onFirstAudio?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Speaking-started callback failed.");
                }
            }

            await _output.PlayAsync(audio.Samples, audio.SampleRate, _token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _texts.Writer.TryComplete();
        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch
        {
            // Errors are observed by the owner through Completion.
        }
    }
}
