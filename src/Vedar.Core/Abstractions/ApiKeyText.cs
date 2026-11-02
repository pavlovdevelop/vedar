using System.Globalization;
using System.Text;

namespace Vedar.Core.Abstractions;

/// <summary>
/// Cleans an API key pasted by the user. Keys copied from e-mails, web pages or documentation often carry
/// quotes, an "Authorization: Bearer" prefix, line breaks or invisible characters, and the server then rejects
/// them. API keys never contain spaces, so all of that can be removed safely.
/// </summary>
public static class ApiKeyText
{
    private static readonly char[] Quotes = ['"', '\'', '`', '“', '”', '„', '‘', '’', '«', '»'];

    public static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return Clean(key);
    }

    /// <summary>Removes white space, control and invisible formatting characters (zero-width space, BOM…).</summary>
    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Trim().Trim(Quotes))
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                sb.Append(' ');
                continue;
            }

            sb.Append(c);
        }

        // Keep single spaces for now so "Bearer <key>" can be recognised, then drop them.
        var parts = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("Authorization:", StringComparison.OrdinalIgnoreCase))
        {
            parts = parts[1..];
        }

        if (parts.Length >= 2 && parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            parts = parts[1..];
        }

        return string.Concat(parts).Trim(Quotes);
    }
}
