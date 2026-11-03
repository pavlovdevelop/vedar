using Vedar.Core.Abstractions;

namespace Vedar.Core.Conversation;

/// <summary>
/// The current conversation. Kept in memory only (unless history saving is enabled elsewhere).
/// Context is bounded to <see cref="MaxMessages"/> messages to keep requests small.
/// </summary>
public sealed class ConversationSession
{
    private readonly List<ChatMessage> _messages = [];
    private readonly object _gate = new();

    public ConversationSession(int maxMessages = 20)
    {
        MaxMessages = Math.Max(2, maxMessages);
        Id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
    }

    public string Id { get; private set; }
    public int MaxMessages { get; set; }
    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.Now;

    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (_gate) { return _messages.ToArray(); } }
    }

    public int Count
    {
        get { lock (_gate) { return _messages.Count; } }
    }

    public void AddUser(string text) => Add(ChatMessage.User(text));

    public void AddAssistant(string text) => Add(ChatMessage.Assistant(text));

    private void Add(ChatMessage message)
    {
        lock (_gate)
        {
            _messages.Add(message);
            LastActivity = DateTimeOffset.Now;
        }
    }

    /// <summary>Removes the last user message (used when the AI request failed, so a retry is clean).</summary>
    public void RemoveLastUserMessage()
    {
        lock (_gate)
        {
            var idx = _messages.FindLastIndex(m => m.Role == ChatRole.User);
            if (idx >= 0 && idx == _messages.Count - 1)
            {
                _messages.RemoveAt(idx);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
            Id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        }
    }

    /// <summary>
    /// System prompt + the most recent messages, always in a shape every chat model accepts: user and assistant take
    /// turns, starting with the user, and no message is empty. BgGPT (Gemma 3) rejects anything else as a
    /// bad request, which VEDAR used to report as "a reply it could not understand". Two messages of the same side in
    /// a row are joined.
    /// </summary>
    public IReadOnlyList<ChatMessage> BuildRequest(string systemPrompt)
    {
        lock (_gate)
        {
            var turns = new List<ChatMessage>();
            foreach (var message in _messages.Where(m => m.Role != ChatRole.System && !string.IsNullOrWhiteSpace(m.Content)))
            {
                if (turns.Count > 0 && turns[^1].Role == message.Role)
                {
                    turns[^1] = turns[^1] with { Content = turns[^1].Content.TrimEnd() + "\n" + message.Content.Trim() };
                }
                else
                {
                    turns.Add(message);
                }
            }

            var recent = turns.Skip(Math.Max(0, turns.Count - MaxMessages)).ToList();
            while (recent.Count > 0 && recent[0].Role != ChatRole.User)
            {
                recent.RemoveAt(0);
            }

            var result = new List<ChatMessage>(recent.Count + 1) { ChatMessage.System(systemPrompt) };
            result.AddRange(recent);
            return result;
        }
    }
}
