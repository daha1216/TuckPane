namespace TuckPane.Services;

/// <summary>
/// Process-wide reference-counted wrapper around DCompositionBoostCompositorClock.
/// Ensures Windows 11 DRR/VRR displays stay locked at 120-240Hz while ANY transition,
/// smooth scroll, hover wave, drag, resize, or Settings animation is active, and cleanly
/// releases the boost only when the last active motion completes.
/// </summary>
internal static class CompositorClockBoost
{
    private static readonly object Gate = new();
    private static int _activeCount;
    private static bool _boosted;

    internal static bool TryAcquire()
    {
        lock (Gate)
        {
            if (_activeCount == 0)
            {
                if (!NativeMethods.TrySetCompositorClockBoost(true))
                    return false;
                _boosted = true;
            }
            _activeCount++;
            return true;
        }
    }

    internal static void Release()
    {
        lock (Gate)
        {
            if (_activeCount <= 0) return;
            _activeCount--;
            if (_activeCount == 0 && _boosted)
            {
                _boosted = false;
                _ = NativeMethods.TrySetCompositorClockBoost(false);
            }
        }
    }

    internal static IDisposable Acquire() => TryAcquire() ? new Lease() : NoOpLease.Instance;

    private sealed class Lease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Release();
        }
    }

    private sealed class NoOpLease : IDisposable
    {
        internal static readonly NoOpLease Instance = new();
        public void Dispose() { }
    }
}
