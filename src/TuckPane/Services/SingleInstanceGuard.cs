namespace TuckPane.Services;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly object _sync = new();
    private readonly Mutex _mutex;
    private readonly Mutex? _legacyMutex;
    private readonly bool _ownsMutex;
    private readonly bool _ownsLegacyMutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly EventWaitHandle _activationAcknowledged;
    private RegisteredWaitHandle? _wait;
    private bool _disposed;

    public SingleInstanceGuard(string name, string? legacyName = null)
    {
        _mutex = new Mutex(initiallyOwned: true, $"Local\\{name}", out bool createdNew);
        _ownsMutex = createdNew;
        if (!string.IsNullOrWhiteSpace(legacyName))
        {
            _legacyMutex = new Mutex(initiallyOwned: true, $"Local\\{legacyName}", out bool legacyCreatedNew);
            _ownsLegacyMutex = legacyCreatedNew;
        }
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{name}-activate");
        _activationAcknowledged = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{name}-activate-ack");
        IsPrimary = _ownsMutex && (_legacyMutex is null || _ownsLegacyMutex);
    }

    public bool IsPrimary { get; }

    public bool SignalPrimary(TimeSpan? timeout = null)
    {
        _activationEvent.Set();
        return _activationAcknowledged.WaitOne(timeout ?? TimeSpan.FromSeconds(4));
    }

    public void Listen(Action activated)
    {
        lock (_sync)
        {
            if (_disposed || !IsPrimary || _wait is not null) return;
            _wait = ThreadPool.RegisterWaitForSingleObject(_activationEvent, (_, _) =>
            {
                try { activated(); }
                finally
                {
                    lock (_sync)
                    {
                        if (!_disposed) _activationAcknowledged.Set();
                    }
                }
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    public static void ShowLegacyInstanceMessage() => NativeMethods.MessageBox(
        IntPtr.Zero,
        AppStrings.Get("LegacyInstanceMessage"),
        "TuckPane",
        0x00000030);

    public void Dispose()
    {
        RegisteredWaitHandle? wait;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            wait = _wait;
            _wait = null;
        }
        wait?.Unregister(null);
        _activationAcknowledged.Dispose();
        _activationEvent.Dispose();
        if (_ownsLegacyMutex) _legacyMutex!.ReleaseMutex();
        _legacyMutex?.Dispose();
        if (_ownsMutex) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
