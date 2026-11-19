namespace TuckPane.Core;

/// <summary>
/// Deterministic LIFO teardown registry for window timers, hooks, native handles,
/// and rendering subscriptions. Ensures every registered cleanup runs even if an
/// earlier cleanup throws, eliminating leaked subscriptions on window close.
/// </summary>
internal sealed class WindowResourceScope : IDisposable
{
    private readonly List<Action> _cleanups = [];
    private bool _disposed;

    internal int Count => _cleanups.Count;
    internal bool IsDisposed => _disposed;

    internal void Track(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        if (_disposed)
        {
            cleanup();
            return;
        }
        _cleanups.Add(cleanup);
    }

    internal T Track<T>(T disposable) where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(disposable);
        Track(disposable.Dispose);
        return disposable;
    }

    internal void DisposeAll(Action<Exception>? onError = null)
    {
        if (_disposed) return;
        _disposed = true;
        for (int index = _cleanups.Count - 1; index >= 0; index--)
        {
            try
            {
                _cleanups[index]();
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        }
        _cleanups.Clear();
    }

    public void Dispose() => DisposeAll();
}
