using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using TuckPane.Models;
using Windows.ApplicationModel.DataTransfer;

namespace TuckPane.Services;

[ComVisible(true)]
[Guid("00000121-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropSource
{
    [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, uint keyState);
    [PreserveSig] int GiveFeedback(uint effect);
}

[StructLayout(LayoutKind.Sequential)]
public struct ShellDragSize { public int Width; public int Height; }

[StructLayout(LayoutKind.Sequential)]
public struct ShellDragPoint { public int X; public int Y; }

[StructLayout(LayoutKind.Sequential)]
public struct ShellDragImage
{
    public ShellDragSize Size;
    public ShellDragPoint Offset;
    public IntPtr Bitmap;
    public uint ColorKey;
}

[ComImport]
[Guid("DE5BF786-477A-11D2-839D-00C04FD918D0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellDragSourceHelper
{
    [PreserveSig] int InitializeFromBitmap(ref ShellDragImage dragImage, IntPtr dataObject);
    [PreserveSig] int InitializeFromWindow(IntPtr window, ref ShellDragPoint point, IntPtr dataObject);
}

internal enum ShellDragOutcome
{
    ExternalCopied,
    ExternalMoved,
    ExternalLinked,
    DesktopRequested,
    Cancelled,
}

internal readonly record struct ShellDragFeedbackSnapshot(
    NativeMethods.POINT Cursor,
    bool OverDesktop,
    bool ControlHeld,
    uint Effect);

internal readonly record struct ShellDragResult(
    ShellDragOutcome Outcome,
    NativeMethods.POINT? DesktopDropPoint,
    bool DesktopCopyRequested,
    TimeSpan PreparationDuration,
    TimeSpan? FirstFeedbackDelay,
    int CallbackCount,
    TimeSpan MaximumCallbackInterval,
    TimeSpan DragDuration,
    NativeMethods.POINT? LastCursorPoint = null);

internal static class ShellDragService
{
    private const int DragImageSize = 64;
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;
    private const uint DropEffectLink = 4;
    private const short CfHDrop = 15;
    private const uint GlobalMoveable = 0x0002;
    private const uint GlobalZeroInit = 0x0040;
    private const uint MouseKeyLeft = 0x0001;
    private const uint MouseKeyControl = 0x0008;
    private const int Success = 0;
    private const int DragDropDrop = 0x00040100;
    private const int DragDropCancel = 0x00040101;
    private const int DragDropUseDefaultCursors = 0x00040102;
    private static readonly Guid ShellItemInterface = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid DataObjectHandler = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    private static readonly Guid DataObjectInterface = new("0000010E-0000-0000-C000-000000000046");
    private static readonly Guid DragDropHelperClass = new("4657278A-411B-11D2-839A-00C04FD918D0");
    private const string PreferredDropEffectFormat = "Preferred DropEffect";

    internal static bool RequiresNativeDrag(WidgetItemKind kind) => Enum.IsDefined(kind);

    internal static void SetCutClipboard(string path) => SetClipboard([path], DropEffectMove);

    internal static void SetCopyClipboard(string path) => SetClipboard([path], DropEffectCopy);

    internal static void SetCutClipboard(IReadOnlyList<string> paths) => SetClipboard(paths, DropEffectMove);

    internal static void SetCopyClipboard(IReadOnlyList<string> paths) => SetClipboard(paths, DropEffectCopy);

    private static void SetClipboard(IReadOnlyList<string> paths, uint preferredEffect)
    {
        int oleResult = NativeMethods.OleInitialize(IntPtr.Zero);
        if (oleResult < 0) Marshal.ThrowExceptionForHR(oleResult);
        try
        {
            using ShellDataObject data = CreateDataObject(paths);
            object dataObject = Marshal.GetObjectForIUnknown(data.Pointer);
            try
            {
                SetPreferredDropEffect((IDataObject)dataObject, preferredEffect);
                Marshal.ThrowExceptionForHR(NativeMethods.OleSetClipboard(data.Pointer));
                Marshal.ThrowExceptionForHR(NativeMethods.OleFlushClipboard());
            }
            finally
            {
                if (Marshal.IsComObject(dataObject)) _ = Marshal.ReleaseComObject(dataObject);
            }
        }
        finally
        {
            NativeMethods.OleUninitialize();
        }
    }

    internal static bool TryGetClipboardPaths(out string[] paths, out bool move)
    {
        paths = [];
        move = false;
        int oleResult = NativeMethods.OleInitialize(IntPtr.Zero);
        if (oleResult < 0) return false;
        IntPtr pointer = IntPtr.Zero;
        object? dataObject = null;
        try
        {
            if (NativeMethods.OleGetClipboard(out pointer) < 0 || pointer == IntPtr.Zero) return false;
            dataObject = Marshal.GetObjectForIUnknown(pointer);
            var clipboard = (IDataObject)dataObject;
            paths = ReadFileDropPaths(clipboard);
            move = (ReadPreferredDropEffect(clipboard) & DropEffectMove) != 0;
            return paths.Length > 0;
        }
        catch (COMException)
        {
            paths = [];
            move = false;
            return false;
        }
        finally
        {
            if (dataObject is not null && Marshal.IsComObject(dataObject)) _ = Marshal.ReleaseComObject(dataObject);
            if (pointer != IntPtr.Zero) _ = Marshal.Release(pointer);
            NativeMethods.OleUninitialize();
        }
    }

    internal static bool HasFileDrop(DataPackageView dataView) =>
        TryUseDataObject(dataView, dataObject =>
        {
            FORMATETC format = CreateFormat(CfHDrop);
            return dataObject.QueryGetData(ref format) == 0;
        });

    internal static bool TryGetFileDropPaths(DataPackageView dataView, out string[] paths)
    {
        string[] result = [];
        bool read = TryUseDataObject(dataView, dataObject =>
        {
            result = ReadFileDropPaths(dataObject)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return result.Length > 0;
        });
        paths = result;
        return read;
    }

    private static bool TryUseDataObject(DataPackageView dataView, Func<IDataObject, bool> action)
    {
        IntPtr unknown = IntPtr.Zero;
        IntPtr dataObjectPointer = IntPtr.Zero;
        object? dataObject = null;
        try
        {
            unknown = Marshal.GetIUnknownForObject(dataView);
            Guid interfaceId = DataObjectInterface;
            if (Marshal.QueryInterface(unknown, in interfaceId, out dataObjectPointer) < 0 ||
                dataObjectPointer == IntPtr.Zero) return false;
            dataObject = Marshal.GetTypedObjectForIUnknown(dataObjectPointer, typeof(IDataObject));
            return action((IDataObject)dataObject);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or
                                   ArgumentException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (dataObject is not null && Marshal.IsComObject(dataObject)) _ = Marshal.ReleaseComObject(dataObject);
            if (dataObjectPointer != IntPtr.Zero) _ = Marshal.Release(dataObjectPointer);
            if (unknown != IntPtr.Zero) _ = Marshal.Release(unknown);
        }
    }

    private static void SetPreferredDropEffect(IDataObject dataObject, uint effect)
    {
        FORMATETC format = CreateFormat(unchecked((short)NativeMethods.RegisterClipboardFormat(PreferredDropEffectFormat)));
        IntPtr memory = NativeMethods.GlobalAlloc(GlobalMoveable | GlobalZeroInit, (UIntPtr)sizeof(uint));
        if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        try
        {
            IntPtr value = NativeMethods.GlobalLock(memory);
            if (value == IntPtr.Zero) throw new OutOfMemoryException();
            try { Marshal.WriteInt32(value, unchecked((int)effect)); }
            finally { _ = NativeMethods.GlobalUnlock(memory); }
            var medium = new STGMEDIUM
            {
                tymed = TYMED.TYMED_HGLOBAL,
                unionmember = memory,
                pUnkForRelease = null
            };
            dataObject.SetData(ref format, ref medium, release: true);
            memory = IntPtr.Zero;
        }
        finally
        {
            if (memory != IntPtr.Zero) _ = NativeMethods.GlobalFree(memory);
        }
    }

    private static string[] ReadFileDropPaths(IDataObject dataObject)
    {
        FORMATETC format = CreateFormat(CfHDrop);
        if (dataObject.QueryGetData(ref format) != 0) return [];
        dataObject.GetData(ref format, out STGMEDIUM medium);
        try
        {
            uint count = NativeMethods.DragQueryFile(medium.unionmember, uint.MaxValue, null, 0);
            var result = new List<string>(checked((int)count));
            for (uint index = 0; index < count; index++)
            {
                uint length = NativeMethods.DragQueryFile(medium.unionmember, index, null, 0);
                var buffer = new StringBuilder(checked((int)length + 1));
                if (NativeMethods.DragQueryFile(medium.unionmember, index, buffer, (uint)buffer.Capacity) > 0)
                    result.Add(buffer.ToString());
            }
            return result.ToArray();
        }
        finally
        {
            NativeMethods.ReleaseStgMedium(ref medium);
        }
    }

    private static uint ReadPreferredDropEffect(IDataObject dataObject)
    {
        FORMATETC format = CreateFormat(unchecked((short)NativeMethods.RegisterClipboardFormat(PreferredDropEffectFormat)));
        if (dataObject.QueryGetData(ref format) != 0) return 0;
        dataObject.GetData(ref format, out STGMEDIUM medium);
        try
        {
            IntPtr value = NativeMethods.GlobalLock(medium.unionmember);
            if (value == IntPtr.Zero) return 0;
            try { return unchecked((uint)Marshal.ReadInt32(value)); }
            finally { _ = NativeMethods.GlobalUnlock(medium.unionmember); }
        }
        finally
        {
            NativeMethods.ReleaseStgMedium(ref medium);
        }
    }

    private static FORMATETC CreateFormat(short format) => new()
    {
        cfFormat = format,
        ptd = IntPtr.Zero,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = TYMED.TYMED_HGLOBAL
    };

    internal static ShellDragResult Move(IntPtr owner, string path, Func<bool>? cancellationRequested = null)
    {
        using ShellDragSession session = Prepare(path);
        return Move(owner, session, default, cancellationRequested);
    }

    internal static ShellDragSession Prepare(
        string path,
        double grabRatioX = .5,
        double grabRatioY = .5,
        bool preferMove = false,
        bool suppressSystemDragImage = false)
    {
        long started = Stopwatch.GetTimestamp();
        ShellDataObject data = CreateDataObject(path);
        IntPtr bitmap = IntPtr.Zero;
        bool dragImageInitialized = false;
        try
        {
            if (preferMove)
            {
                object dataObject = Marshal.GetObjectForIUnknown(data.Pointer);
                try { SetPreferredDropEffect((IDataObject)dataObject, DropEffectMove); }
                finally { if (Marshal.IsComObject(dataObject)) _ = Marshal.ReleaseComObject(dataObject); }
            }
            if (!suppressSystemDragImage)
            {
                try
                {
                    bitmap = IconCacheService.CreateDragBitmap(path, DragImageSize);
                    var grabOffset = new ShellDragPoint
                    {
                        X = Math.Clamp((int)Math.Round(grabRatioX * DragImageSize), 0, DragImageSize - 1),
                        Y = Math.Clamp((int)Math.Round(grabRatioY * DragImageSize), 0, DragImageSize - 1)
                    };
                    dragImageInitialized = TryInitializeDragImage(data.Pointer, bitmap, grabOffset);
                    if (dragImageInitialized)
                    {
                        // IDragSourceHelper::InitializeFromBitmap transfers HBITMAP ownership to IDataObject on S_OK.
                        bitmap = IntPtr.Zero;
                    }
                    else if (bitmap != IntPtr.Zero)
                    {
                        _ = NativeMethods.DeleteObject(bitmap);
                        bitmap = IntPtr.Zero;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("无法准备自定义Shell拖动图像，将继续使用系统默认反馈。", ex);
                }
            }
            return new ShellDragSession(path, data, bitmap, dragImageInitialized, Stopwatch.GetElapsedTime(started));
        }
        catch
        {
            if (bitmap != IntPtr.Zero) _ = NativeMethods.DeleteObject(bitmap);
            data.Dispose();
            throw;
        }
    }

    internal static ShellDragResult Move(
        IntPtr owner,
        ShellDragSession session,
        NativeMethods.RECT desktopExclusionBounds,
        Func<bool>? cancellationRequested = null,
        bool allowLink = true,
        Action<ShellDragFeedbackSnapshot>? onFeedback = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ThrowIfDisposed();
        long dragStarted = Stopwatch.GetTimestamp();
        DesktopTargetSnapshot desktopSnapshot = DesktopTargetSnapshot.Capture();
        var dropSource = new DesktopAwareDropSource(
            owner,
            desktopExclusionBounds,
            desktopSnapshot,
            dragStarted,
            cancellationRequested,
            onFeedback);
        int oleResult = NativeMethods.OleInitialize(IntPtr.Zero);
        if (oleResult < 0) Marshal.ThrowExceptionForHR(oleResult);
        IntPtr dropSourcePointer = IntPtr.Zero;
        try
        {
            dropSourcePointer = AcquireDropSourcePointer(dropSource);
            int result = NativeMethods.DoDragDrop(
                session.Data.Pointer,
                dropSourcePointer,
                DropEffectCopy | DropEffectMove | (allowLink ? DropEffectLink : 0),
                out uint performed);
            session.DragDuration = Stopwatch.GetElapsedTime(dragStarted);
            session.FirstFeedbackDelay = dropSource.FirstFeedbackDelay;
            session.CallbackCount = dropSource.CallbackCount;
            session.MaximumCallbackInterval = dropSource.MaximumCallbackInterval;
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            ShellDragOutcome outcome = ClassifyOutcome(dropSource.DesktopRequested, performed);
            return new ShellDragResult(
                outcome,
                dropSource.DesktopDropPoint,
                dropSource.DesktopCopyRequested,
                session.PreparationDuration,
                session.FirstFeedbackDelay,
                session.CallbackCount,
                session.MaximumCallbackInterval,
                session.DragDuration,
                dropSource.LastCursorPoint);
        }
        finally
        {
            if (dropSourcePointer != IntPtr.Zero) _ = Marshal.Release(dropSourcePointer);
            NativeMethods.OleUninitialize();
            GC.KeepAlive(dropSource);
        }
    }

    internal static ShellDragOutcome ClassifyOutcome(bool desktopRequested, uint performed) =>
        desktopRequested ? ShellDragOutcome.DesktopRequested :
        (performed & DropEffectMove) != 0 ? ShellDragOutcome.ExternalMoved :
        (performed & DropEffectCopy) != 0 ? ShellDragOutcome.ExternalCopied :
        (performed & DropEffectLink) != 0 ? ShellDragOutcome.ExternalLinked :
        ShellDragOutcome.Cancelled;

    private static bool TryInitializeDragImage(IntPtr dataObject, IntPtr bitmap, ShellDragPoint grabOffset)
    {
        object? helper = null;
        try
        {
            Type type = Type.GetTypeFromCLSID(DragDropHelperClass, throwOnError: true)!;
            helper = Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法创建Shell拖动图像管理器。");
            var source = (IShellDragSourceHelper)helper;
            var image = new ShellDragImage
            {
                Size = new ShellDragSize { Width = DragImageSize, Height = DragImageSize },
                Offset = grabOffset,
                Bitmap = bitmap,
                ColorKey = uint.MaxValue
            };
            int result = source.InitializeFromBitmap(ref image, dataObject);
            Marshal.ThrowExceptionForHR(result);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Shell拖动图像初始化失败，将继续使用系统默认反馈。", ex);
            return false;
        }
        finally
        {
            if (helper is not null && Marshal.IsComObject(helper)) _ = Marshal.FinalReleaseComObject(helper);
        }
    }

    internal static IntPtr AcquireDropSourcePointer(IDropSource source) =>
        Marshal.GetComInterfaceForObject<object, IDropSource>(source);

    internal static ShellDataObject CreateDataObject(string path) => CreateDataObject([path]);

    internal static ShellDataObject CreateDataObject(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] valid = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (valid.Length == 0)
            throw new FileNotFoundException(AppStrings.Get("ShellDragMissing"));

        if (valid.Length == 1)
        {
            string singlePath = valid[0];
            if (!File.Exists(singlePath) && !Directory.Exists(singlePath))
                throw new FileNotFoundException(AppStrings.Get("ShellDragMissing"), singlePath);
            int result = NativeMethods.SHCreateItemFromParsingName(singlePath, IntPtr.Zero, ShellItemInterface, out NativeMethods.IShellItem item);
            Marshal.ThrowExceptionForHR(result);
            try
            {
                Guid handler = DataObjectHandler;
                Guid iid = DataObjectInterface;
                result = item.BindToHandler(IntPtr.Zero, ref handler, ref iid, out IntPtr pointer);
                Marshal.ThrowExceptionForHR(result);
                return new ShellDataObject(item, pointer);
            }
            catch
            {
                _ = Marshal.FinalReleaseComObject(item);
                throw;
            }
        }

        var pidls = new List<IntPtr>(valid.Length);
        try
        {
            foreach (string path in valid)
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    throw new FileNotFoundException(AppStrings.Get("ShellDragMissing"), path);
                int hr = NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _);
                Marshal.ThrowExceptionForHR(hr);
                pidls.Add(pidl);
            }

            int createHr = NativeMethods.SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out NativeMethods.IShellItemArray itemArray);
            Marshal.ThrowExceptionForHR(createHr);
            try
            {
                Guid handler = DataObjectHandler;
                Guid iid = DataObjectInterface;
                int bindHr = itemArray.BindToHandler(IntPtr.Zero, ref handler, ref iid, out IntPtr pointer);
                Marshal.ThrowExceptionForHR(bindHr);
                return new ShellDataObject(itemArray, pointer);
            }
            catch
            {
                _ = Marshal.FinalReleaseComObject(itemArray);
                throw;
            }
        }
        finally
        {
            foreach (IntPtr pidl in pidls)
            {
                if (pidl != IntPtr.Zero) NativeMethods.ILFree(pidl);
            }
        }
    }

    private sealed class DesktopAwareDropSource(
        IntPtr owner,
        NativeMethods.RECT desktopExclusionBounds,
        DesktopTargetSnapshot desktopSnapshot,
        long dragStarted,
        Func<bool>? cancellationRequested,
        Action<ShellDragFeedbackSnapshot>? onFeedback) : IDropSource
    {
        private static readonly long FeedbackThrottleTicks = Math.Max(1L, Stopwatch.Frequency / 60);

        private long _lastCallbackAt;
        private long _lastFeedbackAt;
        private bool _hasPublishedFeedback;
        private bool _lastOverDesktop;
        private bool _lastControlHeld;
        private uint _lastEffect;

        public bool DesktopRequested { get; private set; }
        public NativeMethods.POINT? DesktopDropPoint { get; private set; }
        public NativeMethods.POINT? LastCursorPoint { get; private set; }
        public bool DesktopCopyRequested { get; private set; }
        public TimeSpan? FirstFeedbackDelay { get; private set; }
        public int CallbackCount { get; private set; }
        public TimeSpan MaximumCallbackInterval { get; private set; }

        public int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, uint keyState)
        {
            long now = NotifyShellLoopActive();
            bool controlHeld = (keyState & MouseKeyControl) != 0;
            bool hasCursor = NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);
            if (hasCursor) LastCursorPoint = cursor;

            if (escapePressed || cancellationRequested?.Invoke() == true) return DragDropCancel;
            if ((keyState & MouseKeyLeft) != 0)
            {
                if (hasCursor && onFeedback is not null)
                    PublishFeedback(now, cursor, controlHeld, _lastEffect);
                return Success;
            }
            if (hasCursor && desktopSnapshot.IsPointOverDesktop(owner, cursor))
            {
                if (DragBoundaryMath.Contains(desktopExclusionBounds, cursor)) return DragDropCancel;
                DesktopRequested = true;
                DesktopDropPoint = cursor;
                DesktopCopyRequested = controlHeld;
                return DragDropCancel;
            }
            return DragDropDrop;
        }

        public int GiveFeedback(uint effect)
        {
            long now = NotifyShellLoopActive();
            if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
            {
                LastCursorPoint = cursor;
                if (onFeedback is not null)
                {
                    bool controlHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
                    PublishFeedback(now, cursor, controlHeld, effect);
                    return DragDropUseDefaultCursors;
                }
            }
            _lastEffect = effect;
            return DragDropUseDefaultCursors;
        }

        private void PublishFeedback(long now, NativeMethods.POINT cursor, bool controlHeld, uint effect)
        {
            bool overDesktop = desktopSnapshot.IsPointOverDesktop(owner, cursor) &&
                !DragBoundaryMath.Contains(desktopExclusionBounds, cursor);
            bool stateChanged = !_hasPublishedFeedback ||
                overDesktop != _lastOverDesktop ||
                controlHeld != _lastControlHeld ||
                effect != _lastEffect;
            if (!stateChanged && now - _lastFeedbackAt < FeedbackThrottleTicks) return;

            _hasPublishedFeedback = true;
            _lastFeedbackAt = now;
            _lastOverDesktop = overDesktop;
            _lastControlHeld = controlHeld;
            _lastEffect = effect;
            try
            {
                onFeedback?.Invoke(new ShellDragFeedbackSnapshot(cursor, overDesktop, controlHeld, effect));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Shell拖动反馈回调失败。", ex);
            }
        }

        private long NotifyShellLoopActive()
        {
            long now = Stopwatch.GetTimestamp();
            if (CallbackCount == 0)
            {
                FirstFeedbackDelay = Stopwatch.GetElapsedTime(dragStarted, now);
            }
            else
            {
                TimeSpan interval = Stopwatch.GetElapsedTime(_lastCallbackAt, now);
                if (interval > MaximumCallbackInterval) MaximumCallbackInterval = interval;
            }
            _lastCallbackAt = now;
            CallbackCount++;
            return now;
        }
    }

    private sealed class DesktopTargetSnapshot
    {
        private DesktopTargetSnapshot(IntPtr desktopView, IntPtr listView, IntPtr desktopHost)
        {
            DesktopView = desktopView;
            ListView = listView;
            DesktopHost = desktopHost;
        }

        private IntPtr DesktopView { get; }
        private IntPtr ListView { get; }
        private IntPtr DesktopHost { get; }

        internal static DesktopTargetSnapshot Capture()
        {
            // Prefer a handle discovered during normal desktop attachment. If
            // none is cached, perform only a bounded EnumWindows lookup; do
            // not send the worker-spawn message on the drag/UI critical path.
            IntPtr desktopView = DesktopLayerService.GetCachedDesktopIconView();
            if (desktopView == IntPtr.Zero)
                desktopView = DesktopLayerService.FindDesktopIconView(allowWorkerSpawn: false);
            return new(
                desktopView,
                desktopView == IntPtr.Zero
                    ? IntPtr.Zero
                    : NativeMethods.FindWindowEx(desktopView, IntPtr.Zero, "SysListView32", "FolderView"),
                desktopView == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetParent(desktopView));
        }

        internal bool IsPointOverDesktop(IntPtr owner, NativeMethods.POINT point)
        {
            if (DesktopView == IntPtr.Zero || !NativeMethods.IsWindow(DesktopView)) return false;
            IntPtr hit = NativeMethods.WindowFromPoint(point);
            if (hit == IntPtr.Zero || hit == owner || NativeMethods.IsChild(owner, hit)) return false;
            return hit == ListView || hit == DesktopView || hit == DesktopHost ||
                (ListView != IntPtr.Zero && NativeMethods.IsChild(ListView, hit)) ||
                (DesktopView != IntPtr.Zero && NativeMethods.IsChild(DesktopView, hit));
        }

        internal bool TryGetDesktopDrop(IntPtr owner, out NativeMethods.POINT point)
        {
            point = default;
            return NativeMethods.GetCursorPos(out point) && IsPointOverDesktop(owner, point);
        }
    }

    internal static Task<ShellDragSession> PrepareAsync(
        string path,
        double grabRatioX = .5,
        double grabRatioY = .5,
        bool preferMove = false,
        CancellationToken cancellationToken = default,
        bool suppressSystemDragImage = false) =>
        PrepareAsync([path], grabRatioX, grabRatioY, preferMove, cancellationToken, suppressSystemDragImage);

    internal static async Task<ShellDragSession> PrepareAsync(
        IReadOnlyList<string> paths,
        double grabRatioX = .5,
        double grabRatioY = .5,
        bool preferMove = false,
        CancellationToken cancellationToken = default,
        bool suppressSystemDragImage = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) throw new FileNotFoundException(AppStrings.Get("ShellDragMissing"));
        string primaryPath = paths[0];
        long started = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        ShellDataObject data = CreateDataObject(paths);
        IntPtr bitmap = IntPtr.Zero;
        bool dragImageInitialized = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preferMove)
            {
                object dataObject = Marshal.GetObjectForIUnknown(data.Pointer);
                try { SetPreferredDropEffect((IDataObject)dataObject, DropEffectMove); }
                finally { if (Marshal.IsComObject(dataObject)) _ = Marshal.ReleaseComObject(dataObject); }
            }

            if (!suppressSystemDragImage)
            {
                try
                {
                    Task<IntPtr> bitmapTask = Task.Run(
                        () => IconCacheService.CreateDragBitmap(primaryPath, DragImageSize));
                    if (cancellationToken.CanBeCanceled)
                    {
                        Task completed = await Task.WhenAny(
                            bitmapTask,
                            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
                        if (!ReferenceEquals(completed, bitmapTask))
                        {
                            _ = bitmapTask.ContinueWith(
                                task =>
                                {
                                    if (task.Status == TaskStatus.RanToCompletion && task.Result != IntPtr.Zero)
                                        _ = NativeMethods.DeleteObject(task.Result);
                                    else if (task.IsFaulted)
                                        _ = task.Exception;
                                },
                                CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                    bitmap = await bitmapTask;
                    cancellationToken.ThrowIfCancellationRequested();
                    var grabOffset = new ShellDragPoint
                    {
                        X = Math.Clamp((int)Math.Round(grabRatioX * DragImageSize), 0, DragImageSize - 1),
                        Y = Math.Clamp((int)Math.Round(grabRatioY * DragImageSize), 0, DragImageSize - 1)
                    };
                    dragImageInitialized = TryInitializeDragImage(data.Pointer, bitmap, grabOffset);
                    if (dragImageInitialized)
                    {
                        // IDragSourceHelper::InitializeFromBitmap transfers HBITMAP ownership to IDataObject on S_OK.
                        bitmap = IntPtr.Zero;
                    }
                    else if (bitmap != IntPtr.Zero)
                    {
                        _ = NativeMethods.DeleteObject(bitmap);
                        bitmap = IntPtr.Zero;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("无法准备自定义Shell拖动图像，将继续使用系统默认反馈。", ex);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ShellDragSession(primaryPath, data, bitmap, dragImageInitialized, Stopwatch.GetElapsedTime(started));
        }
        catch
        {
            if (bitmap != IntPtr.Zero) _ = NativeMethods.DeleteObject(bitmap);
            data.Dispose();
            throw;
        }
    }

    internal sealed class ShellDataObject : IDisposable
    {
        private object? _ownerComObject;

        internal ShellDataObject(object ownerComObject, IntPtr pointer)
        {
            _ownerComObject = ownerComObject;
            Pointer = pointer;
        }

        internal IntPtr Pointer { get; private set; }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                _ = Marshal.Release(Pointer);
                Pointer = IntPtr.Zero;
            }
            if (_ownerComObject is not null)
            {
                if (Marshal.IsComObject(_ownerComObject)) _ = Marshal.FinalReleaseComObject(_ownerComObject);
                _ownerComObject = null;
            }
        }
    }

    internal sealed class ShellDragSession : IDisposable
    {
        private IntPtr _bitmap;

        internal ShellDragSession(string path, ShellDataObject data, IntPtr bitmap, bool dragImageInitialized, TimeSpan preparationDuration)
        {
            Path = path;
            Data = data;
            _bitmap = bitmap;
            DragImageInitialized = dragImageInitialized;
            PreparationDuration = preparationDuration;
        }

        internal string Path { get; }
        internal ShellDataObject Data { get; }
        internal TimeSpan PreparationDuration { get; }
        internal bool HasCustomDragImage => DragImageInitialized;
        internal bool DragImageInitialized { get; }
        internal TimeSpan? FirstFeedbackDelay { get; set; }
        internal int CallbackCount { get; set; }
        internal TimeSpan MaximumCallbackInterval { get; set; }
        internal TimeSpan DragDuration { get; set; }
        internal bool IsDisposed { get; private set; }

        internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            Data.Dispose();
            if (_bitmap != IntPtr.Zero)
            {
                _ = NativeMethods.DeleteObject(_bitmap);
                _bitmap = IntPtr.Zero;
            }
        }
    }
}
