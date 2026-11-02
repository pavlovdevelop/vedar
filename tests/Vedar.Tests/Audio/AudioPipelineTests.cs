using Vedar.Audio.Dsp;
using Vedar.Audio.Testing;
using Vedar.Core.Abstractions;
using Vedar.Core.Settings;
using Vedar.Speech.Vad;
using Vedar.Tests.Support;

namespace Vedar.Tests.Audio;

internal static class Signals
{
    public static float[] Sine(double freq, double seconds, int rate = 16000, float amplitude = 0.3f) =>
        Enumerable.Range(0, (int)(seconds * rate)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * freq * i / rate))).ToArray();

    public static float[] Noise(double seconds, float amplitude, int seed = 1, int rate = 16000)
    {
        var r = new Random(seed);
        return Enumerable.Range(0, (int)(seconds * rate)).Select(_ => (float)((r.NextDouble() * 2 - 1) * amplitude)).ToArray();
    }

    /// <summary>Voice-like signal: harmonics of 140 Hz with a syllable-rate envelope.</summary>
    public static float[] Speechlike(double seconds, int rate = 16000) =>
        Enumerable.Range(0, (int)(seconds * rate)).Select(i =>
        {
            double t = i / (double)rate;
            double env = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 4 * t);
            return (float)(0.25 * env * (Math.Sin(2 * Math.PI * 140 * t) + 0.5 * Math.Sin(2 * Math.PI * 280 * t) + 0.3 * Math.Sin(2 * Math.PI * 700 * t)));
        }).ToArray();

    public static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    public static int ZeroCrossings(float[] s) => Enumerable.Range(1, s.Length - 1).Count(i => (s[i - 1] < 0) != (s[i] < 0));
}

public class DspTests
{
    [Fact]
    public void Resampler_48k_to_16k_preserves_frequency_and_duration()
    {
        var input = Signals.Sine(1000, 1.0, 48000);
        var output = StreamingResampler.Resample(input, 48000, 16000);
        Assert.InRange(output.Length, 15990, 16010);
        // 1 kHz → about 2000 zero crossings per second.
        Assert.InRange(Signals.ZeroCrossings(output[100..^100]), 1950, 2010);
        Assert.InRange(AudioMath.Rms(output[200..^200]), 0.19f, 0.23f);
    }

    [Fact]
    public void Resampler_removes_content_above_the_new_nyquist()
    {
        var input = Signals.Sine(12000, 1.0, 48000);
        var output = StreamingResampler.Resample(input, 48000, 16000);
        Assert.True(AudioMath.Rms(output[200..^200]) < 0.02f, "12 kHz must be filtered before downsampling to 16 kHz");
    }

    [Fact]
    public void Streaming_resampler_is_seamless_across_chunks()
    {
        var input = Signals.Sine(440, 0.5, 44100);
        var r = new StreamingResampler(44100, 16000);
        var chunks = new List<float>();
        for (int i = 0; i < input.Length; i += 441)
        {
            chunks.AddRange(r.Process(input.AsSpan(i, Math.Min(441, input.Length - i))));
        }

        var whole = StreamingResampler.Resample(input, 44100, 16000);
        for (int i = 100; i < chunks.Count - 100; i++)
        {
            Assert.InRange(chunks[i] - whole[i], -0.01f, 0.01f);
        }
    }

    [Fact]
    public void HighPass_removes_hum_and_dc_but_keeps_voice_band()
    {
        var hum = Signals.Sine(50, 1);
        for (int i = 0; i < hum.Length; i++) { hum[i] += 0.2f; }
        new HighPassFilter(16000).Process(hum);
        Assert.True(AudioMath.Rms(hum[4000..]) < 0.1f);

        var voice = Signals.Sine(1000, 1);
        new HighPassFilter(16000).Process(voice);
        Assert.InRange(AudioMath.Rms(voice[4000..]), 0.2f, 0.22f);
    }

