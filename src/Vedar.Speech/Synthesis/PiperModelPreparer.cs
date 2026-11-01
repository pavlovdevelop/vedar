using System.Text;
using System.Text.Json;

namespace Vedar.Speech.Synthesis;

public sealed record PiperVoiceConfig(int SampleRate, string EspeakVoice, int NumSpeakers, float NoiseScale, float LengthScale, float NoiseW, IReadOnlyList<(string Symbol, int Id)> PhonemeIds);

public sealed record PreparedPiperModel(string ModelPath, string TokensPath, string EspeakDataDir, PiperVoiceConfig Config);

/// <summary>
/// The sherpa-onnx runtime needs a few metadata entries inside the ONNX file and a tokens.txt.
/// This class adds them to a copy of the official Piper voice (rhasspy/piper-voices). The neural network
/// weights are not changed: ONNX is protobuf, and appending a <c>metadata_props</c> field (number 14 of
/// ModelProto) to the end of the file is equivalent to adding the entries with the onnx Python package.
/// </summary>
public static class PiperModelPreparer
{
    public const string PreparedSuffix = ".sherpa.onnx";

    public static PiperVoiceConfig ReadConfig(string configJsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(configJsonPath));
        var root = doc.RootElement;
        var inference = root.TryGetProperty("inference", out var inf) ? inf : default;
        float Get(string name, float fallback) =>
            inference.ValueKind == JsonValueKind.Object && inference.TryGetProperty(name, out var v) ? v.GetSingle() : fallback;

