namespace Vedar.Desktop.Hosting;

/// <summary>
/// Ensures one VEDAR per Windows user. Starting VEDAR again (e.g. with the Ctrl+Alt+V shortcut) shows the
/// running instance instead of starting a second assistant that would compete for the microphone.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showSignal;
    private readonly EventWaitHandle _exitSignal;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, EventWaitHandle showSignal, EventWaitHandle exitSignal, bool isFirst)
    {
        _mutex = mutex;
        _showSignal = showSignal;
        _exitSignal = exitSignal;
        IsFirstInstance = isFirst;
    }

    public bool IsFirstInstance { get; }

    public static SingleInstance Acquire(string? suffix = null)
    {
        var id = "VEDAR-" + Environment.UserName + (suffix is null ? string.Empty : "-" + suffix);
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + id, out var createdNew);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + id + "-show");
        var exit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + id + "-exit");
        return new SingleInstance(mutex, signal, exit, createdNew);
    }

    public void SignalFirstInstance() => _showSignal.Set();

    /// <summary>Asks the running instance to exit (used by the installer before an upgrade: VEDAR.exe --exit).</summary>
    public void SignalExit() => _exitSignal.Set();

    public void ListenForShowRequests(Action onShow, Action? onExit = null)
    {
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _showSignal, _exitSignal, _cts.Token.WaitHandle };
            while (true)
            {
                var index = WaitHandle.WaitAny(handles);
                if (index == 0)
                {
                    onShow();
                }
                else if (index == 1)
                {
                    onExit?.Invoke();
                }
                else
                {
                    return;
                }
            }
        })
        { IsBackground = true, Name = "VEDAR single instance" };
        thread.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
        _showSignal.Dispose();
        _exitSignal.Dispose();
        _cts.Dispose();
    }
}
