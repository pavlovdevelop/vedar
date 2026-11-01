using System.Runtime.CompilerServices;
using System.Text;

namespace Vedar.AI.OpenAi;

/// <summary>
/// Parser for Server-Sent Events (text/event-stream) as used by OpenAI-compatible streaming APIs.
/// Yields the data payload of each event. Handles CRLF/LF, comments, multi-line data and partial reads.
/// </summary>
public static class SseParser
{
    public static async IAsyncEnumerable<string> ReadEventsAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var data = new StringBuilder();
        bool hasData = false;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (hasData)
                {
                    yield return data.ToString();
                    data.Clear();
                    hasData = false;
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue; // comment / keep-alive
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (field == "data")
            {
                if (hasData)
                {
                    data.Append('\n');
                }

                data.Append(value);
                hasData = true;
            }
        }

        if (hasData)
        {
            yield return data.ToString();
        }
    }
}
