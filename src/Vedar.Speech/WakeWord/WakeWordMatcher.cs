using System.Text.RegularExpressions;
using Vedar.Core.Abstractions;
using Vedar.Core.Text;
using Vedar.Speech.Text;

namespace Vedar.Speech.WakeWord;

/// <summary>
/// Text-based wake word detection on top of local Whisper transcripts. Accepts the typical ways Whisper writes
/// "Ведар" (Видар, Ведър, Vedar…) at the start of an utterance — after a filler ("ей", "хей"), a greeting
/// ("Здравей, Ведар", "Добро утро, Ведар") or "кажи ми" — and, set off by a comma, at the end of a short phrase
/// ("Как си, Ведар?", "Благодаря, Ведар."), the way people address someone.
/// Real Bulgarian words that look similar (вятър, бодър, ведро, вътре) are rejected.
/// </summary>
public sealed partial class WakeWordMatcher : IWakeWordMatcher
{
    /// <summary>Sounds before the wake word that carry no meaning; dropped.</summary>
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "ей", "хей", "ало", "а", "е", "абе", "добре", "о", "ох", "ами", "ъъ", "ъ", "мм", "хм", "ехо",
    };

    /// <summary>Greetings, thanks and the like said with the name; kept, as they are the request ("Здравей, Ведар").</summary>
    private static readonly HashSet<string> SocialWords = new(StringComparer.Ordinal)
    {
        "здравей", "здрасти", "здравейте", "здрасте", "привет", "добро", "утро", "добър", "добра", "ден", "вечер", "благодаря", "мерси",
        "довиждане", "чао", "лека", "нощ", "как", "си", "сте", "кажи", "ми", "слушай", "чуй", "виж", "моля", "те",
    };

    /// <summary>At most this many words before the wake word at the start ("Добро утро, Ведар").</summary>
    private const int MaxLeadInWords = 4;

    /// <summary>"…, Ведар" at the end counts only in a phrase this short (fewer false activations from other speech).</summary>
    private const int MaxWordsWithTrailingWakeWord = 6;

    /// <summary>Up to this many words the comma may be missing: the fast model often drops it ("Какси Ведар").</summary>
    private const int MaxWordsWithoutComma = 3;

    /// <summary>Spellings that are also ordinary words ("ведър" = cheerful): not accepted at the end of a phrase.</summary>
    private static readonly HashSet<string> WeakAtTheEnd = new(StringComparer.Ordinal) { "ведър", "вейдър", "бедар", "фидар" };

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

    private static readonly char[] Punctuation = [' ', ',', '.', '!', '?', ':', ';', '-', '—', '–', '…'];

    public WakeWordMatch Match(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return WakeWordMatch.None;
        }

        // Walk the original text word by word so the request keeps its punctuation and casing.
        var words = WordPattern().Matches(transcript).Where(w => BulgarianText.NormalizeForMatching(w.Value).Length > 0).ToList();
        int keptFrom = -1;
        for (int i = 0; i < words.Count && i <= MaxLeadInWords; i++)
        {
            var w = words[i];
            if (IsWakeToken(w.Value))
            {
                var command = transcript[(w.Index + w.Length)..].TrimStart(Punctuation).Trim();
                var before = keptFrom < 0 ? string.Empty : transcript[words[keptFrom].Index..w.Index].Trim(Punctuation);
                return new WakeWordMatch(true, command, w.Value, before);
            }

            var norm = BulgarianText.NormalizeForMatching(w.Value);
            if (SocialWords.Contains(norm))
            {
                keptFrom = keptFrom < 0 ? i : keptFrom;
            }
            else if (!Fillers.Contains(norm))
            {
                break;
            }
        }

        // "Как си, Ведар?" / "Колко е часът, Ведар?": the name at the end of a short phrase, after a comma
        // (in two or three words also without it).
        if (words.Count is >= 2 and <= MaxWordsWithTrailingWakeWord)
        {
            var last = words[^1];
            var lastNorm = BulgarianText.NormalizeForMatching(last.Value);
            var gap = transcript[(words[^2].Index + words[^2].Length)..last.Index];
            if ((gap.Contains(',') || words.Count <= MaxWordsWithoutComma) && !WeakAtTheEnd.Contains(lastNorm) && IsStrongWakeToken(lastNorm)
                && transcript[(last.Index + last.Length)..].All(c => !char.IsLetterOrDigit(c)))
            {
                var before = transcript[..last.Index].Trim(Punctuation);
                return new WakeWordMatch(true, string.Empty, last.Value, before);
            }
        }

        return WakeWordMatch.None;
    }

    /// <summary>A known spelling of the name or its exact shape (no fuzzy matching): safer away from the start.</summary>
    private bool IsStrongWakeToken(string norm)
    {
        if (norm.Any(char.IsAsciiLetter))
        {
            return IsWakeToken(norm);
        }

        return !Rejected.Contains(norm) && (_variants.Contains(norm) || (_wakeWord == "ведар" && WakeShape().IsMatch(norm)));
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}'-]+")]
    private static partial Regex WordPattern();
}
