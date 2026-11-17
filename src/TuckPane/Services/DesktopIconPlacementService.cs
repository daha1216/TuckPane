using System.Runtime.InteropServices;

namespace TuckPane.Services;

internal enum DesktopIconPlacementStatus
{
    Positioned,
    AutoArrangeEnabled,
    Failed
}

internal readonly record struct DesktopIconPlacementResult(
    DesktopIconPlacementStatus Status,
    NativeMethods.POINT? Position,
    string? Warning);

internal sealed class DesktopIconPlacementService
{
    private const int DesktopCsidl = 0;
    private const int ShellWindowClassDesktop = 8;
    private const int FindWindowNeedDispatch = 1;
    private const int Success = 0;
    private const int False = 1;
    private const uint SVSI_POSITIONITEM = 0x00000080;
    private const uint SVSI_NOSTATECHANGE = 0x80000000;

    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserInterface = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid ShellFolderInterface = new("000214E6-0000-0000-C000-000000000046");

    internal async Task<DesktopIconPlacementResult> PositionAsync(
        string finalDesktopPath,
        NativeMethods.POINT dropScreenPoint,
        CancellationToken cancellationToken = default)
    {
        string finalPath = Path.GetFullPath(finalDesktopPath);
        TimeSpan timeout = TimeSpan.FromSeconds(1);
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        DesktopIconPlacementResult last = new(DesktopIconPlacementStatus.Failed, null, "桌面图标视图尚未载入新项目。");
        while (System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = TryPosition(finalPath, dropScreenPoint);
            if (last.Status != DesktopIconPlacementStatus.Failed) return last;
            await Task.Delay(50, cancellationToken);
        }
        return last with { Warning = $"桌面图标定位在 1 秒内未完成：{last.Warning}" };
    }

