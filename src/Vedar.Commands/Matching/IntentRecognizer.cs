using System.Text.RegularExpressions;
using Vedar.Core.Text;

namespace Vedar.Commands.Matching;

public sealed record IntentMatch(string IntentId, double Score, string? Slot, int? Number, int Extras);

internal enum SlotKind { None, Rest, Number }

internal sealed record PatternToken(string[] Alternatives, bool Optional, SlotKind Slot);

internal sealed class IntentPattern
{
    public required string IntentId { get; init; }
    public required PatternToken[] Tokens { get; init; }
    public int MaxExtras { get; init; } = 2;
    public Func<string, bool>? ValidateSlot { get; init; }

    /// <summary>Pattern syntax: words separated by spaces; "a|b" alternatives; trailing "?" optional; "{rest}" and "{n}" slots.</summary>
    public static IntentPattern Parse(string intentId, string pattern, int maxExtras = 2, Func<string, bool>? validateSlot = null) => new()
    {
        IntentId = intentId,
        MaxExtras = maxExtras,
        ValidateSlot = validateSlot,
        Tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t =>
        {
            var optional = t.EndsWith('?');
            var body = optional ? t[..^1] : t;
            return body switch
            {
                "{rest}" => new PatternToken([], optional, SlotKind.Rest),
                "{n}" => new PatternToken([], optional, SlotKind.Number),
                _ => new PatternToken(body.Split('|'), optional, SlotKind.None),
            };
        }).ToArray(),
    };
}

/// <summary>
/// Understands short Bulgarian commands in natural word order, tolerant to recognition errors.
/// Template words must appear in order; filler words ("моля", "ми", "можеш ли") are ignored;
/// too many unrelated words mean the sentence is a question for the AI, not a command.
/// </summary>
public sealed partial class IntentRecognizer
{
    internal static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "моля", "ми", "ти", "ли", "би", "можеш", "може", "можете", "искам", "искаш", "да", "сега", "веднага", "хайде", "ей", "хей", "малко",
        "ако", "обичаш", "обичате", "ще", "просто", "още", "мога", "нали", "бе", "ве", "ъъ", "ъ", "е", "аз", "ни", "те", "го", "тук", "така",
        "бързо", "отново", "пак", "днес", "сега", "вече", "ведар", "ведаре", "здравей", "добре", "тогава", "значи", "един", "една", "ама", "абе", "сам",
    };

    private readonly List<IntentPattern> _patterns = [];
    private readonly Func<string, bool> _isWakeToken;

    public IntentRecognizer(Func<string, bool>? isWakeToken = null)
    {
        _isWakeToken = isWakeToken ?? (t => t is "ведар" or "ведаре");
    }

    internal IReadOnlyList<IntentPattern> Patterns => _patterns;

    public IntentRecognizer Add(string intentId, string pattern, int maxExtras = 2, Func<string, bool>? validateSlot = null)
    {
        _patterns.Add(IntentPattern.Parse(intentId, pattern, maxExtras, validateSlot));
        return this;
    }

    [GeneratedRegex(@"\b(?:чат\s*(?:джи\s*пи\s*ти|gpt|джипити|гпт)|chat\s*gpt|чатджипити|чатгпт)\b")]
    private static partial Regex ChatGptPhrase();

    [GeneratedRegex(@"\b(?:бг\s*(?:джи\s*пи\s*ти|gpt|джипити)|bg\s*gpt|би\s*джи\s*джи\s*пи\s*ти)\b")]
    private static partial Regex BgGptPhrase();

    /// <summary>Normalizes and tokenizes; joins multi-word brand names that Whisper splits ("чат джи пи ти" → "chatgpt").</summary>
    public string[] Tokenize(string text)
    {
        var normalized = BulgarianText.NormalizeForMatching(text);
        normalized = ChatGptPhrase().Replace(normalized, "chatgpt");
        normalized = BgGptPhrase().Replace(normalized, "bggpt");
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count > 1 && _isWakeToken(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        return tokens.ToArray();
    }

    public IntentMatch? Recognize(string text)
    {
        var tokens = Tokenize(text);
        if (tokens.Length == 0)
        {
            return null;
        }

        IntentMatch? best = null;
        foreach (var pattern in _patterns)
        {
            var m = Match(tokens, pattern);
            if (m is not null && (best is null || m.Score > best.Score + 1e-9))
            {
                best = m;
            }
        }

        return best;
    }

    internal static IntentMatch? Match(string[] u, IntentPattern pattern)
    {
        int pos = 0, matched = 0, extras = 0;
        double quality = 0;
        string? slot = null;
        int? number = null;

        foreach (var t in pattern.Tokens)
        {
            switch (t.Slot)
            {
                case SlotKind.Rest:
                {
                    var rest = u.Skip(pos).Where(w => !Fillers.Contains(w)).ToArray();
                    if (rest.Length == 0)
                    {
                        if (!t.Optional)
                        {
                            return null;
                        }

                        continue;
                    }

                    slot = string.Join(' ', rest);
                    if (pattern.ValidateSlot is not null && !pattern.ValidateSlot(slot))
                    {
                        return null;
                    }

                    pos = u.Length;
                    matched++;
                    quality += 0.5;
                    continue;
                }

                case SlotKind.Number:
                {
                    int found = -1;
                    for (int j = pos; j < u.Length; j++)
                    {
                        if (BulgarianNumberParser.Parse(u, j) is { } parsed)
                        {
                            found = j;
                            number = parsed.Value;
                            extras += u.Skip(pos).Take(j - pos).Count(w => !Fillers.Contains(w));
                            pos = j + parsed.Length;
                            break;
                        }
                    }

                    if (found < 0)
                    {
                        if (!t.Optional)
                        {
                            return null;
                        }

                        continue;
                    }

                    matched++;
                    quality += 1;
                    continue;
                }
            }

            int hit = -1, width = 1;
            double hitQuality = 0;
            for (int j = pos; j < u.Length && hit < 0; j++)
            {
                foreach (var alt in t.Alternatives)
                {
                    if (FuzzyMatcher.TokenMatches(u[j], alt))
                    {
                        hit = j;
                        hitQuality = u[j] == alt ? 1.0 : FuzzyMatcher.Similarity(u[j], alt);
                        break;
                    }

                    // Whisper sometimes splits one word in two ("на стръките" for "настройките").
                    if (j + 1 < u.Length && alt.Length >= 6 && FuzzyMatcher.TokenMatches(u[j] + u[j + 1], alt))
                    {
                        hit = j;
                        width = 2;
                        hitQuality = FuzzyMatcher.Similarity(u[j] + u[j + 1], alt) - 0.1;
                        break;
                    }
                }
            }

            if (hit < 0)
            {
                if (t.Optional)
                {
                    continue;
                }

                return null;
            }

            extras += u.Skip(pos).Take(hit - pos).Count(w => !Fillers.Contains(w));
            pos = hit + width;
            matched++;
            quality += hitQuality;
        }

        extras += u.Skip(pos).Count(w => !Fillers.Contains(w));
        if (extras > pattern.MaxExtras || matched == 0)
        {
            return null;
        }

        var score = quality - 0.6 * extras;
        return new IntentMatch(pattern.IntentId, score, slot, number, extras);
    }
}
