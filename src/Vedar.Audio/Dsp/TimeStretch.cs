namespace Vedar.Audio.Dsp;

/// <summary>
/// Changes the tempo of speech without changing its pitch (WSOLA: waveform-similarity overlap-add).
/// </summary>
/// <remarks>
/// Piper's own speed control (VITS length scale) is weak when speeding up: speed 2.0 gives only about 1.3× faster
/// speech, because phoneme durations are rounded to whole frames. Blind users often listen at high speeds, so the
/// synthesizer speaks at the model's natural tempo and this class compresses it by exactly the requested factor.
/// Each output frame is taken from near its nominal input position, shifted by up to one pitch period to the place
/// whose waveform best continues the previous frame, which avoids the "warble" of plain overlap-add.
/// </remarks>
public static class TimeStretch
{
    /// <summary>Returns <paramref name="input"/> played <paramref name="speed"/> times faster (0.25–4), same pitch.</summary>
    /// <param name="sampleRate">Sample rate of the audio; sets the frame length (~23 ms) and search range (~12 ms).</param>
    public static float[] ChangeTempo(ReadOnlySpan<float> input, double speed, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(speed, 0.25);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(speed, 4.0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        var outputLength = (int)Math.Round(input.Length / speed);
        if (Math.Abs(speed - 1.0) < 1e-3 || input.Length == 0)
        {
            return input.ToArray();
        }

        // Frame of ~23 ms with 50% overlap (periodic Hann windows at half-frame hops sum to exactly 1).
        var frame = Math.Max(64, (int)(sampleRate * 0.023) & ~1);
        var hop = frame / 2;
        // Search range: more than one pitch period of a low male voice (~12 ms covers F0 down to ~85 Hz).
        var tolerance = Math.Max(8, (int)(sampleRate * 0.012));

        var window = new float[frame];
        for (int i = 0; i < frame; i++)
        {
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / frame));
        }

        // Zero padding lets every frame (and its search range) read a full window near the start and the end.
        var offset = frame + tolerance;
        var padded = new float[offset + input.Length + 4 * frame + tolerance];
        input.CopyTo(padded.AsSpan(offset));

        var output = new float[outputLength + 2 * frame];
        var weight = new float[output.Length];
        var previous = offset - hop; // input position of the previously copied frame (virtual frame before the start)
        for (int k = 0; k * hop < outputLength + hop; k++)
        {
            var nominal = offset - hop + (int)Math.Round(k * hop * speed);
            var position = k == 0 ? nominal : BestMatch(padded, previous + hop, nominal, frame, tolerance);
            var outStart = k * hop;
            for (int i = 0; i < frame && outStart + i < output.Length; i++)
            {
                output[outStart + i] += padded[position + i] * window[i];
                weight[outStart + i] += window[i];
            }

            previous = position;
        }

        // Output frame k covers output time (k - 1/2)·hop.. so the first half frame is the lead-in; drop it.
        var result = new float[outputLength];
        for (int i = 0; i < outputLength; i++)
        {
            var w = weight[i + hop];
            result[i] = w > 1e-3f ? output[i + hop] / w : 0f;
        }

        return result;
    }

    /// <summary>
    /// Finds the frame start within ±<paramref name="tolerance"/> of <paramref name="nominal"/> whose waveform is most
    /// similar to the natural continuation of the previous frame (starting at <paramref name="continuation"/>).
    /// </summary>
    private static int BestMatch(float[] signal, int continuation, int nominal, int frame, int tolerance)
    {
        var low = Math.Max(0, nominal - tolerance);
        var high = Math.Min(signal.Length - frame, nominal + tolerance);
        if (continuation < 0 || continuation + frame > signal.Length || low > high)
        {
            return Math.Clamp(nominal, 0, signal.Length - frame);
        }

        // Compare the overlapping half only (it is what gets cross-faded) and every second sample: plenty for speech.
        var length = frame / 2;
        var best = nominal;
        var bestScore = double.NegativeInfinity;
        var reference = signal.AsSpan(continuation, length);
        for (int candidate = low; candidate <= high; candidate++)
        {
            var span = signal.AsSpan(candidate, length);
            double dot = 0, energy = 1e-9;
            for (int i = 0; i < length; i += 2)
            {
                dot += reference[i] * span[i];
                energy += span[i] * span[i];
            }

            var score = dot / Math.Sqrt(energy);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }
}