    private static DesktopIconPlacementResult TryPosition(string finalPath, NativeMethods.POINT dropScreenPoint)
    {
        object? shellWindowsObject = null;
        object? desktopDispatch = null;
        object? browserObject = null;
        NativeMethods.IShellView? shellView = null;
        object? folderObject = null;
        IntPtr childPidl = IntPtr.Zero;
        try
        {
            Type shellWindowsType = Type.GetTypeFromCLSID(
                new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"),
                throwOnError: true)!;
            shellWindowsObject = Activator.CreateInstance(shellWindowsType)
                ?? throw new InvalidOperationException("无法创建桌面 ShellWindows 服务。");
            var shellWindows = (NativeMethods.IShellWindows)shellWindowsObject;
            object location = DesktopCsidl;
            object root = new();
            desktopDispatch = shellWindows.FindWindowSW(
                ref location,
                ref root,
                ShellWindowClassDesktop,
                out _,
                FindWindowNeedDispatch);
            if (desktopDispatch is not NativeMethods.IServiceProvider serviceProvider)
                throw new InvalidCastException("桌面窗口未提供 IServiceProvider。");

            Guid service = TopLevelBrowserService;
            Guid browserIid = ShellBrowserInterface;
            int hresult = serviceProvider.QueryService(ref service, ref browserIid, out IntPtr browserPointer);
            Marshal.ThrowExceptionForHR(hresult);
            try
            {
                browserObject = Marshal.GetObjectForIUnknown(browserPointer);
            }
            finally
            {
                _ = Marshal.Release(browserPointer);
            }

            var browser = (NativeMethods.IShellBrowser)browserObject;
            hresult = browser.QueryActiveShellView(out shellView);
            Marshal.ThrowExceptionForHR(hresult);
            hresult = shellView.GetWindow(out IntPtr activeViewWindow);
            Marshal.ThrowExceptionForHR(hresult);
            IntPtr expectedView = DesktopLayerService.FindDesktopIconView();
            if (expectedView == IntPtr.Zero || activeViewWindow != expectedView)
                throw new InvalidOperationException("活动 Shell 视图不是当前桌面图标视图。");

            var folderView = (NativeMethods.IFolderView)shellView;
            var spacing = new NativeMethods.POINT();
            hresult = folderView.GetSpacing(ref spacing);
            Marshal.ThrowExceptionForHR(hresult);
            if (spacing.X <= 0 || spacing.Y <= 0)
                throw new InvalidOperationException("桌面图标网格间距无效。");

            Guid folderIid = ShellFolderInterface;
            hresult = folderView.GetFolder(ref folderIid, out IntPtr folderPointer);
            Marshal.ThrowExceptionForHR(hresult);
            try
            {
                folderObject = Marshal.GetObjectForIUnknown(folderPointer);
            }
            finally
            {
                _ = Marshal.Release(folderPointer);
            }
            var shellFolder = (NativeMethods.IShellFolder)folderObject;
            hresult = shellFolder.ParseDisplayName(
                IntPtr.Zero,
                IntPtr.Zero,
                Path.GetFileName(finalPath),
                IntPtr.Zero,
                out childPidl,
                IntPtr.Zero);
            Marshal.ThrowExceptionForHR(hresult);

            hresult = folderView.GetItemPosition(childPidl, out _);
            if (hresult != Success)
                return new(DesktopIconPlacementStatus.Failed, null, "Explorer 桌面视图尚未载入新项目。");

            int autoArrange = folderView.GetAutoArrange();
            if (autoArrange == Success)
                return new(DesktopIconPlacementStatus.AutoArrangeEnabled, null, null);
            if (autoArrange != False) Marshal.ThrowExceptionForHR(autoArrange);

            IntPtr listView = NativeMethods.FindWindowEx(activeViewWindow, IntPtr.Zero, "SysListView32", "FolderView");
            if (listView == IntPtr.Zero)
                throw new InvalidOperationException("无法读取桌面图标视图范围。");

            NativeMethods.POINT dropClientPoint = dropScreenPoint;
            if (!NativeMethods.ScreenToClient(listView, ref dropClientPoint))
                throw new InvalidOperationException("无法把桌面落点转换到图标视图客户区。");
            IntPtr monitor = NativeMethods.MonitorFromPoint(dropScreenPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                szDevice = string.Empty
            };
            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
                throw new InvalidOperationException("无法读取落点显示器工作区。");
            var workTopLeft = new NativeMethods.POINT { X = monitorInfo.rcWork.Left, Y = monitorInfo.rcWork.Top };
            var workBottomRight = new NativeMethods.POINT { X = monitorInfo.rcWork.Right, Y = monitorInfo.rcWork.Bottom };
            if (!NativeMethods.ScreenToClient(listView, ref workTopLeft) ||
                !NativeMethods.ScreenToClient(listView, ref workBottomRight))
                throw new InvalidOperationException("无法转换显示器工作区坐标。");
            var monitorClientBounds = new NativeMethods.RECT
            {
                Left = Math.Min(workTopLeft.X, workBottomRight.X),
                Top = Math.Min(workTopLeft.Y, workBottomRight.Y),
                Right = Math.Max(workTopLeft.X, workBottomRight.X),
                Bottom = Math.Max(workTopLeft.Y, workBottomRight.Y)
            };
            NativeMethods.POINT position = DesktopIconPlacementMath.SnapToGrid(dropClientPoint, monitorClientBounds, spacing);
            hresult = folderView.SelectAndPositionItems(
                1,
                [childPidl],
                [position],
                SVSI_POSITIONITEM | SVSI_NOSTATECHANGE);
            Marshal.ThrowExceptionForHR(hresult);
            hresult = folderView.GetItemPosition(childPidl, out NativeMethods.POINT actualPosition);
            if (hresult != Success)
                return new(DesktopIconPlacementStatus.Failed, null, "桌面图标写入后无法读回位置。");
            int toleranceX = Math.Max(12, spacing.X / 2);
            int toleranceY = Math.Max(12, spacing.Y / 2);
            bool onTargetMonitor = actualPosition.X >= monitorClientBounds.Left &&
                actualPosition.Y >= monitorClientBounds.Top &&
                actualPosition.X + spacing.X <= monitorClientBounds.Right &&
                actualPosition.Y + spacing.Y <= monitorClientBounds.Bottom;
            if (!onTargetMonitor || Math.Abs(actualPosition.X - position.X) > toleranceX ||
                Math.Abs(actualPosition.Y - position.Y) > toleranceY)
            {
                return new(DesktopIconPlacementStatus.Failed, actualPosition, "桌面图标读回位置与目标网格不一致。");
            }
            return new(DesktopIconPlacementStatus.Positioned, actualPosition, null);
        }
        catch (Exception ex)
        {
            return new(DesktopIconPlacementStatus.Failed, null, ex.Message);
        }
        finally
        {
            if (childPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(childPidl);
            ReleaseComObject(folderObject);
            ReleaseComObject(shellView);
            ReleaseComObject(browserObject);
            ReleaseComObject(desktopDispatch);
            ReleaseComObject(shellWindowsObject);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.ReleaseComObject(value);
    }
}
