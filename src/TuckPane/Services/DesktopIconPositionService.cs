using System.Runtime.InteropServices;
using TuckPane.Core;

namespace TuckPane.Services;

internal static class DesktopIconPositionService
{
    private const int MaxPollAttempts = 10;
    private const int PollDelayMs = 65;

    internal static Task<int> PositionDroppedDesktopItemsAsync(
        IReadOnlyList<string> destinationPaths,
        NativeMethods.POINT dropPointPx,
        CancellationToken cancellationToken = default)
    {
        if (AppPaths.IsTestMode ||
            cancellationToken.IsCancellationRequested ||
            destinationPaths is null ||
            destinationPaths.Count == 0)
        {
            return Task.FromResult(0);
        }

        List<string> normalizedTargets = NormalizeTargets(destinationPaths);
        if (normalizedTargets.Count == 0)
            return Task.FromResult(0);

        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                int positioned = PositionDroppedItemsOnSta(normalizedTargets, dropPointPx, cancellationToken);
                completion.TrySetResult(positioned);
            }
            catch (OperationCanceledException)
            {
                completion.TrySetResult(0);
            }
            catch (Exception ex)
            {
                AppLogger.Error("桌面图标精准落位失败。", ex);
                completion.TrySetResult(0);
            }
        })
        {
            Name = "TuckPane.DesktopIconPosition",
            IsBackground = true
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private static int PositionDroppedItemsOnSta(
        IReadOnlyList<string> normalizedTargets,
        NativeMethods.POINT dropPointPx,
        CancellationToken cancellationToken)
    {
        object? shellWindowsObj = null;
        object? desktopWindowObj = null;
        IntPtr browserPtr = IntPtr.Zero;
        object? browserObj = null;
        NativeMethods.IShellView? shellView = null;
        IntPtr desktopFolderPtr = IntPtr.Zero;

        var matchedChildPidls = new Dictionary<int, IntPtr>(normalizedTargets.Count);
        var fallbackFullPidls = new List<IntPtr>();

        try
        {
            Type? shellWindowsType = Type.GetTypeFromCLSID(FloatingDragNativeMethods.CLSID_ShellWindows);
            if (shellWindowsType is null) return 0;

            shellWindowsObj = Activator.CreateInstance(shellWindowsType);
            if (shellWindowsObj is not NativeMethods.IShellWindows shellWindows) return 0;

            object emptyLocation = 0;
            object emptyRoot = 0;
            desktopWindowObj = shellWindows.FindWindowSW(
                ref emptyLocation,
                ref emptyRoot,
                FloatingDragNativeMethods.SWC_DESKTOP,
                out _,
                FloatingDragNativeMethods.SWFO_NEEDDISPATCH);
            if (desktopWindowObj is not NativeMethods.IServiceProvider serviceProvider) return 0;

            Guid sid = FloatingDragNativeMethods.SID_STopLevelBrowser;
            Guid browserIid = FloatingDragNativeMethods.IID_IShellBrowser;
            if (serviceProvider.QueryService(ref sid, ref browserIid, out browserPtr) < 0 || browserPtr == IntPtr.Zero)
                return 0;

            browserObj = Marshal.GetObjectForIUnknown(browserPtr);
            if (browserObj is not NativeMethods.IShellBrowser shellBrowser) return 0;
            if (shellBrowser.QueryActiveShellView(out shellView) < 0 || shellView is null) return 0;
            if (shellView is not NativeMethods.IFolderView folderView) return 0;

            FloatingDragNativeMethods.IFolderView2? folderView2 = shellView as FloatingDragNativeMethods.IFolderView2;
            if (IsAutoArrangeEnabled(folderView, folderView2))
                return 0;

            IntPtr listViewHwnd = ResolveDesktopListViewHandle(shellView, out IntPtr defViewHwnd);
            IntPtr coordinateHwnd = listViewHwnd != IntPtr.Zero ? listViewHwnd : defViewHwnd;

            DisplayInfo display = DisplayPlacementService.ForBounds(new NativeMethods.RECT
            {
                Left = dropPointPx.X,
                Top = dropPointPx.Y,
                Right = dropPointPx.X + 1,
                Bottom = dropPointPx.Y + 1
            });

            (int cellWidth, int cellHeight) = ResolveDesktopCellSpacing(folderView, listViewHwnd, display);
            int iconCenterY = Math.Clamp((int)Math.Round(28.0 * Math.Max(1.0, display.Scale)), 16, Math.Max(16, cellHeight / 2));
            int anchorX = dropPointPx.X - cellWidth / 2;
            int anchorY = dropPointPx.Y - iconCenterY;

            IReadOnlyList<(int X, int Y)> fanScreenPositions = CrossWindowDragMath.ComputeDesktopDropFanPositions(
                anchorX,
                anchorY,
                normalizedTargets.Count,
                cellWidth,
                cellHeight,
                display.Work.Left,
                display.Work.Top,
                display.Work.Right,
                display.Work.Bottom);

            var targetClientPoints = new NativeMethods.POINT[normalizedTargets.Count];
            for (int i = 0; i < normalizedTargets.Count; i++)
            {
                var pt = new NativeMethods.POINT
                {
                    X = fanScreenPositions[i].X,
                    Y = fanScreenPositions[i].Y
                };
                if (coordinateHwnd != IntPtr.Zero)
                    _ = NativeMethods.ScreenToClient(coordinateHwnd, ref pt);
                targetClientPoints[i] = pt;
            }

            var targetIndexByPath = new Dictionary<string, int>(normalizedTargets.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < normalizedTargets.Count; i++)
                targetIndexByPath[normalizedTargets[i]] = i;

            if (folderView2 is null)
            {
                Guid shellFolderIid = FloatingDragNativeMethods.IID_IShellFolder;
                _ = folderView.GetFolder(ref shellFolderIid, out desktopFolderPtr);
            }

            for (int attempt = 0; attempt < MaxPollAttempts; attempt++)
            {
                if (cancellationToken.IsCancellationRequested) return 0;

                CollectMatchingDesktopItems(
                    folderView,
                    folderView2,
                    desktopFolderPtr,
                    targetIndexByPath,
                    matchedChildPidls);

                if (matchedChildPidls.Count >= normalizedTargets.Count)
                    break;

                if (attempt + 1 < MaxPollAttempts)
                {
                    if (cancellationToken.CanBeCanceled)
                    {
                        if (cancellationToken.WaitHandle.WaitOne(PollDelayMs))
                            return 0;
                    }
                    else
                    {
                        Thread.Sleep(PollDelayMs);
                    }
                }
            }

            if (cancellationToken.IsCancellationRequested) return 0;

            // Fallback: for any existing destination path not yet enumerated via IFolderView2,
            // derive its child PIDL via SHParseDisplayName + ILFindLastID.
            var finalPidls = new List<IntPtr>(normalizedTargets.Count);
            var finalPoints = new List<NativeMethods.POINT>(normalizedTargets.Count);
            for (int i = 0; i < normalizedTargets.Count; i++)
            {
                if (matchedChildPidls.TryGetValue(i, out IntPtr childPidl) && childPidl != IntPtr.Zero)
                {
                    finalPidls.Add(childPidl);
                    finalPoints.Add(targetClientPoints[i]);
                    continue;
                }

                string path = normalizedTargets[i];
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                if (NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out IntPtr fullPidl, 0, out _) >= 0 &&
                    fullPidl != IntPtr.Zero)
                {
                    fallbackFullPidls.Add(fullPidl);
                    IntPtr lastId = FloatingDragNativeMethods.ILFindLastID(fullPidl);
                    if (lastId != IntPtr.Zero)
                    {
                        finalPidls.Add(lastId);
                        finalPoints.Add(targetClientPoints[i]);
                    }
                }
            }

            if (finalPidls.Count == 0) return 0;

            int hr = folderView.SelectAndPositionItems(
                (uint)finalPidls.Count,
                finalPidls.ToArray(),
                finalPoints.ToArray(),
                FloatingDragNativeMethods.SVSI_POSITIONITEM);

            return hr >= 0 ? finalPidls.Count : 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            AppLogger.Error("Shell IFolderView 桌面图标定位异常。", ex);
            return 0;
        }
        finally
        {
            foreach (IntPtr pidl in matchedChildPidls.Values)
            {
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            }
            foreach (IntPtr fullPidl in fallbackFullPidls)
            {
                if (fullPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(fullPidl);
            }

            if (desktopFolderPtr != IntPtr.Zero) _ = Marshal.Release(desktopFolderPtr);
            // Note: folderView / folderView2 are QI casts on the same RCW as shellView — release shellView once.
            SafeFinalReleaseCom(shellView);
            SafeFinalReleaseCom(browserObj);
            if (browserPtr != IntPtr.Zero) _ = Marshal.Release(browserPtr);
            SafeFinalReleaseCom(desktopWindowObj);
            SafeFinalReleaseCom(shellWindowsObj);
        }
    }

    private static bool IsAutoArrangeEnabled(
        NativeMethods.IFolderView folderView,
        FloatingDragNativeMethods.IFolderView2? folderView2)
    {
        if (folderView2 is not null && folderView2.GetCurrentFolderFlags(out uint flags) >= 0)
            return (flags & FloatingDragNativeMethods.FWF_AUTOARRANGE) != 0;

        // IFolderView::GetAutoArrange returns S_OK (0) when Auto-Arrange is enabled, S_FALSE (1) when disabled.
        return folderView.GetAutoArrange() == 0;
    }

    private static void CollectMatchingDesktopItems(
        NativeMethods.IFolderView folderView,
        FloatingDragNativeMethods.IFolderView2? folderView2,
        IntPtr desktopFolderPtr,
        Dictionary<string, int> targetIndexByPath,
        Dictionary<int, IntPtr> matchedChildPidls)
    {
        if (folderView.ItemCount(FloatingDragNativeMethods.SVGIO_ALLVIEW, out int totalCount) < 0 ||
            totalCount <= 0 ||
            totalCount > 10000)
        {
            return;
        }

        Guid shellItemIid = FloatingDragNativeMethods.IID_IShellItem;
        for (int index = 0; index < totalCount && matchedChildPidls.Count < targetIndexByPath.Count; index++)
        {
            IntPtr childPidl = IntPtr.Zero;
            IntPtr itemPtr = IntPtr.Zero;
            object? itemObj = null;
            bool ownershipTransferred = false;

            try
            {
                if (folderView2 is not null)
                {
                    if (folderView2.GetItem(index, ref shellItemIid, out itemPtr) < 0 || itemPtr == IntPtr.Zero)
                        continue;
                }
                else
                {
                    if (desktopFolderPtr == IntPtr.Zero) continue;
                    if (folderView.Item(index, out childPidl) < 0 || childPidl == IntPtr.Zero)
                        continue;
                    if (FloatingDragNativeMethods.SHCreateItemWithParent(
                            IntPtr.Zero,
                            desktopFolderPtr,
                            childPidl,
                            ref shellItemIid,
                            out itemPtr) < 0 || itemPtr == IntPtr.Zero)
                    {
                        continue;
                    }
                }

                itemObj = Marshal.GetObjectForIUnknown(itemPtr);
                if (itemObj is not NativeMethods.IShellItem shellItem) continue;

                string? itemPath = TryReadNormalizedItemPath(shellItem);
                if (itemPath is null ||
                    !targetIndexByPath.TryGetValue(itemPath, out int targetIndex) ||
                    matchedChildPidls.ContainsKey(targetIndex))
                {
                    continue;
                }

                if (childPidl == IntPtr.Zero)
                {
                    if (folderView.Item(index, out childPidl) < 0 || childPidl == IntPtr.Zero)
                        continue;
                }

                matchedChildPidls[targetIndex] = childPidl;
                ownershipTransferred = true;
            }
            catch
            {
                // Skip individual virtual/inaccessible shell items safely.
            }
            finally
            {
                if (!ownershipTransferred && childPidl != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(childPidl);
                SafeFinalReleaseCom(itemObj);
                if (itemPtr != IntPtr.Zero) _ = Marshal.Release(itemPtr);
            }
        }
    }

    private static string? TryReadNormalizedItemPath(NativeMethods.IShellItem shellItem)
    {
        IntPtr namePtr = IntPtr.Zero;
        try
        {
            int hr = shellItem.GetDisplayName(FloatingDragNativeMethods.SIGDN_FILESYSPATH, out namePtr);
            if (hr < 0 || namePtr == IntPtr.Zero)
            {
                if (namePtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(namePtr);
                    namePtr = IntPtr.Zero;
                }
                hr = shellItem.GetDisplayName(FloatingDragNativeMethods.SIGDN_DESKTOPABSOLUTEPARSING, out namePtr);
            }

            if (hr < 0 || namePtr == IntPtr.Zero) return null;
            string? raw = Marshal.PtrToStringUni(namePtr);
            return NormalizePathOrNull(raw);
        }
        finally
        {
            if (namePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(namePtr);
        }
    }

    private static IntPtr ResolveDesktopListViewHandle(NativeMethods.IShellView shellView, out IntPtr defViewHwnd)
    {
        defViewHwnd = IntPtr.Zero;
        if (shellView.GetWindow(out IntPtr viewHwnd) >= 0 && viewHwnd != IntPtr.Zero)
        {
            defViewHwnd = viewHwnd;
            IntPtr listView = NativeMethods.FindWindowEx(viewHwnd, IntPtr.Zero, "SysListView32", "FolderView");
            if (listView != IntPtr.Zero) return listView;
        }

        IntPtr cachedView = DesktopLayerService.GetCachedDesktopIconView();
        if (cachedView == IntPtr.Zero)
            cachedView = DesktopLayerService.FindDesktopIconView(allowWorkerSpawn: false);
        if (cachedView != IntPtr.Zero)
        {
            if (defViewHwnd == IntPtr.Zero) defViewHwnd = cachedView;
            return NativeMethods.FindWindowEx(cachedView, IntPtr.Zero, "SysListView32", "FolderView");
        }

        return IntPtr.Zero;
    }

    private static (int CellWidthPx, int CellHeightPx) ResolveDesktopCellSpacing(
        NativeMethods.IFolderView folderView,
        IntPtr listViewHwnd,
        DisplayInfo display)
    {
        if (listViewHwnd != IntPtr.Zero &&
            NativeMethods.SendMessageTimeout(
                listViewHwnd,
                NativeMethods.LVM_GETITEMSPACING,
                UIntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG,
                1000,
                out UIntPtr packedSpacing) != IntPtr.Zero &&
            DesktopGridService.TryDecodeItemSpacing(packedSpacing, out int activeW, out int activeH))
        {
            return (Math.Max(48, activeW), Math.Max(48, activeH));
        }

        var spacing = new NativeMethods.POINT();
        if (folderView.GetSpacing(ref spacing) >= 0 &&
            spacing.X is >= 48 and <= 512 &&
            spacing.Y is >= 48 and <= 512)
        {
            return (spacing.X, spacing.Y);
        }

        if (folderView.GetDefaultSpacing(out NativeMethods.POINT defaultSpacing) >= 0 &&
            defaultSpacing.X is >= 48 and <= 512 &&
            defaultSpacing.Y is >= 48 and <= 512)
        {
            return (defaultSpacing.X, defaultSpacing.Y);
        }

        uint dpi = (uint)Math.Max(96, Math.Round(display.Scale * 96.0));
        var metrics = new NativeMethods.ICONMETRICS
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.ICONMETRICS>(),
            Font = new NativeMethods.LOGFONT { FaceName = string.Empty }
        };
        bool readMetrics = NativeMethods.SystemParametersInfoForDpi(
            NativeMethods.SPI_GETICONMETRICS,
            metrics.Size,
            ref metrics,
            0,
            dpi);
        int width = readMetrics ? metrics.HorizontalSpacing : NativeMethods.GetSystemMetrics(NativeMethods.SM_CXICONSPACING);
        int height = readMetrics ? metrics.VerticalSpacing : NativeMethods.GetSystemMetrics(NativeMethods.SM_CYICONSPACING);
        return (Math.Max(48, width), Math.Max(48, height));
    }

    private static List<string> NormalizeTargets(IReadOnlyList<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            string? normalized = NormalizePathOrNull(path);
            if (normalized is not null && seen.Add(normalized))
                result.Add(normalized);
        }
        return result;
    }

    private static string? NormalizePathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string trimmed = path.Trim();
        if (trimmed.StartsWith("::{", StringComparison.Ordinal)) return null;
        try
        {
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private static void SafeFinalReleaseCom(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try { _ = Marshal.FinalReleaseComObject(comObject); }
            catch { }
        }
    }
}
