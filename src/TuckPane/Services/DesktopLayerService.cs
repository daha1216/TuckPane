namespace TuckPane.Services;

internal enum DesktopAttachmentState
{
    Ok,
    Hidden,
    BehindDesktop,
    OwnerLost
}

public sealed class DesktopLayerService : IDisposable
{
    private const uint SpawnWorkerMessage = 0x052C;
    private static readonly UIntPtr ActivationSubclassId = new(0x47464C59UL);
    private static readonly object CachedViewGate = new();
    private static IntPtr _lastKnownDesktopIconView;
    private static uint _lastKnownDesktopProcessId;

    private readonly IntPtr _window;
    private readonly IntPtr _expandedOwner;
    private readonly NativeMethods.SubclassProc _activationGuard;
    private IntPtr _desktopIconView;
    private uint _desktopIconViewPid;
    private bool _allowActivation;
    private bool _expanded;
    private bool _stayTopmost;

    public DesktopLayerService(IntPtr window, IntPtr expandedOwner)
    {
        _window = window;
        _expandedOwner = expandedOwner;
        _activationGuard = ActivationGuard;
        ApplyToolWindowStyle();
        _ = NativeMethods.SetWindowSubclass(_window, _activationGuard, ActivationSubclassId, IntPtr.Zero);
        ApplyTransparentChrome();
        Reattach(showWindow: false);
    }

    public void Reattach(bool showWindow = false)
    {
        if (_expanded)
        {
            if (NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT) != _expandedOwner)
            {
                _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, _expandedOwner);
            }

