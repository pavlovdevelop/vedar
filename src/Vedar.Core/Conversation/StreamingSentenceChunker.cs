using System.Text;
using Vedar.Core.Text;

namespace Vedar.Core.Conversation;

/// <summary>
/// Collects streamed AI tokens and releases complete sentences as soon as they are known,
/// so speech can start before the whole answer has arrived.
/// </summary>
public sealed class StreamingSentenceChunker
{
    private readonly StringBuilder _buffer = new();
    private readonly int _maxLength;

    public StreamingSentenceChunker(int maxLength = 220) => _maxLength = maxLength;

    /// <summary>Adds a token and returns any sentences that are now complete.</summary>
    public IReadOnlyList<string> Append(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return [];
        }

        _buffer.Append(token);
        var ready = new List<string>();
        while (TryTakeSentence(out var sentence))
        {
            ready.Add(sentence);
        }

        return ready;
    }

    /// <summary>Returns whatever text remains at the end of the stream.</summary>
    public string? Flush()
    {
        var rest = _buffer.ToString().Trim();
        _buffer.Clear();
        return rest.Any(char.IsLetterOrDigit) ? rest : null;
    }

    private bool TryTakeSentence(out string sentence)
    {
        var text = _buffer.ToString();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '\n')
            {
                var candidate = text[..i].Trim();
                _buffer.Remove(0, i + 1);
                if (candidate.Any(char.IsLetterOrDigit))
                {
                    sentence = candidate;
                    return true;
                }

                return TryTakeSentence(out sentence);
            }

            // We need to see the character after the punctuation to decide; wait for more input otherwise.
            if (text[i] is '.' or '!' or '?' or '…' or ';' && i + 1 < text.Length)
            {
                int next = i + 1;
                while (next < text.Length && text[next] is '"' or '”' or '»' or ')')
                {
                    next++;
                }

                if (next >= text.Length)
                {
                    break;
                }

                if (char.IsWhiteSpace(text[next]))
                {
                    // Need a following non-space char for abbreviation/decimal checks.
                    int k = next;
                    while (k < text.Length && char.IsWhiteSpace(text[k]))
                    {
                        k++;
                    }

                    if (k >= text.Length)
                    {
                        break;
                    }

                    if (SentenceSplitter.IsSentenceEnd(text, i))
                    {
                        sentence = text[..next].Trim();
                        _buffer.Remove(0, next);
                        return true;
                    }
                }
            }
        }

        if (_buffer.Length > _maxLength)
        {
            var current = _buffer.ToString();
            var piece = SentenceSplitter.BreakLong(current, _maxLength).First();
            _buffer.Remove(0, Math.Min(_buffer.Length, current.IndexOf(piece, StringComparison.Ordinal) + piece.Length));
            sentence = piece;
            return true;
        }

        sentence = string.Empty;
        return false;
    }
}
