using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vedar.Core.Abstractions;
using Vedar.Core.Settings;

namespace Vedar.Infrastructure.Configuration;

/// <summary>
/// Stores settings as human-readable JSON. Writes are atomic (temp file + replace).
/// A corrupt file is kept as a backup and defaults are used, so the app always starts.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly object _gate = new();
    private VedarSettings? _current;

    public JsonSettingsStore(AppPaths paths, ILogger<JsonSettingsStore> logger)
    {
        _path = paths.SettingsFile;
        _logger = logger;
    }

    public event EventHandler<VedarSettings>? SettingsChanged;

    public VedarSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= LoadCore();
            }
        }
    }

    public VedarSettings Load()
    {
        lock (_gate)
        {
            _current = LoadCore();
            return _current;
        }
    }

    private VedarSettings LoadCore()
    {
        if (!File.Exists(_path))
        {
            _logger.LogInformation("No settings file; using defaults.");
            return new VedarSettings().Normalize();
        }

        try
        {
            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<VedarSettings>(json, JsonOptions) ?? new VedarSettings();
            return settings.Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var backup = _path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            _logger.LogError(ex, "Settings file is unreadable. Backing it up to {Backup} and using defaults.", backup);
            try
            {
                File.Copy(_path, backup, overwrite: true);
            }
            catch (Exception copyError)
            {
                _logger.LogWarning(copyError, "Could not back up the corrupt settings file.");
            }

            return new VedarSettings().Normalize();
        }
    }

    public void Save(VedarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            settings.Normalize();
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, _path, overwrite: true);
            _current = settings;
        }

        SettingsChanged?.Invoke(this, settings);
    }

    /// <summary>Deep copy via JSON, for editing in a settings dialog without touching the live instance.</summary>
    public static VedarSettings Clone(VedarSettings settings) =>
        JsonSerializer.Deserialize<VedarSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!.Normalize();
}
