using System.Runtime.Versioning;
using NAudio.CoreAudioApi;

namespace Vedar.Audio.Windows;

public interface ISystemVolume
{
    float? Get();
    float? Set(float level);
}

/// <summary>Windows master volume of the default output device. VEDAR never mutes the system.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemVolume : ISystemVolume
{
    public float? Get()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            return device.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public float? Set(float level)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            var vol = device.AudioEndpointVolume;
            if (vol.Mute)
            {
                vol.Mute = false;
            }

            vol.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
            return vol.MasterVolumeLevelScalar;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
