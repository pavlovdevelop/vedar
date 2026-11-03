using Vedar.Audio.Dsp;

namespace Vedar.Speech.Vad;

/// <summary>
/// Notices the user starting to speak while VEDAR's own voice comes out of the loudspeakers.
/// </summary>
/// <remarks>
/// <para>There is no echo cancellation. Instead the detector uses what VEDAR knows exactly: the audio it is
/// playing. Its level (30 ms frames), shifted by the playback delay and widened for the room's reverberation,
/// predicts how loud VEDAR's own voice is in the microphone (the "echo"). How much quieter the echo is than the
/// played audio (the echo gain) is learned while VEDAR speaks, and the delay is found by comparing the two
/// level curves. A microphone frame counts as the user's when it is clearly louder than the predicted echo and
/// the background noise; enough such frames in a short time mean the user has started to speak.</para>
/// <para>With headphones there is no echo at all, so any speech counts. With loudspeakers the user has to be
/// louder at the microphone than VEDAR's voice is (usually the case when the user sits closer to the microphone
/// than the speakers are); in VEDAR's short pauses between words even a quieter user is noticed.</para>
/// </remarks>
public sealed class EchoAwareSpeechDetector
{
    public const int FrameSamples = 480; // 30 ms at 16 kHz
    private const double FrameMs = 30;

    /// <summary>Played audio above this level means VEDAR is speaking.</summary>
    private const double ReferenceActiveDb = -50;

    /// <summary>Tunable numbers (the defaults were chosen with simulated rooms, see the tests).</summary>
    public sealed record Options
    {
        /// <summary>The user must be this much louder than VEDAR's typical echo (the median).</summary>
        public double MarginDb { get; init; } = 8;
        /// <summary>… and this much louder than VEDAR's loud echo moments (<see cref="HighPercentile"/>).</summary>
        public double HighMarginDb { get; init; } = 3;
        /// <summary>… and this much louder than the background noise.</summary>
        public double NoiseMarginDb { get; init; } = 9;
        /// <summary>The typical echo gain: this percentile of (microphone − played level) while only VEDAR is heard.</summary>
        public double GainPercentile { get; init; } = 0.5;
        /// <summary>The loud echo moments: this percentile.</summary>
        public double HighPercentile { get; init; } = 0.9;
        /// <summary>Until the delay is measured, the prediction looks this far around the assumed delay.</summary>
        public double InitialToleranceMs { get; init; } = 150;
        /// <summary>Echo of a word is assumed to fade this fast (dB per ms); real rooms fade faster.</summary>
        public double ReverbDecayDbPerMs { get; init; } = 0.06;
        public double ReverbWindowMs { get; init; } = 300;
        /// <summary>The delay is known only roughly; the prediction takes the loudest frame within this tolerance.</summary>
        public double AlignToleranceMs { get; init; } = 45;
        public int WindowFrames { get; init; } = 8;
        public int TriggerFrames { get; init; } = 5;
    }

    private readonly Options _o;

    public EchoAwareSpeechDetector(Options? options = null) => _o = options ?? new Options();
    private const int EchoSamples = 150;  // ~4.5 s of echo frames for the gain estimate
    private const int MinEchoSamples = 20;

    /// <summary>Until the echo gain is learned, assume the echo is as loud as the played audio plus this.</summary>
    private const double UnknownGainDb = 10;

    private readonly List<(double Ms, float Db)> _reference = [];
    private readonly LinkedList<float> _echoDiffs = new();
    private readonly Queue<float> _activeMicLevels = new();
    private readonly Queue<bool> _recentUser = new();
    private readonly List<(double Ms, float Db, bool User)> _micHistory = [];
    private double _delayMs = 150;
    private bool _delayKnown;
    /// <summary>How well the microphone level follows the played level (best lag); null before the first check.</summary>
    private double? _echoCorrelation;
    private double _noiseDb = -60;
    private int _framesSeen;
    private int _framesSinceDelayEstimate;
    private bool _triggered;

    /// <summary>Absolute minimum level for the user's speech (dBFS).</summary>
    public double AbsoluteFloorDb { get; set; } = -53;

    /// <summary>Learned echo gain (dB, microphone level minus played level); null while unknown.</summary>
    public double? EchoGainDb => _echoDiffs.Count >= MinEchoSamples ? Percentile(_echoDiffs, _o.GainPercentile) : null;

    /// <summary>Estimated delay between playing a sound and hearing it in the microphone (ms).</summary>
    public double DelayMs => _delayMs;

    public double NoiseFloorDb => _noiseDb;

    /// <summary>True from the moment the user started speaking until <see cref="Reset"/>.</summary>
    public bool Triggered => _triggered;

