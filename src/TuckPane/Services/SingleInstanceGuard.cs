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
        Mutex? mutex = null;
        Mutex? legacyMutex = null;
        EventWaitHandle? activationEvent = null;
        EventWaitHandle? activationAcknowledged = null;
        bool ownsMutex = false;
        bool ownsLegacyMutex = false;
        try
        {
            mutex = new Mutex(initiallyOwned: true, $"Local\\{name}", out ownsMutex);
            if (!string.IsNullOrWhiteSpace(legacyName))
            {
                legacyMutex = new Mutex(initiallyOwned: true, $"Local\\{legacyName}", out ownsLegacyMutex);
            }
            activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{name}-activate");
            activationAcknowledged = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{name}-activate-ack");

            _mutex = mutex;
            _ownsMutex = ownsMutex;
            _legacyMutex = legacyMutex;
            _ownsLegacyMutex = ownsLegacyMutex;
            _activationEvent = activationEvent;
            _activationAcknowledged = activationAcknowledged;
            IsPrimary = _ownsMutex && (_legacyMutex is null || _ownsLegacyMutex);
        }
        catch
        {
            activationAcknowledged?.Dispose();
            activationEvent?.Dispose();
            if (ownsLegacyMutex && legacyMutex is not null)
            {
                try { legacyMutex.ReleaseMutex(); } catch { }
            }
            legacyMutex?.Dispose();
            if (ownsMutex && mutex is not null)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
            mutex?.Dispose();
            throw;
        }
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
