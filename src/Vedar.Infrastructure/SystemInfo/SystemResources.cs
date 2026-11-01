namespace Vedar.Infrastructure.SystemInfo;

public sealed record ResourceReport(double TotalMemoryGb, int LogicalProcessors, bool SufficientForLocalAi, string RecommendedOllamaModel, string Explanation);

/// <summary>Checks whether the computer can run a local AI model.</summary>
public static class SystemResources
{
    public static double TotalMemoryGb() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024;

    public static int RecommendedWhisperThreads() => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    public static ResourceReport Evaluate(double? totalMemoryGb = null, int? processors = null)
    {
        var mem = totalMemoryGb ?? TotalMemoryGb();
        var cpu = processors ?? Environment.ProcessorCount;
        if (mem < 7.5)
        {
            return new ResourceReport(mem, cpu, false, string.Empty,
                "Паметта не е достатъчна за локален модел. Използвай BgGPT или само местните команди.");
        }

        if (mem < 15.5)
        {
            return new ResourceReport(mem, cpu, true, "gemma3:4b",
                "Подходящ е малък многоезичен модел, например gemma3:4b (около 3,3 GB). Отговорите ще са по-бавни от BgGPT.");
        }

        return new ResourceReport(mem, cpu, true, "gemma3:12b",
            "Може да се използва по-голям многоезичен модел, например gemma3:12b (около 8 GB).");
    }
}
