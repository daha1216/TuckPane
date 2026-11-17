using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace TuckPane.Services;

// HWND disabling also suspends keyboard input inside WebView2 while documents are flushed.
internal sealed class UpdateWindowInput : IDisposable
{
    private readonly List<(IntPtr Handle, bool Enabled)> _windows = [];

    public UpdateWindowInput(IEnumerable<Window> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        try
        {
            foreach (Window window in windows)
            {
                IntPtr handle = WindowNative.GetWindowHandle(window);
                if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle)) continue;
                bool wasEnabled = NativeMethods.IsWindowEnabled(handle);
                _windows.Add((handle, wasEnabled));
                _ = NativeMethods.EnableWindow(handle, false);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var window in _windows)
        {
            if (window.Handle != IntPtr.Zero && NativeMethods.IsWindow(window.Handle))
                _ = NativeMethods.EnableWindow(window.Handle, window.Enabled);
        }
        _windows.Clear();
    }
}