        var ids = root.GetProperty("phoneme_id_map").EnumerateObject().Select(p => (p.Name, p.Value[0].GetInt32())).ToList();
        return new PiperVoiceConfig(
            root.GetProperty("audio").GetProperty("sample_rate").GetInt32(),
            root.GetProperty("espeak").GetProperty("voice").GetString() ?? "bg",
            root.TryGetProperty("num_speakers", out var ns) ? ns.GetInt32() : 1,
            Get("noise_scale", 0.667f),
            Get("length_scale", 1.0f),
            Get("noise_w", 0.8f),
            ids);
    }

    /// <summary>
    /// Prepares the voice in <paramref name="voiceDir"/>. The installer ships only the prepared model
    /// (<c>{voice}.sherpa.onnx</c> + tokens.txt + config); a developer checkout has the original <c>{voice}.onnx</c>.
    /// </summary>
    public static PreparedPiperModel PrepareVoice(string voiceDir, string voiceId, string espeakDataDir, string cacheDir)
    {
        var json = Path.Combine(voiceDir, voiceId + ".onnx.json");
        if (!File.Exists(json))
        {
            throw new FileNotFoundException("Piper voice configuration not found.", json);
        }

        var shipped = Path.Combine(voiceDir, voiceId + PreparedSuffix);
        var tokens = Path.Combine(voiceDir, "tokens.txt");
        if (File.Exists(shipped) && File.Exists(tokens))
        {
            if (!Directory.Exists(espeakDataDir))
            {
                throw new DirectoryNotFoundException($"espeak-ng data not found: {espeakDataDir}");
            }

            return new PreparedPiperModel(shipped, tokens, espeakDataDir, ReadConfig(json));
        }

        return Prepare(Path.Combine(voiceDir, voiceId + ".onnx"), json, espeakDataDir, cacheDir);
    }

    /// <summary>
    /// Returns a model ready for sherpa-onnx. Uses a pre-prepared sibling (shipped by the installer) when present,
    /// otherwise prepares a copy in <paramref name="cacheDir"/>.
    /// </summary>
    public static PreparedPiperModel Prepare(string onnxPath, string configJsonPath, string espeakDataDir, string cacheDir)
    {
        if (!File.Exists(onnxPath))
        {
            throw new FileNotFoundException("Piper voice model not found.", onnxPath);
        }

        if (!Directory.Exists(espeakDataDir))
        {
            throw new DirectoryNotFoundException($"espeak-ng data not found: {espeakDataDir}");
        }

        var config = ReadConfig(configJsonPath);
        var baseName = Path.GetFileNameWithoutExtension(onnxPath);
        var dir = Path.GetDirectoryName(onnxPath)!;
        var shipped = Path.Combine(dir, baseName + PreparedSuffix);
        var shippedTokens = Path.Combine(dir, "tokens.txt");
        if (File.Exists(shipped) && File.Exists(shippedTokens))
        {
            return new PreparedPiperModel(shipped, shippedTokens, espeakDataDir, config);
        }

        var info = new FileInfo(onnxPath);
        var key = $"{baseName}-{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}";
        var targetDir = Path.Combine(cacheDir, "piper", key);
        var model = Path.Combine(targetDir, baseName + PreparedSuffix);
        var tokens = Path.Combine(targetDir, "tokens.txt");
        if (!File.Exists(model) || !File.Exists(tokens))
        {
            Directory.CreateDirectory(targetDir);
            WritePrepared(onnxPath, config, model, tokens);
        }

        return new PreparedPiperModel(model, tokens, espeakDataDir, config);
    }

    public static void WritePrepared(string sourceOnnx, PiperVoiceConfig config, string targetModel, string targetTokens)
    {
        var temp = targetModel + ".tmp";
        File.Copy(sourceOnnx, temp, overwrite: true);
        using (var fs = new FileStream(temp, FileMode.Append, FileAccess.Write))
        {
            AppendMetadata(fs, BuildMetadata(config));
        }

        File.Move(temp, targetModel, overwrite: true);
        WriteTokens(config, targetTokens);
    }

    public static Dictionary<string, string> BuildMetadata(PiperVoiceConfig config) => new()
    {
        ["model_type"] = "vits",
        ["comment"] = "piper",
        ["language"] = config.EspeakVoice.StartsWith("bg", StringComparison.OrdinalIgnoreCase) ? "Bulgarian" : config.EspeakVoice,
        ["voice"] = config.EspeakVoice,
        ["has_espeak"] = "1",
        ["n_speakers"] = config.NumSpeakers.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["sample_rate"] = config.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public static void WriteTokens(PiperVoiceConfig config, string path)
    {
        var sb = new StringBuilder();
        foreach (var (symbol, id) in config.PhonemeIds)
        {
            sb.Append(symbol).Append(' ').Append(id).Append('\n');
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Appends ModelProto.metadata_props (field 14, StringStringEntryProto{key=1, value=2}) entries.</summary>
    public static void AppendMetadata(Stream stream, IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var (key, value) in metadata)
        {
            using var entry = new MemoryStream();
            WriteString(entry, 1, key);
            WriteString(entry, 2, value);
            stream.WriteByte((14 << 3) | 2);
            WriteVarint(stream, (ulong)entry.Length);
            entry.Position = 0;
            entry.CopyTo(stream);
        }
    }

    /// <summary>Reads metadata_props entries back (top-level field 14) — used to verify prepared models.</summary>
    public static Dictionary<string, string> ReadMetadata(string onnxPath)
    {
        var result = new Dictionary<string, string>();
        using var fs = File.OpenRead(onnxPath);
        while (fs.Position < fs.Length)
        {
            var tag = ReadVarint(fs);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            switch (wire)
            {
                case 0:
                    ReadVarint(fs);
                    break;
                case 1:
                    fs.Seek(8, SeekOrigin.Current);
                    break;
                case 5:
                    fs.Seek(4, SeekOrigin.Current);
                    break;
                case 2:
                    var len = (long)ReadVarint(fs);
                    if (field == 14)
                    {
                        var buf = new byte[len];
                        fs.ReadExactly(buf);
                        var (k, v) = ParseEntry(buf);
                        if (k is not null)
                        {
                            result[k] = v ?? string.Empty;
                        }
                    }
                    else
                    {
                        fs.Seek(len, SeekOrigin.Current);
                    }

                    break;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {wire}.");
            }
        }

        return result;
    }

    private static (string? Key, string? Value) ParseEntry(byte[] buf)
    {
        using var ms = new MemoryStream(buf);
        string? key = null, value = null;
        while (ms.Position < ms.Length)
        {
            var tag = ReadVarint(ms);
            var len = (int)ReadVarint(ms);
            var bytes = new byte[len];
            ms.ReadExactly(bytes);
            if ((tag >> 3) == 1)
            {
                key = Encoding.UTF8.GetString(bytes);
            }
            else if ((tag >> 3) == 2)
            {
                value = Encoding.UTF8.GetString(bytes);
            }
        }

        return (key, value);
    }

    private static void WriteString(Stream s, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        s.WriteByte((byte)((field << 3) | 2));
        WriteVarint(s, (ulong)bytes.Length);
        s.Write(bytes);
    }

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }

        s.WriteByte((byte)v);
    }

    private static ulong ReadVarint(Stream s)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException();
            }

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }
}
