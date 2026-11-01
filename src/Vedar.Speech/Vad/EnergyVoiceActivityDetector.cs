using Vedar.Audio.Dsp;

namespace Vedar.Speech.Vad;

public enum VadEvent { None, SpeechStart, SpeechEnd }

/// <summary>
/// Lightweight voice activity detector based on frame energy over an adaptive noise floor.
/// Cheap enough to run continuously (well under 1% of one CPU core). Whisper only runs once an utterance ends.
/// </summary>
public sealed class EnergyVoiceActivityDetector
{
    public const int SampleRate = 16000;
    public const int FrameSamples = 480; // 30 ms

    private readonly float[] _frame = new float[FrameSamples];
    private int _frameFill;
    private float _noiseDb = -60f;
    private int _speechFrames;
    private int _silenceFrames;
    private int _framesSeen;

    /// <param name="sensitivity">0 = only loud, close speech; 1 = reacts to quiet speech (and more noise).</param>
    public EnergyVoiceActivityDetector(float sensitivity = 0.5f)
    {
        Sensitivity = sensitivity;
    }

    public float Sensitivity { get; set; }
    public bool InSpeech { get; private set; }
    public int StartFrames { get; set; } = 4;              // 120 ms of speech to start
    public int HangoverFrames { get; set; } = 27;          // ~800 ms of silence to end
    public float NoiseFloorDb => _noiseDb;
    public float LastFrameDb { get; private set; } = -100f;

    /// <summary>Margin above the noise floor needed to count as speech.</summary>
    public float MarginDb => 15f - 9f * Math.Clamp(Sensitivity, 0f, 1f);

    /// <summary>Absolute minimum level for speech (dBFS).</summary>
    public float AbsoluteFloorDb => -46f - 14f * Math.Clamp(Sensitivity, 0f, 1f);

    public void Reset()
    {
        _frameFill = 0;
        _speechFrames = 0;
        _silenceFrames = 0;
        InSpeech = false;
    }

    /// <summary>Processes samples; the callback receives an event for each complete 30 ms frame.</summary>
    public void Process(ReadOnlySpan<float> samples, Action<VadEvent, float[]> onFrame)
    {
        int i = 0;
        while (i < samples.Length)
        {
            int take = Math.Min(FrameSamples - _frameFill, samples.Length - i);
            samples.Slice(i, take).CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += take;
            i += take;
            if (_frameFill == FrameSamples)
            {
                var frame = (float[])_frame.Clone();
                _frameFill = 0;
                onFrame(ProcessFrame(frame), frame);
            }
        }
    }

    private VadEvent ProcessFrame(float[] frame)
    {
        var db = AudioMath.ToDecibels(AudioMath.Rms(frame));
        LastFrameDb = db;
        _framesSeen++;
        var isSpeech = db > _noiseDb + MarginDb && db > AbsoluteFloorDb;

        if (!InSpeech)
        {
            // Track the noise floor: fall quickly, rise slowly (so speech does not raise it).
            if (db < _noiseDb)
            {
                _noiseDb = 0.7f * _noiseDb + 0.3f * db;
            }
            else if (!isSpeech)
            {
                _noiseDb = 0.98f * _noiseDb + 0.02f * db;
            }

            if (_framesSeen < 10)
            {
                // Initial calibration: never treat the first 300 ms as speech.
                _noiseDb = Math.Min(Math.Max(_noiseDb, db), -20f);
                return VadEvent.None;
            }

            _speechFrames = isSpeech ? _speechFrames + 1 : Math.Max(0, _speechFrames - 1);
            if (_speechFrames >= StartFrames)
            {
                InSpeech = true;
                _silenceFrames = 0;
                return VadEvent.SpeechStart;
            }

            return VadEvent.None;
        }

        _silenceFrames = isSpeech ? 0 : _silenceFrames + 1;
        if (_silenceFrames >= HangoverFrames)
        {
            InSpeech = false;
            _speechFrames = 0;
            _silenceFrames = 0;
            return VadEvent.SpeechEnd;
        }

        return VadEvent.None;
    }
}
