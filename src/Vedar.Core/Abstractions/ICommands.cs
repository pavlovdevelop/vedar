namespace Vedar.Core.Abstractions;

public enum CommandOutcome
{
    /// <summary>Executed; speak <see cref="CommandResult.SpokenText"/>.</summary>
    Handled,
    /// <summary>Not a local command.</summary>
    NotRecognized,
    /// <summary>Sensitive action: ask the user to confirm with "да"/"не".</summary>
    NeedsConfirmation,
    /// <summary>The command failed; speak the error text.</summary>
    Failed,
}

/// <summary>Assistant-level actions a command may request from the orchestrator.</summary>
public enum AssistantDirective
{
    None,
    Stop,
    Repeat,
    StartConversation,
    EndConversation,
    MuteMicrophone,
    /// <summary>Speak the text, then listen again for an immediate follow-up.</summary>
    ListenAgain,
    /// <summary>Start the spoken setup ("Ведар, настройка").</summary>
    StartVoiceSetup,
    /// <summary>Take an API key from the clipboard, store it protected and check it ("Ведар, постави ключа").</summary>
    PasteApiKey,
}

public sealed record CommandResult(
    CommandOutcome Outcome,
    string? SpokenText = null,
    AssistantDirective Directive = AssistantDirective.None,
    string? CommandId = null,
    Func<CancellationToken, Task<CommandResult>>? ConfirmAction = null)
{
    public static CommandResult NotRecognized { get; } = new(CommandOutcome.NotRecognized);

    public static CommandResult Done(string commandId, string? text, AssistantDirective directive = AssistantDirective.None) =>
        new(CommandOutcome.Handled, text, directive, commandId);

    public static CommandResult Fail(string commandId, string text) => new(CommandOutcome.Failed, text, CommandId: commandId);

    public static CommandResult Confirm(string commandId, string question, Func<CancellationToken, Task<CommandResult>> action) =>
        new(CommandOutcome.NeedsConfirmation, question, CommandId: commandId, ConfirmAction: action);
}

/// <summary>Parses Bulgarian text into a local command and executes it from an allow-list.</summary>
public interface ICommandDispatcher
{
    /// <summary>Returns <see cref="CommandResult.NotRecognized"/> if the text is not a known command.</summary>
    Task<CommandResult> DispatchAsync(string text, CancellationToken cancellationToken);

    /// <summary>Human readable list of the available commands (for "помощ").</summary>
    IReadOnlyList<string> DescribeCommands();
}

/// <summary>Recognizes yes/no answers to confirmation questions.</summary>
public enum ConfirmationAnswer { Unknown, Yes, No }
