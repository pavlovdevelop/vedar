namespace Vedar.Core.Orchestration;

public enum Earcon { ListenStart, ListenEnd, Cancel, Error, Thinking }

/// <summary>
/// Short non-verbal tones. They tell a blind user that the microphone opened or closed
/// without adding words. Generated in code (no audio files to license).
/// </summary>
public static class Earcons
{
    public const int SampleRate = 22050;

    public static float[] Create(Earcon earcon) => earcon switch
    {
        Earcon.ListenStart => Sequence((660, 70), (880, 90)),
        Earcon.ListenEnd => Sequence((880, 60), (660, 70)),
        Earcon.Cancel => Sequence((520, 70), (390, 110)),
        Earcon.Error => Sequence((330, 160), (262, 200)),
        Earcon.Thinking => Sequence((523, 50)),
        _ => [],
    };

    private static float[] Sequence(params (double Freq, int Ms)[] tones)
    {
        var samples = new List<float>();
        foreach (var (freq, ms) in tones)
        {
            int n = SampleRate * ms / 1000;
            int fade = Math.Min(n / 4, SampleRate * 8 / 1000);
            for (int i = 0; i < n; i++)
            {
                double env = 1.0;
                if (i < fade)
                {
                    env = i / (double)fade;
                }
                else if (i > n - fade)
                {
                    env = (n - i) / (double)fade;
                }

                samples.Add((float)(0.25 * env * Math.Sin(2 * Math.PI * freq * i / SampleRate)));
            }

            samples.AddRange(new float[SampleRate * 15 / 1000]);
        }

        return samples.ToArray();
    }
}
