namespace Vedar.Core.State;

/// <summary>
/// Thread-safe state holder that only allows well-defined transitions.
/// Any state may go to <see cref="AssistantState.Error"/>, <see cref="AssistantState.Stopped"/>
/// or <see cref="AssistantState.Idle"/> (cancel / recover), so the assistant can never get stuck.
/// </summary>
public sealed class AssistantStateMachine
{
    private static readonly Dictionary<AssistantState, AssistantState[]> Allowed = new()
    {
        [AssistantState.Starting] = [AssistantState.Idle, AssistantState.Speaking],
        [AssistantState.Idle] = [AssistantState.Listening, AssistantState.Recognizing, AssistantState.Processing, AssistantState.Speaking],
        [AssistantState.Listening] = [AssistantState.Recognizing, AssistantState.Speaking, AssistantState.Processing],
        [AssistantState.Recognizing] = [AssistantState.Processing, AssistantState.Speaking, AssistantState.Listening],
        [AssistantState.Processing] = [AssistantState.Speaking, AssistantState.Listening],
        [AssistantState.Speaking] = [AssistantState.Listening, AssistantState.Processing],
        [AssistantState.Error] = [AssistantState.Speaking, AssistantState.Starting],
        [AssistantState.Stopped] = [AssistantState.Starting],
    };

    private readonly object _gate = new();
    private AssistantState _state = AssistantState.Starting;

    public event EventHandler<AssistantStateChangedEventArgs>? StateChanged;

    public AssistantState Current
    {
        get { lock (_gate) { return _state; } }
    }

    public static bool IsAllowed(AssistantState from, AssistantState to)
    {
        if (from == to)
        {
            return true;
        }

        if (to is AssistantState.Error or AssistantState.Stopped or AssistantState.Idle)
        {
            return true;
        }

        return Allowed.TryGetValue(from, out var targets) && targets.Contains(to);
    }

    /// <summary>Attempts a transition. Returns false (and leaves the state unchanged) if it is not allowed.</summary>
    public bool TryTransition(AssistantState to, string? detail = null)
    {
        AssistantState previous;
        lock (_gate)
        {
            previous = _state;
            if (previous == to)
            {
                return true;
            }

            if (!IsAllowed(previous, to))
            {
                return false;
            }

            _state = to;
        }

        StateChanged?.Invoke(this, new AssistantStateChangedEventArgs(previous, to, detail));
        return true;
    }

    /// <summary>Transition that throws when the transition is invalid. Used where an invalid transition is a bug.</summary>
    public void Transition(AssistantState to, string? detail = null)
    {
        if (!TryTransition(to, detail))
        {
            throw new InvalidOperationException($"Invalid assistant state transition {Current} -> {to}.");
        }
    }
}