    /// <summary>The last frame's numbers (for diagnostics): microphone, predicted echo and noise level, and the decision.</summary>
    public (double MicDb, double EchoDb, double NoiseDb, bool User) LastFrame { get; private set; }

    /// <summary>Audio VEDAR is about to play (any sample rate), and when <see cref="Play"/> was asked to play it.</summary>
    public void AddReference(ReadOnlySpan<float> samples, int sampleRate, double startMs)
    {
        var frame = Math.Max(1, (int)(sampleRate * FrameMs / 1000));
        for (int i = 0, n = 0; i < samples.Length; i += frame, n++)
        {
            var chunk = samples.Slice(i, Math.Min(frame, samples.Length - i));
            _reference.Add((startMs + n * FrameMs, AudioMath.ToDecibels(AudioMath.Rms(chunk))));
        }
    }

    private double _attenuationDb, _previousAttenuationDb, _attenuationFromMs;

    /// <summary>VEDAR's voice was made quieter by <paramref name="db"/> (it gives way while the user speaks) from now on.</summary>
    public void Attenuate(double db, double fromMs)
    {
        _previousAttenuationDb = _attenuationDb;
        _attenuationDb = db;
        _attenuationFromMs = fromMs;
    }

    /// <summary>Forgets the user's speech (a new turn); the learned echo gain and delay are kept.</summary>
    public void Reset()
    {
        _recentUser.Clear();
        _micHistory.Clear();
        _triggered = false;
    }

