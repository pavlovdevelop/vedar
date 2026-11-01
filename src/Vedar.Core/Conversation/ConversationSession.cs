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

    /// <summary>System prompt + the most recent messages. The first kept message is always a user message.</summary>
    public IReadOnlyList<ChatMessage> BuildRequest(string systemPrompt)
    {
        lock (_gate)
        {
            var recent = _messages.Skip(Math.Max(0, _messages.Count - MaxMessages)).ToList();
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
