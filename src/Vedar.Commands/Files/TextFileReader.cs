using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Vedar.Commands.Files;

public sealed record FileReadResult(string Text, bool Truncated, string FileName);

/// <summary>
/// Reads a file the user explicitly selected. Supports plain text (UTF-8, UTF-16, legacy Windows-1251 Cyrillic)
/// and Word .docx. The file is only read locally — never uploaded.
/// </summary>
public sealed class TextFileReader
{
    public const int MaxCharacters = 20_000;
    public const long MaxBytes = 20 * 1024 * 1024;
    public static readonly string[] TextExtensions = [".txt", ".md", ".csv", ".log", ".srt", ".ini", ".json", ".xml", ".html", ".htm"];
    public static readonly string[] SupportedExtensions = [.. TextExtensions, ".docx"];

    static TextFileReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public FileReadResult Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("File not found.", path);
        }

        if (info.Length > MaxBytes)
        {
            throw new InvalidDataException("File too large.");
        }

        var ext = info.Extension.ToLowerInvariant();
        var text = ext switch
        {
            ".docx" => ReadDocx(path),
            _ when TextExtensions.Contains(ext) => DecodeText(File.ReadAllBytes(path)),
            _ => throw new NotSupportedException($"Unsupported file type {ext}."),
        };

        if (ext is ".html" or ".htm" or ".xml")
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var truncated = text.Length > MaxCharacters;
        if (truncated)
        {
            var cut = text.LastIndexOfAny(['.', '!', '?', '\n'], MaxCharacters);
            text = text[..(cut > MaxCharacters / 2 ? cut + 1 : MaxCharacters)];
        }

        return new FileReadResult(text, truncated, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Detects BOM, then validates UTF-8; otherwise assumes Windows-1251 (common for old Bulgarian files).</summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static string ReadDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Not a Word document.");
        using var stream = entry.Open();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        var sb = new StringBuilder();
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == W)
            {
                switch (reader.LocalName)
                {
                    case "t":
                        sb.Append(reader.ReadElementContentAsString());
                        break;
                    case "tab":
                        sb.Append(' ');
                        break;
                    case "br":
                    case "cr":
                        sb.Append('\n');
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.NamespaceURI == W && reader.LocalName == "p")
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }
}
