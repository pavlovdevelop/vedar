using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Vedar.Infrastructure.Models;

public sealed class ModelIntegrityException(string message) : Exception(message);

/// <summary>
/// Downloads a model to a temporary file, verifies its SHA-256 and only then moves it into place.
/// A failed or cancelled download never leaves a partial model behind.
/// </summary>
public sealed class ModelDownloader
{
    private readonly HttpClient _http;
    private readonly ILogger<ModelDownloader> _logger;

    public ModelDownloader(HttpClient http, ILogger<ModelDownloader> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <param name="progress">Reports 0..1.</param>
    public async Task<string> DownloadAsync(ModelInfo model, string modelsRoot, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var target = Path.Combine(modelsRoot, model.RelativePath);
        if (File.Exists(target) && await VerifyAsync(target, model.Sha256, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(1);
            return target;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".part";
        _logger.LogInformation("Downloading model {Id} from {Url}", model.Id, model.Url);
        try
        {
            using var response = await _http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? model.SizeBytes;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                double lastReported = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    var fraction = total > 0 ? Math.Min(1, done / (double)total) : 0;
                    if (fraction - lastReported >= 0.01)
                    {
                        lastReported = fraction;
                        progress?.Report(fraction);
                    }
                }
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelIntegrityException($"SHA-256 mismatch for {model.Id}: expected {model.Sha256}, got {actual}.");
            }

            File.Move(temp, target, overwrite: true);
            progress?.Report(1);
            _logger.LogInformation("Model {Id} downloaded and verified.", model.Id);
            return target;
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { /* best effort */ }
            }
        }
    }

    public static async Task<bool> VerifyAsync(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}