            _ = NativeMethods.SetWindowPos(
                _window,
                _stayTopmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_TOP,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                (showWindow ? NativeMethods.SWP_SHOWWINDOW : 0) | NativeMethods.SWP_NOOWNERZORDER);
            return;
        }

        if (!IsValidDesktopIconView(_desktopIconView, _desktopIconViewPid))
        {
            SetDesktopIconView(FindDesktopIconView());
        }

        bool ownerChanged = _desktopIconView != IntPtr.Zero &&
            NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT) != _desktopIconView;
        if (ownerChanged)
        {
            _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, _desktopIconView);
        }

        uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER;
        if (!TryGetDesktopBandAnchor(out IntPtr anchor)) flags |= NativeMethods.SWP_NOZORDER;
        if (showWindow) flags |= NativeMethods.SWP_SHOWWINDOW;
        _ = NativeMethods.SetWindowPos(
            _window,
            anchor,
            0,
            0,
            0,
            0,
            flags);
    }

    public void SetExpanded(bool expanded, bool stayTopmost = false, bool showWindow = false)
    {
        if (!expanded && _stayTopmost)
        {
            _ = NativeMethods.SetWindowPos(
                _window,
                NativeMethods.HWND_NOTOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER);
        }

        _expanded = expanded;
        _stayTopmost = expanded && stayTopmost;
        Reattach(showWindow);
        if (expanded && !stayTopmost) RaiseAboveNormalWindows();
    }

    public void BringAboveDesktopPeers()
    {
        Reattach(showWindow: true);
        _ = NativeMethods.SetWindowPos(
            _window,
            NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    internal bool IsInputActivationEnabled => _allowActivation;

    internal string DescribeAttachment()
    {
        long owner = NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT).ToInt64();
        long view = _desktopIconView.ToInt64();
        return $"hwnd=0x{_window.ToInt64():X} view=0x{view:X} owner=0x{owner:X} expanded={_expanded}";
    }

    // Returns the state observed *before* repairing so the caller can log what was wrong, and
    // reports what is observed afterwards through <paramref name="remaining"/>. A repair can be
    // silently undone (the shell takes the owner back, the host is raised again, or the window ends
    // up in the same broken state for a different reason), so probing afterwards is the only way to
    // tell a real fix from a no-op. Reporting the pre-repair state alone made a failed repair look
    // identical to a successful one.
    internal DesktopAttachmentState RepairDesktopAttachment(out DesktopAttachmentState remaining)
    {
        DesktopAttachmentState state = ProbeDesktopAttachment();
        remaining = state;
        if (state == DesktopAttachmentState.Ok) return state;

        if (_expanded)
        {
            bool hidden = state == DesktopAttachmentState.Hidden;
            if (hidden)
            {
                _ = NativeMethods.SetWindowPos(
                    _window,
                    IntPtr.Zero,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_HIDEWINDOW);
            }
            RepairExpandedZOrder(showWindow: hidden);
            remaining = ProbeDesktopAttachment();
            return state;
        }

        IntPtr iconView = _desktopIconView;
        if (state == DesktopAttachmentState.OwnerLost &&
            IsValidDesktopIconView(iconView, _desktopIconViewPid))
        {
            _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, iconView);
        }

        ReassertDesktopAttachment();
        if (state == DesktopAttachmentState.Hidden)
        {
            // A window hidden by the shell misses WM_SHOWWINDOW, so WinUI keeps its last frame.
            // Force a hide/show round trip so the frame is rebuilt before it is shown again.
            _ = NativeMethods.SetWindowPos(
                _window,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_HIDEWINDOW);
            ReassertDesktopAttachment();
        }

        remaining = ProbeDesktopAttachment();
        return state;
    }

    internal void RepairExpandedZOrder(bool showWindow = false)
    {
        if (_window == IntPtr.Zero || !NativeMethods.IsWindow(_window)) return;
        if (_stayTopmost)
        {
            _ = NativeMethods.SetWindowPos(
                _window,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOOWNERZORDER);
            return;
        }
        // Re-insert directly above the desktop host rather than jumping over normal user windows.
        uint flags = NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER;
        if (showWindow) flags |= NativeMethods.SWP_SHOWWINDOW;
        if (!TryGetDesktopInsertAnchor(out IntPtr anchor))
        {
            if (!showWindow) return;
            flags |= NativeMethods.SWP_NOZORDER;
        }
        _ = NativeMethods.SetWindowPos(_window, anchor, 0, 0, 0, 0, flags);
    }

    private void ReassertDesktopAttachment()
    {
        uint flags = NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_SHOWWINDOW;
        if (!TryGetDesktopInsertAnchor(out IntPtr anchor)) flags |= NativeMethods.SWP_NOZORDER;
        _ = NativeMethods.SetWindowPos(_window, anchor, 0, 0, 0, 0, flags);
    }

    // Walks the contiguous peer band owned by _desktopIconView immediately above the desktop host.
    // Returns false when _window is already seated inside that band (avoiding z-order churn between
    // multiple desktop organizers on periodic repair ticks), or returns the safe insert-after anchor
    // immediately above the peer band when _window has drifted above normal windows or behind Progman.
    private bool TryGetDesktopBandAnchor(out IntPtr anchor)
    {
        anchor = IntPtr.Zero;
        IntPtr root = DesktopHostRoot();
        if (root == IntPtr.Zero || _desktopIconView == IntPtr.Zero) return false;

        IntPtr cursor = root;
        for (int step = 0; step < 256; step++)
        {
            IntPtr above = NativeMethods.GetWindow(cursor, NativeMethods.GW_HWNDPREV);
            if (above == _window) return false;
            if (above == IntPtr.Zero)
            {
                anchor = NativeMethods.HWND_TOP;
                return true;
            }
            if (NativeMethods.IsWindow(above) &&
                NativeMethods.GetWindowLongPtr(above, NativeMethods.GWLP_HWNDPARENT) == _desktopIconView)
            {
                cursor = above;
                continue;
            }
            if (!NativeMethods.IsWindow(above) || above == NativeMethods.HWND_BOTTOM) return false;
            anchor = above;
            return true;
        }
        return TryGetDesktopInsertAnchor(out anchor);
    }

    // HWND_TOP is IntPtr.Zero, so "the host is unknown" and "the host is the top-most window" are
    // the same value. Both callers have to tell those apart: an unknown host must leave the z-order
    // alone (SWP_NOZORDER), while a host that really is top-most must be anchored to the top. This
    // returns true only when the anchor can be handed to SetWindowPos as-is; callers add
    // SWP_NOZORDER when it returns false and the anchor value is then ignored by the API.
    //
    // The shell raises the desktop host above the window's bottom-most slot while the desktop is
    // shown (Win+D, clicking the desktop), and a plain HWND_BOTTOM then buries the window behind
    // the wallpaper for good. Placing the window directly above the host keeps it on the desktop
    // layer while every ordinary window still stays in front of it.
    private bool TryGetDesktopInsertAnchor(out IntPtr anchor)
    {
        anchor = IntPtr.Zero;
        IntPtr root = DesktopHostRoot();
        // Falling back to HWND_BOTTOM would restore the very bug this method exists to fix: once
        // the shell raises the desktop host, a bottom-most window stays buried behind the
        // wallpaper.
        if (root == IntPtr.Zero) return false;

        // hWndInsertAfter places the window right behind the given window, so the window just in
        // front of the host is the right anchor for "immediately above the host".
        IntPtr above = NativeMethods.GetWindow(root, NativeMethods.GW_HWNDPREV);
        if (above != IntPtr.Zero && !NativeMethods.IsWindow(above)) return false;
        anchor = above == IntPtr.Zero ? NativeMethods.HWND_TOP : above;
        // HWND_BOTTOM is 1, not 0, so it would sail past a plain "anchor == IntPtr.Zero" check.
        // Burying the window is the exact state this service exists to prevent.
        return anchor != NativeMethods.HWND_BOTTOM && anchor != _window;
    }

    private IntPtr DesktopHostRoot()
    {
        IntPtr iconView = _desktopIconView;
        if (!IsValidDesktopIconView(iconView, _desktopIconViewPid)) return IntPtr.Zero;
        return NativeMethods.GetAncestor(iconView, NativeMethods.GA_ROOT);
    }

    private DesktopAttachmentState ProbeDesktopAttachment()
    {
        if ((_expanded && _stayTopmost) || _window == IntPtr.Zero || !NativeMethods.IsWindow(_window))
        {
            return DesktopAttachmentState.Ok;
        }

        if (NativeMethods.DwmGetWindowAttribute(_window, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 &&
            cloaked != 0)
        {
            return DesktopAttachmentState.Ok;
        }

        if (!IsValidDesktopIconView(_desktopIconView, _desktopIconViewPid))
        {
            SetDesktopIconView(FindDesktopIconView(allowWorkerSpawn: false));
        }

        if (_desktopIconView == IntPtr.Zero)
        {
            return DesktopAttachmentState.Ok;
        }

        if (!_expanded && NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT) != _desktopIconView)
        {
            return DesktopAttachmentState.OwnerLost;
        }

        if (!NativeMethods.IsWindowVisible(_window))
        {
            return DesktopAttachmentState.Hidden;
        }

        return IsBehindDesktopLayer() ? DesktopAttachmentState.BehindDesktop : DesktopAttachmentState.Ok;
    }

    private bool IsBehindDesktopLayer()
    {
        IntPtr iconView = _desktopIconView;
        if (iconView == IntPtr.Zero) return false;
        IntPtr desktopRoot = NativeMethods.GetAncestor(iconView, NativeMethods.GA_ROOT);
        if (desktopRoot == IntPtr.Zero) desktopRoot = iconView;

        for (IntPtr walk = NativeMethods.GetTopWindow(IntPtr.Zero);
             walk != IntPtr.Zero;
             walk = NativeMethods.GetWindow(walk, NativeMethods.GW_HWNDNEXT))
        {
            if (walk == _window) return false;
            if (walk == desktopRoot) return true;
        }

        return false;
    }

    public void SetTransientOwner(IntPtr owner)
    {
        if (owner == IntPtr.Zero || !NativeMethods.IsWindow(owner)) return;
        if (NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT) != owner)
            _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, owner);
        _ = NativeMethods.SetWindowPos(
            _window,
            NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOOWNERZORDER);
    }

    public void SetInputActivation(bool enabled)
    {
        _allowActivation = enabled;
        if (enabled && _stayTopmost)
        {
            _ = NativeMethods.SetWindowPos(
                _window,
                NativeMethods.HWND_NOTOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER);
        }
        long style = NativeMethods.GetWindowLongPtr(_window, NativeMethods.GWL_EXSTYLE).ToInt64();
        style = enabled ? style & ~NativeMethods.WS_EX_NOACTIVATE : style | NativeMethods.WS_EX_NOACTIVATE;
        _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
        _ = NativeMethods.SetWindowPos(
            _window,
            NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOOWNERZORDER);

        if (enabled)
        {
            _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, _expandedOwner);
            _ = NativeMethods.SetWindowPos(
                _window,
                NativeMethods.HWND_TOP,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW |
                NativeMethods.SWP_NOOWNERZORDER);
            _ = NativeMethods.SetForegroundWindow(_window);
        }
        else
        {
            Reattach();
        }
    }

    public void Dispose()
    {
        if (NativeMethods.IsWindow(_window))
        {
            _ = NativeMethods.RemoveWindowSubclass(_window, _activationGuard, ActivationSubclassId);
            _ = NativeMethods.SetWindowLongPtr(_window, NativeMethods.GWLP_HWNDPARENT, _expandedOwner);
        }
    }

    private void SetDesktopIconView(IntPtr view)
    {
        _desktopIconView = view;
        if (view != IntPtr.Zero)
            _ = NativeMethods.GetWindowThreadProcessId(view, out _desktopIconViewPid);
        else
            _desktopIconViewPid = 0;
    }

    private void ApplyToolWindowStyle() => ApplyInitialToolWindowStyle(_window);

    internal static void ApplyInitialToolWindowStyle(IntPtr window)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window)) return;
        long windowStyle = NativeMethods.GetWindowLongPtr(window, NativeMethods.GWL_STYLE).ToInt64();
        windowStyle &= ~(
            NativeMethods.WS_CAPTION |
            NativeMethods.WS_THICKFRAME |
            NativeMethods.WS_SYSMENU |
            NativeMethods.WS_MINIMIZEBOX |
            NativeMethods.WS_MAXIMIZEBOX);
        windowStyle |= NativeMethods.WS_POPUP;
        _ = NativeMethods.SetWindowLongPtr(window, NativeMethods.GWL_STYLE, new IntPtr(windowStyle));

        long style = NativeMethods.GetWindowLongPtr(window, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        style &= ~NativeMethods.WS_EX_APPWINDOW;
        _ = NativeMethods.SetWindowLongPtr(window, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
        _ = NativeMethods.SetWindowPos(
            window,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    private void RaiseAboveNormalWindows()
    {
        // HWND_TOP cannot reliably cross the foreground boundary for a no-activate window.
        const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW;
        if (NativeMethods.SetWindowPos(_window, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags))
        {
            _ = NativeMethods.SetWindowPos(_window, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        }
    }

    internal void ApplyTransparentChrome() => ApplyTransparentChrome(_window);

    internal static void ApplyTransparentChrome(IntPtr window)
    {
        if (!NativeMethods.SupportsWindows11DwmAttributes ||
            window == IntPtr.Zero ||
            !NativeMethods.IsWindow(window))
            return;

        int corner = NativeMethods.DWMWCP_DONOTROUND;
        _ = NativeMethods.DwmSetWindowAttribute(
            window,
            NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref corner,
            sizeof(int));
        int border = NativeMethods.DWMWA_COLOR_NONE;
        _ = NativeMethods.DwmSetWindowAttribute(
            window,
            NativeMethods.DWMWA_BORDER_COLOR,
            ref border,
            sizeof(int));
    }

    private IntPtr ActivationGuard(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, IntPtr referenceData)
    {
        if (message == NativeMethods.WM_MOUSEACTIVATE && !_allowActivation)
        {
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        if (message == NativeMethods.WM_GETMINMAXINFO)
        {
            NativeMethods.MINMAXINFO info = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
            info.MinTrackSize = new NativeMethods.POINT { X = 1, Y = 1 };
            System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
            return IntPtr.Zero;
        }
        IntPtr result = NativeMethods.DefSubclassProc(hWnd, message, wParam, lParam);
        if (message is NativeMethods.WM_NCACTIVATE or
            NativeMethods.WM_THEMECHANGED or
            NativeMethods.WM_DWMCOMPOSITIONCHANGED or
            NativeMethods.WM_SETTINGCHANGE)
        {
            ApplyTransparentChrome();
        }
        return result;
    }

    internal static IntPtr FindDesktopIconView(bool allowWorkerSpawn = true)
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (!IsDesktopHostWindow(window)) return true;
            IntPtr child = NativeMethods.FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child == IntPtr.Zero)
            {
                return true;
            }
            found = child;
            return false;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero)
        {
            CacheDesktopIconView(found);
            return found;
        }

        if (!allowWorkerSpawn) return IntPtr.Zero;

        IntPtr progman = NativeMethods.FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            _ = NativeMethods.SendMessageTimeout(progman, SpawnWorkerMessage, UIntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
            found = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        }
        if (found != IntPtr.Zero) CacheDesktopIconView(found);
        return found;
    }

    internal static IntPtr GetCachedDesktopIconView()
    {
        IntPtr cached;
        uint pid;
        lock (CachedViewGate)
        {
            cached = _lastKnownDesktopIconView;
            pid = _lastKnownDesktopProcessId;
        }
        if (IsValidDesktopIconView(cached, pid)) return cached;
        InvalidateCachedDesktopIconView();
        return IntPtr.Zero;
    }

    internal static void InvalidateCachedDesktopIconView()
    {
        lock (CachedViewGate)
        {
            _lastKnownDesktopIconView = IntPtr.Zero;
            _lastKnownDesktopProcessId = 0;
        }
    }

    private static void CacheDesktopIconView(IntPtr view)
    {
        if (view == IntPtr.Zero) return;
        _ = NativeMethods.GetWindowThreadProcessId(view, out uint pid);
        lock (CachedViewGate)
        {
            _lastKnownDesktopIconView = view;
            _lastKnownDesktopProcessId = pid;
        }
    }

    internal static bool IsValidDesktopIconView(IntPtr view, uint expectedProcessId = 0)
    {
        if (view == IntPtr.Zero || !NativeMethods.IsWindow(view)) return false;
        if (expectedProcessId != 0)
        {
            _ = NativeMethods.GetWindowThreadProcessId(view, out uint currentPid);
            if (currentPid == 0 || currentPid != expectedProcessId) return false;
        }
        var className = new System.Text.StringBuilder(32);
        if (NativeMethods.GetClassName(view, className, className.Capacity) <= 0 ||
            !string.Equals(className.ToString(), "SHELLDLL_DefView", StringComparison.Ordinal))
        {
            return false;
        }
        IntPtr parent = NativeMethods.GetParent(view);
        return parent != IntPtr.Zero && IsDesktopHostWindow(parent);
    }

    internal static bool IsDesktopHostClassName(string? className) =>
        className is "Progman" or "WorkerW";

    private static bool IsDesktopHostWindow(IntPtr window)
    {
        var className = new System.Text.StringBuilder(32);
        return NativeMethods.GetClassName(window, className, className.Capacity) > 0 &&
            IsDesktopHostClassName(className.ToString());
    }
}