    [Fact]
    public void Wav_roundtrip_keeps_samples()
    {
        var dir = TestModels.NewTempDir();
        var path = Path.Combine(dir, "t.wav");
        var samples = Signals.Sine(300, 0.25, 22050);
        WavFile.WriteMono16(path, samples, 22050);
        var read = WavFile.ReadMono(path);
        Assert.Equal(22050, read.SampleRate);
        Assert.Equal(samples.Length, read.Samples.Length);
        Assert.InRange(read.Samples.Zip(samples, (a, b) => Math.Abs(a - b)).Max(), 0, 0.0002f);
    }

    [Fact]
    public void Reads_real_fleurs_wav()
    {
        var wav = WavFile.ReadMono(Path.Combine(TestModels.TestDataDir, "fleurs", "fleurs_bg_1.wav"));
        Assert.Equal(16000, wav.SampleRate);
        Assert.InRange(wav.Duration.TotalSeconds, 3, 6);
        Assert.True(AudioMath.Rms(wav.Samples) > 0.005f);
    }
}

public class VadTests
{
    [Fact]
    public void Detects_start_and_end_of_speech_after_silence()
    {
        var vad = new EnergyVoiceActivityDetector(0.5f);
        var signal = Signals.Concat(Signals.Noise(1.0, 0.002f), Signals.Speechlike(1.5), Signals.Noise(1.5, 0.002f));
        var events = new List<(VadEvent Evt, int Frame)>();
        int frame = 0;
        vad.Process(signal, (e, _) => { if (e != VadEvent.None) { events.Add((e, frame)); } frame++; });
        Assert.Equal([VadEvent.SpeechStart, VadEvent.SpeechEnd], events.Select(e => e.Evt));
        var startSec = events[0].Frame * 0.03;
        var endSec = events[1].Frame * 0.03;
        Assert.InRange(startSec, 1.0, 1.4);
        Assert.InRange(endSec, 2.5, 3.6);
    }

    [Fact]
    public void Steady_background_noise_is_not_speech()
    {
        var vad = new EnergyVoiceActivityDetector(0.5f);
        var events = new List<VadEvent>();
        vad.Process(Signals.Noise(5, 0.02f), (e, _) => { if (e != VadEvent.None) { events.Add(e); } });
        Assert.Empty(events);
    }

    [Fact]
    public void Higher_sensitivity_lowers_thresholds()
    {
        var low = new EnergyVoiceActivityDetector(0f);
        var high = new EnergyVoiceActivityDetector(1f);
        Assert.True(high.MarginDb < low.MarginDb);
        Assert.True(high.AbsoluteFloorDb < low.AbsoluteFloorDb);
    }
}

public class VoiceListenerTests
{
    private static (VoiceListener Listener, SimulatedAudioInput Input, TestSettingsStore Settings) Create(Action<VedarSettings>? configure = null)
    {
        var settings = new VedarSettings();
        configure?.Invoke(settings);
        var store = new TestSettingsStore(settings);
        var input = new SimulatedAudioInput(new AudioDeviceInfo("mic-a", "Микрофон А", true), new AudioDeviceInfo("mic-b", "Микрофон Б", false));
        return (new VoiceListener(input, store, Log.Null<VoiceListener>()), input, store);
    }

    // The trailing silence must be longer than the end-of-speech pause (1.4 s by default).
    private static float[] Utterance() => Signals.Concat(Signals.Noise(0.6, 0.002f), Signals.Speechlike(1.2), Signals.Noise(2.0, 0.002f));

    [Fact]
    public async Task Captures_one_utterance_in_command_mode()
    {
        var (listener, input, _) = Create();
        var captured = new List<UtteranceEventArgs>();
        listener.UtteranceCaptured += (_, e) => captured.Add(e);
        await listener.SetModeAsync(ListeningMode.Command, CancellationToken.None);
        await input.FeedAsync(Utterance());
        var u = Assert.Single(captured);
        Assert.Equal(ListeningMode.Command, u.Mode);
        Assert.InRange(u.Duration.TotalSeconds, 1.2, 3.3); // speech + pre-roll + the end-of-speech pause
    }

