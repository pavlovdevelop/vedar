namespace Vedar.Desktop.Hosting;

/// <summary>
/// Ensures one VEDAR per Windows user. Starting VEDAR again (e.g. with the Ctrl+Alt+V shortcut) shows the
/// running instance instead of starting a second assistant that would compete for the microphone.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showSignal;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, EventWaitHandle showSignal, bool isFirst)
    {
        _mutex = mutex;
        _showSignal = showSignal;
        IsFirstInstance = isFirst;
    }

    public bool IsFirstInstance { get; }

    public static SingleInstance Acquire(string? suffix = null)
    {
        var id = "VEDAR-" + Environment.UserName + (suffix is null ? string.Empty : "-" + suffix);
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + id, out var createdNew);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + id + "-show");
        return new SingleInstance(mutex, signal, createdNew);
    }

    public void SignalFirstInstance() => _showSignal.Set();

    public void ListenForShowRequests(Action onShow)
    {
        var thread = new Thread(() =>
        {
            var handles = new[] { _showSignal, _cts.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                onShow();
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
        _cts.Dispose();
    }
}
