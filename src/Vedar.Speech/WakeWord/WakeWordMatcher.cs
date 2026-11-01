using System.Text.RegularExpressions;
using Vedar.Core.Abstractions;
using Vedar.Core.Text;
using Vedar.Speech.Text;

namespace Vedar.Speech.WakeWord;

/// <summary>
/// Text-based wake word detection on top of local Whisper transcripts. Accepts the typical ways Whisper writes
/// "Ведар" (Видар, Ведър, Vedar…) at the start of an utterance, optionally after a filler ("ей", "хей").
/// Real Bulgarian words that look similar (вятър, бодър, ведро, вътре) are rejected.
/// </summary>
public sealed partial class WakeWordMatcher : IWakeWordMatcher
{
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "ей", "хей", "ало", "а", "е", "абе", "добре", "здравей", "здрасти", "о", "ох", "ами", "ъъ", "ъ", "мм", "хм", "ами", "ехо",
    };

    private static readonly HashSet<string> Rejected = new(StringComparer.Ordinal)
    {
        "вятър", "вятъра", "вятърът", "бодър", "бодра", "бодро", "ведро", "ведра", "вътре", "видра", "видри", "федер", "бидър",
    };

    private readonly HashSet<string> _variants;
    private readonly string _wakeWord;

    public WakeWordMatcher(string wakeWord = "Ведар")
    {
        _wakeWord = BulgarianText.NormalizeForMatching(wakeWord);
        _variants = new HashSet<string>(StringComparer.Ordinal)
        {
            _wakeWord, _wakeWord + "е", "ведар", "ведаре", "видар", "въдар", "ведър", "ведор", "вейдър", "вейдар", "бедар", "фидар", "ведарь", "вядар",
        };
    }

    [GeneratedRegex("^[вбф][аеиоуъяюь]{1,2}й?[дт][аеиоуъяю]{1,2}р[ьъеаи]?$")]
    private static partial Regex WakeShape();

    public bool IsWakeToken(string token)
    {
        var t = BulgarianText.NormalizeForMatching(token);
        if (t.Length == 0)
        {
            return false;
        }

        if (t.Any(char.IsAsciiLetter))
        {
            t = BulgarianText.NormalizeForMatching(LatinTransliterator.Transliterate(t));
            if (t is "вадер" or "ведар" or "уедар")
            {
                return true;
            }
        }

        if (Rejected.Contains(t))
        {
            return false;
        }

        if (_variants.Contains(t))
        {
            return true;
        }

        if (_wakeWord == "ведар" && WakeShape().IsMatch(t))
        {
            return true;
        }

        return t.Length >= 4 && FuzzyMatcher.Similarity(t, _wakeWord) >= 0.75;
    }

    public WakeWordMatch Match(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return WakeWordMatch.None;
        }

        // Walk the original text word by word so the command keeps its punctuation and casing.
        var words = WordPattern().Matches(transcript);
        int checkedWords = 0;
        foreach (Match w in words)
        {
            var norm = BulgarianText.NormalizeForMatching(w.Value);
            if (norm.Length == 0)
            {
                continue;
            }

            if (IsWakeToken(w.Value))
            {
                var command = transcript[(w.Index + w.Length)..].TrimStart(' ', ',', '.', '!', '?', ':', ';', '-', '—', '–').Trim();
                return new WakeWordMatch(true, command, w.Value);
            }

            if (!Fillers.Contains(norm) || ++checkedWords >= 3)
            {
                break;
            }
        }

        return WakeWordMatch.None;
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}'-]+")]
    private static partial Regex WordPattern();
}