    [Theory]
    [InlineData(1400, 1)] // default: a one-second pause to think does not end the sentence
    [InlineData(600, 2)]  // the shortest setting splits it
    public async Task A_pause_to_think_does_not_cut_the_sentence(int pauseMs, int expectedUtterances)
    {
        var (listener, input, _) = Create(s => s.Activation.EndOfSpeechPauseMs = pauseMs);
        var captured = new List<UtteranceEventArgs>();
        listener.UtteranceCaptured += (_, e) => captured.Add(e);
        await listener.SetModeAsync(ListeningMode.Command, CancellationToken.None);
        // "Искам да говорим" … 1 s … "за историята на България"
        await input.FeedAsync(Signals.Concat(Signals.Noise(0.6, 0.002f), Signals.Speechlike(1.2), Signals.Noise(1.0, 0.002f), Signals.Speechlike(1.5), Signals.Noise(3.5, 0.002f)));
        Assert.Equal(expectedUtterances, captured.Count);
    }

    [Fact]
    public async Task A_short_pause_offers_the_audio_so_far_and_going_on_is_reported()
    {
        var (listener, input, _) = Create();
        var paused = new List<UtteranceEventArgs>();
        var resumed = 0;
        var captured = new List<UtteranceEventArgs>();
        listener.SpeechPaused += (_, e) => paused.Add(e);
        listener.SpeechResumed += (_, _) => resumed++;
        listener.UtteranceCaptured += (_, e) => captured.Add(e);
        await listener.SetModeAsync(ListeningMode.Command, CancellationToken.None);

        // "Разкажи ми за" … 0.8 s … "Васил Левски" … the end.
        await input.FeedAsync(Signals.Concat(Signals.Noise(0.6, 0.002f), Signals.Speechlike(1.2), Signals.Noise(0.8, 0.002f), Signals.Speechlike(1.0), Signals.Noise(2.0, 0.002f)));
        Assert.Equal(2, paused.Count); // after each part
        Assert.InRange(paused[0].Duration.TotalSeconds, 1.2, 2.2);
        Assert.Equal(1, resumed);
        var u = Assert.Single(captured); // still one sentence
        Assert.True(u.Duration.TotalSeconds > paused[1].Duration.TotalSeconds);
    }

    [Fact]
    public async Task Commit_at_silence_ends_a_complete_sentence_after_a_shorter_pause()
    {
        var (listener, input, _) = Create();
        var captured = new List<UtteranceEventArgs>();
        listener.UtteranceCaptured += (_, e) => captured.Add(e);
        listener.SpeechPaused += (_, _) => listener.CommitAtSilence(800); // "Колко е часът?" was recognized as complete
        await listener.SetModeAsync(ListeningMode.Command, CancellationToken.None);

        await input.FeedAsync(Signals.Concat(Signals.Noise(0.6, 0.002f), Signals.Speechlike(1.2), Signals.Noise(0.9, 0.002f)));
        var u = Assert.Single(captured); // 0.9 s of silence was enough (the full pause is 1.4 s)
        Assert.InRange(u.Duration.TotalSeconds, 1.9, 2.5);
    }

    [Fact]
    public async Task A_planned_early_end_is_dropped_when_the_user_goes_on()
    {
        var (listener, input, _) = Create();
        var captured = new List<UtteranceEventArgs>();
        listener.UtteranceCaptured += (_, e) => captured.Add(e);
        listener.SpeechPaused += (_, _) => listener.CommitAtSilence(800);
        await listener.SetModeAsync(ListeningMode.Command, CancellationToken.None);

        await input.FeedAsync(Signals.Concat(Signals.Noise(0.6, 0.002f), Signals.Speechlike(1.2), Signals.Noise(0.6, 0.002f), Signals.Speechlike(1.2), Signals.Noise(2.0, 0.002f)));
        var u = Assert.Single(captured);
        Assert.True(u.Duration.TotalSeconds > 3.0, $"{u.Duration.TotalSeconds:F2} s"); // both parts, one sentence
    }