    /// <summary>One 30 ms microphone frame (16 kHz) captured at about <paramref name="frameMs"/>.</summary>
    /// <returns>True once, when the user starts speaking.</returns>
    public bool Process(ReadOnlySpan<float> frame, double frameMs)
    {
        var mic = (double)AudioMath.ToDecibels(AudioMath.Rms(frame));
        _framesSeen++;
        Prune(frameMs);

        var refMax = ReferenceMax(frameMs);
        var active = refMax > ReferenceActiveDb;
        // A change of VEDAR's volume reaches the microphone after the playback buffer (~100 ms).
        refMax -= frameMs >= _attenuationFromMs + 100 ? _attenuationDb : _previousAttenuationDb;
        UpdateNoise(mic, active);

        double predicted = double.NegativeInfinity, threshold = _noiseDb + _o.NoiseMarginDb;
        if (active && EchoInaudible())
        {
            // Headphones (or speakers too quiet to matter): only the plain level counts — above the noise and
            // above the loudest the microphone ever was while VEDAR alone spoke.
            if (_activeMicLevels.Count >= MinEchoSamples)
            {
                threshold = Math.Max(threshold, Percentile(_activeMicLevels, 0.95) + _o.HighMarginDb);
            }
        }
        else if (active)
        {
            if (_echoDiffs.Count >= MinEchoSamples)
            {
                var sorted = _echoDiffs.OrderBy(v => v).ToArray();
                predicted = refMax + At(sorted, _o.GainPercentile);
                threshold = Math.Max(threshold, Math.Max(predicted + _o.MarginDb, refMax + At(sorted, _o.HighPercentile) + _o.HighMarginDb));
            }
            else
            {
                predicted = refMax + UnknownGainDb; // not learned yet: assume a loud echo
                threshold = Math.Max(threshold, predicted + _o.MarginDb);
            }
        }

        var user = _framesSeen > 10 && mic > threshold && mic > AbsoluteFloorDb;
        LastFrame = (mic, predicted, _noiseDb, user);
        if (active && !user)
        {
            _activeMicLevels.Enqueue((float)mic);
            while (_activeMicLevels.Count > EchoSamples)
            {
                _activeMicLevels.Dequeue();
            }

            if (mic > _noiseDb + 6)
            {
                _echoDiffs.AddLast((float)(mic - refMax));
                if (_echoDiffs.Count > EchoSamples)
                {
                    _echoDiffs.RemoveFirst();
                }
            }
        }

        _micHistory.Add((frameMs, (float)mic, user));
        if (++_framesSinceDelayEstimate >= 33)
        {
            _framesSinceDelayEstimate = 0;
            EstimateDelay();
        }

        Push(_recentUser, user, _o.WindowFrames);
        if (!_triggered && _recentUser.Count(u => u) >= _o.TriggerFrames)
        {
            _triggered = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The microphone does not follow what VEDAR plays (headphones, or speakers too quiet to be heard): the echo
    /// does not matter, only the plain level does.
    /// </summary>
    private bool EchoInaudible() => _echoCorrelation is < 0.3;

    /// <summary>The loudest played frame that can be heard now, with the delay tolerance and the reverberation.</summary>
    private double ReferenceMax(double micMs)
    {
        var tolerance = _delayKnown ? _o.AlignToleranceMs : _o.InitialToleranceMs;
        var latest = micMs - _delayMs + tolerance;
        var earliest = micMs - _delayMs - tolerance - _o.ReverbWindowMs;
        var fadeFrom = micMs - _delayMs - tolerance;
        double best = double.NegativeInfinity;
        for (int i = FirstAtOrAfter(earliest - FrameMs); i < _reference.Count && _reference[i].Ms <= latest; i++)
        {
            var (ms, db) = _reference[i];
            var end = ms + FrameMs;
            var age = Math.Max(0, fadeFrom - end);
            best = Math.Max(best, db - age * _o.ReverbDecayDbPerMs);
        }

        return best;
    }

    private double ReferenceAt(double ms)
    {
        var i = FirstAtOrAfter(ms - FrameMs);
        return i < _reference.Count && _reference[i].Ms <= ms ? _reference[i].Db : -100;
    }

    private int FirstAtOrAfter(double ms)
    {
        int lo = 0, hi = _reference.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_reference[mid].Ms < ms)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>The lag at which the microphone level follows the played level best (only VEDAR speaking).</summary>
    private void EstimateDelay()
    {
        var frames = _micHistory.Where(f => !f.User).ToList();
        if (frames.Count < 40)
        {
            return;
        }

        double bestLag = _delayMs, bestCorr = 0.5, maxCorr = 0;
        var checkedAny = false;
        for (double lag = 0; lag <= 450; lag += FrameMs)
        {
            var pairs = frames.Select(f => (Mic: (double)f.Db, Ref: ReferenceAt(f.Ms - lag))).Where(p => p.Ref > -60).ToList();
            if (pairs.Count < 30)
            {
                continue;
            }

            var corr = Correlation(pairs);
            checkedAny = true;
            maxCorr = Math.Max(maxCorr, corr);
            if (corr > bestCorr)
            {
                bestCorr = corr;
                bestLag = lag;
            }
        }

        if (checkedAny)
        {
            _echoCorrelation = _echoCorrelation is { } previous ? 0.5 * previous + 0.5 * maxCorr : maxCorr;
        }

        if (bestCorr > 0.5 && bestLag != _delayMs)
        {
            _delayMs = _delayKnown ? 0.5 * _delayMs + 0.5 * bestLag : bestLag;
            _delayKnown = true;
        }
        else if (bestCorr > 0.5)
        {
            _delayKnown = true;
        }
    }

    private static double Correlation(List<(double Mic, double Ref)> pairs)
    {
        double mx = pairs.Average(p => p.Mic), my = pairs.Average(p => p.Ref);
        double sxy = 0, sxx = 0, syy = 0;
        foreach (var (x, y) in pairs)
        {
            sxy += (x - mx) * (y - my);
            sxx += (x - mx) * (x - mx);
            syy += (y - my) * (y - my);
        }

        return sxx <= 0 || syy <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }

    private void UpdateNoise(double mic, bool referenceActive)
    {
        if (_framesSeen <= 10)
        {
            _noiseDb = _framesSeen == 1 ? Math.Min(mic, -20) : Math.Min(Math.Max(_noiseDb, mic), -20);
            return;
        }

        if (mic < _noiseDb)
        {
            _noiseDb = 0.7 * _noiseDb + 0.3 * mic;
        }
        else if (!referenceActive && mic < _noiseDb + _o.MarginDb)
        {
            _noiseDb = 0.98 * _noiseDb + 0.02 * mic;
        }
    }

    private void Prune(double nowMs)
    {
        var keepReference = nowMs - 6000;
        var drop = 0;
        while (drop < _reference.Count && _reference[drop].Ms < keepReference)
        {
            drop++;
        }

        if (drop > 0)
        {
            _reference.RemoveRange(0, drop);
        }

        var keepMic = nowMs - 3000;
        drop = 0;
        while (drop < _micHistory.Count && _micHistory[drop].Ms < keepMic)
        {
            drop++;
        }

        if (drop > 0)
        {
            _micHistory.RemoveRange(0, drop);
        }
    }

    private static void Push(Queue<bool> queue, bool value, int max)
    {
        queue.Enqueue(value);
        while (queue.Count > max)
        {
            queue.Dequeue();
        }
    }

    private static double Percentile(IEnumerable<float> values, double p) => At(values.OrderBy(v => v).ToArray(), p);

    private static double At(float[] sorted, double p) => sorted[Math.Clamp((int)Math.Round(p * (sorted.Length - 1)), 0, sorted.Length - 1)];
}
