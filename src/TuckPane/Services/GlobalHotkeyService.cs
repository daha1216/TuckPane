using TuckPane.Core;

namespace TuckPane.Services;

/// <summary>
/// Manages Win32 global hotkeys bound to the hidden console host window.
/// Supports conflict-safe registration, atomic rollback, and clean disposal.
/// </summary>
internal sealed class GlobalHotkeyService : IDisposable
{
    private static readonly UIntPtr SubclassId = new(0x4746484BUL);
    private readonly IntPtr _window;
    private readonly Action<int> _onTriggered;
    private readonly NativeMethods.SubclassProc _subclass;
    private readonly Dictionary<int, HotkeyBinding> _registered = [];
    private bool _disposed;

    internal GlobalHotkeyService(IntPtr window, Action<int> onTriggered)
    {
        _window = window;
        _onTriggered = onTriggered;
        _subclass = WindowProc;
        if (_window != IntPtr.Zero)
            _ = NativeMethods.SetWindowSubclass(_window, _subclass, SubclassId, IntPtr.Zero);
    }

    internal bool TrySetHotkey(int id, HotkeyBinding? binding)
    {
        if (_disposed || _window == IntPtr.Zero) return binding is null;

        bool hadPrevious = _registered.TryGetValue(id, out HotkeyBinding previous);
        if (hadPrevious && binding is HotkeyBinding candidate && previous.SameKeyCombo(candidate))
            return true;

        if (hadPrevious)
        {
            _ = NativeMethods.UnregisterHotKey(_window, id);
            _registered.Remove(id);
        }

        if (binding is not HotkeyBinding desired)
            return true;

        if (NativeMethods.RegisterHotKey(_window, id, desired.Modifiers, desired.VirtualKey))
        {
            _registered[id] = desired;
            return true;
        }

        // Roll back to previous binding if the new combo is owned by another application.
        if (hadPrevious && NativeMethods.RegisterHotKey(_window, id, previous.Modifiers, previous.VirtualKey))
            _registered[id] = previous;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_window != IntPtr.Zero)
        {
            foreach (int id in _registered.Keys.ToArray())
                _ = NativeMethods.UnregisterHotKey(_window, id);
            _ = NativeMethods.RemoveWindowSubclass(_window, _subclass, SubclassId);
        }
        _registered.Clear();
    }

    private IntPtr WindowProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        IntPtr referenceData)
    {
        if (message == NativeMethods.WM_HOTKEY && !_disposed)
        {
            int id = unchecked((int)wParam.ToUInt64());
            if (_registered.ContainsKey(id))
            {
                _onTriggered(id);
                return IntPtr.Zero;
            }
        }
        return NativeMethods.DefSubclassProc(hWnd, message, wParam, lParam);
    }
}
