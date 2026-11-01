namespace Vedar.Infrastructure.Models;

public enum ModelKind { WhisperStt, PiperVoice }

/// <summary>A downloadable model with a pinned SHA-256 so corrupted or tampered downloads are rejected.</summary>
public sealed record ModelInfo(
    string Id,
    string DisplayName,
    ModelKind Kind,
    string RelativePath,
    string Url,
    string Sha256,
    long SizeBytes,
    double MinMemoryGb,
    string Description,
    bool BundledWithInstaller);

/// <summary>
/// Known models. Whisper models: multilingual (not ".en"), from the official whisper.cpp model repository.
/// Hashes are the Hugging Face LFS SHA-256 values.
/// </summary>
public static class ModelCatalog
{
    private const string WhisperBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static readonly IReadOnlyList<ModelInfo> Whisper =
    [
        new("whisper-base-q5_1", "Whisper Base (бърз, по-неточен)", ModelKind.WhisperStt, "whisper/ggml-base-q5_1.bin",
            WhisperBase + "ggml-base-q5_1.bin", "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898", 59_707_625, 2,
            "60 MB. Използва се за бързо откриване на думата „Ведар“.", true),
        new("whisper-small-q5_1", "Whisper Small (препоръчан)", ModelKind.WhisperStt, "whisper/ggml-small-q5_1.bin",
            WhisperBase + "ggml-small-q5_1.bin", "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", 190_085_487, 4,
            "190 MB. Добър баланс между точност и скорост за обикновен компютър.", true),
        new("whisper-medium-q5_0", "Whisper Medium (по-точен, по-бавен)", ModelKind.WhisperStt, "whisper/ggml-medium-q5_0.bin",
            WhisperBase + "ggml-medium-q5_0.bin", "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f", 539_212_467, 8,
            "540 MB. За по-мощни компютри.", false),
        new("whisper-large-v3-turbo-q5_0", "Whisper Large v3 Turbo (най-точен)", ModelKind.WhisperStt, "whisper/ggml-large-v3-turbo-q5_0.bin",
            WhisperBase + "ggml-large-v3-turbo-q5_0.bin", "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", 574_041_195, 8,
            "575 MB. Най-добро качество; препоръчва се 8 ядра и 8 GB памет.", false),
    ];

    public static ModelInfo? FindWhisperByFile(string fileName) =>
        Whisper.FirstOrDefault(m => string.Equals(Path.GetFileName(m.RelativePath), fileName, StringComparison.OrdinalIgnoreCase));
}