    [Fact]
    public async Task No_pause_signals_while_scanning_for_the_wake_word()
    {
        var (listener, input, _) = Create();
        var paused = 0;
        listener.SpeechPaused += (_, _) => paused++;
        await listener.SetModeAsync(ListeningMode.WakeWord, CancellationToken.None);
        await input.FeedAsync(Utterance());
        Assert.Equal(0, paused);
    }

    [Fact]
    public async Task The_listening_window_can_be_longer_than_the_command_timeout()
    {
        var (listener, _, _) = Create(s => s.Activation.CommandListenTimeoutSeconds = 3);
        var timedOut = new TaskCompletionSource();
        listener.CommandTimeout += (_, _) => timedOut.TrySetResult();
        listener.ListeningWindowSeconds = 5; // listening quietly after an answer
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await listener.SetModeAsync(ListeningMode.Command, CancellationToken.None);
        await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(9));
        Assert.InRange(sw.Elapsed.TotalSeconds, 4.5, 8.0);
    }

    [Fact]
    public async Task Continuation_pieces_include_the_beginning_and_end_after_the_normal_pause()
    {
        var (listener, input, _) = Create();
        var pieces = new List<UtteranceEventArgs>();
        var starts = 0;
        listener.BargeInCaptured += (_, e) => pieces.Add(e);
        listener.BargeInSpeechStarted += (_, _) => starts++;
        await listener.SetModeAsync(ListeningMode.Command, CancellationToken.None);
        listener.Suppressed = true;
        listener.BargeInContinuation = true;
        listener.BargeInEnabled = true;
        // 6 s of continuation with a short pause: one piece (not cut at 4 s, not cut by the 0.4 s pause).
        await input.FeedAsync(Signals.Concat(Signals.Noise(0.5, 0.002f), Signals.Speechlike(3.0), Signals.Noise(0.4, 0.002f), Signals.Speechlike(3.0), Signals.Noise(2.5, 0.002f)));
        Assert.Equal(1, starts);
        var piece = Assert.Single(pieces);
        Assert.True(piece.Duration.TotalSeconds > 6.0, $"{piece.Duration.TotalSeconds:F2} s");
    }

    [Fact]
    public async Task While_suppressed_only_barge_in_pieces_are_reported()
    {
        var (listener, input, _) = Create();
        var utterances = 0;
        var pieces = new List<UtteranceEventArgs>();
        listener.UtteranceCaptured += (_, _) => utterances++;
        listener.BargeInCaptured += (_, e) => pieces.Add(e);
        await listener.SetModeAsync(ListeningMode.WakeWord, CancellationToken.None);
        listener.Suppressed = true;

        await input.FeedAsync(Utterance());
        Assert.Empty(pieces); // barge-in is off

        listener.BargeInEnabled = true;
        await input.FeedAsync(Utterance());
        listener.BargeInEnabled = false;
        await input.FeedAsync(Utterance());

        Assert.Equal(0, utterances); // normal commands stay suppressed while VEDAR speaks
        var piece = Assert.Single(pieces);
        Assert.InRange(piece.Duration.TotalSeconds, 1.0, 2.0);
    }

    [Fact]
    public async Task Barge_in_pieces_are_at_most_four_seconds()
    {
        var (listener, input, _) = Create();
        var pieces = new List<UtteranceEventArgs>();
        listener.BargeInCaptured += (_, e) => pieces.Add(e);
        await listener.SetModeAsync(ListeningMode.Command, CancellationToken.None);
        listener.Suppressed = true;
        listener.BargeInEnabled = true;
        await input.FeedAsync(Signals.Concat(Signals.Noise(0.5, 0.002f), Signals.Speechlike(9), Signals.Noise(1, 0.002f)));
        Assert.True(pieces.Count >= 2, $"{pieces.Count} pieces");
        Assert.All(pieces, p => Assert.True(p.Duration.TotalSeconds <= 4.05, $"{p.Duration.TotalSeconds:F2} s")); // 4 s + one 30 ms frame
    }

    [Fact]
    public async Task Discards_audio_while_suppressed()
    {
        var (listener, input, _) = Create();
        var count = 0;
        listener.UtteranceCaptured += (_, _) => count++;
        await listener.SetModeAsync(ListeningMode.WakeWord, CancellationToken.None);
        listener.Suppressed = true;
        await input.FeedAsync(Utterance());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Off_mode_closes_the_microphone()
    {
        var (listener, input, _) = Create();
        await listener.SetModeAsync(ListeningMode.WakeWord, CancellationToken.None);
        Assert.True(input.IsCapturing);
        await listener.SetModeAsync(ListeningMode.Off, CancellationToken.None);
        Assert.False(input.IsCapturing);
    }

    [Fact]
    public async Task Muted_setting_never_opens_the_microphone()
    {
        var (listener, input, _) = Create(s => s.Microphone.Muted = true);
        await listener.SetModeAsync(ListeningMode.Command, CancellationToken.None);
        Assert.False(input.IsCapturing);
        Assert.Equal(ListeningMode.Off, listener.Mode);
    }

    [Fact]
    public async Task Raises_timeout_when_nobody_speaks()
    {
        var (listener, _, _) = Create(s => s.Activation.CommandListenTimeoutSeconds = 3);
        var timedOut = new TaskCompletionSource();
        listener.CommandTimeout += (_, _) => timedOut.TrySetResult();
        await listener.SetModeAsync(ListeningMode.Command, CancellationToken.None);
        var finished = await Task.WhenAny(timedOut.Task, Task.Delay(6000));
        Assert.Same(timedOut.Task, finished);
    }

    [Fact]
    public async Task Switching_the_microphone_in_settings_reopens_the_selected_device()
    {
        var (listener, input, settings) = Create();
        await listener.SetModeAsync(ListeningMode.WakeWord, CancellationToken.None);
        Assert.Equal("mic-a", input.CurrentDeviceId);

        var s = settings.Current;
        s.Microphone.DeviceId = "mic-b";
        settings.Save(s);
        await listener.RestartAsync(CancellationToken.None);
        Assert.Equal("mic-b", input.CurrentDeviceId);
        Assert.Equal(["mic-a", "mic-b"], input.StartedDevices);
    }

    [Fact]
    public async Task Missing_selected_microphone_falls_back_to_default()
    {
        var (listener, input, _) = Create(s => s.Microphone.DeviceId = "unplugged-usb-mic");
        await listener.SetModeAsync(ListeningMode.WakeWord, CancellationToken.None);
        Assert.Equal("mic-a", input.CurrentDeviceId);
    }

    [Fact]
    public async Task Device_loss_is_reported()
    {
        var (listener, input, _) = Create();
        var errors = 0;
        listener.MicrophoneError += (_, _) => errors++;
        await listener.SetModeAsync(ListeningMode.WakeWord, CancellationToken.None);
        input.SimulateDeviceLoss();
        Assert.Equal(1, errors);
    }

    [Fact]
    public async Task Finish_utterance_emits_what_was_said_so_far()
    {
        var (listener, input, _) = Create();
        var captured = new List<UtteranceEventArgs>();
        listener.UtteranceCaptured += (_, e) => captured.Add(e);
        await listener.SetModeAsync(ListeningMode.Command, CancellationToken.None);
        await input.FeedAsync(Signals.Concat(Signals.Noise(0.6, 0.002f), Signals.Speechlike(1.0)));
        listener.FinishUtterance();
        var u = Assert.Single(captured);
        Assert.False(u.EndedBySilence);
    }
}
