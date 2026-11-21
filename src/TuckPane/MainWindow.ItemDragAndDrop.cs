using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TuckPane.Core;
using TuckPane.Controls;
using TuckPane.Models;
using TuckPane.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;

namespace TuckPane;

public sealed partial class MainWindow
{
    private void Item_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border host) BeginItemPointerPress(host, e, host);
    }

    private void BeginItemPointerPress(Border host, PointerRoutedEventArgs e, UIElement captureOwner)
    {
        if (e.Handled) return;
        CloseDockToolTip();
        if (_itemReorderProbeRunning || !_expanded || _animating || _shellDragActive || _shellDropFinalizing || _itemReorderSession is not null ||
            host.Tag is not string relativeName)
        {
            if (_itemDragTraceEnabled)
                AppLogger.Info($"换序跟踪：忽略按下 probe={_itemReorderProbeRunning} expanded={_expanded} animating={_animating} shell={_shellDragActive} finalizing={_shellDropFinalizing} session={_itemReorderSession is not null}。");
            return;
        }
        PointerPoint point = e.GetCurrentPoint(ItemsRepeater);
        if (!point.Properties.IsLeftButtonPressed) return;
        int sourceIndex = IndexOfItem(relativeName);
        if (sourceIndex < 0) return;
        if (IsCompactList && e.Pointer.PointerDeviceType == PointerDeviceType.Mouse) BeginCompactClick(host, e);
        SetItemInteractionVisual(host, pressed: true);
        if (_itemDragTraceEnabled) AppLogger.Info($"换序跟踪：按下 source={sourceIndex} pointer={e.Pointer.PointerId}。");

        NormalizeItemMotionsToLayout("NewPress");
        Point hostOrigin = host.TransformToVisual(ItemsRepeater).TransformPoint(new Point());
        _hoverDragOriginOffset = _hoverPressOffset is Point grab
            ? new Vector3((float)(point.Position.X - hostOrigin.X - grab.X), (float)(point.Position.Y - hostOrigin.Y - grab.Y), 0)
            : Vector3.Zero;
        _itemReorderSession = new ItemReorderSession(
            relativeName,
            sourceIndex,
            point.Position,
            _hoverPressOffset ?? new Point(point.Position.X - hostOrigin.X, point.Position.Y - hostOrigin.Y));
        _itemDragPressedAt = Stopwatch.GetTimestamp();
        _itemDragHost = host;
        _itemDragPointerId = e.Pointer.PointerId;
        _itemDragPointerType = e.Pointer.PointerDeviceType;
        _itemDragLastPointerPoint = e.GetCurrentPoint(host);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
            UpdateCompactRowHighlight(e.GetCurrentPoint(ItemsScrollView).Position);
        // Keep native gesture recognition on the element that received the press.
        // Logical file identity still belongs to host; a drag transfers capture later.
        _itemPointerCaptureOwner = captureOwner;
        _itemCapturedPointer = e.Pointer;
        if (!captureOwner.CapturePointer(e.Pointer))
        {
            CancelItemReorder();
            return;
        }
        if (_itemDragPointerType != PointerDeviceType.Mouse)
        {
            _itemTouchHoldTimer.Stop();
            _itemTouchHoldTimer.Start();
        }
    }

    private void Item_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_itemReorderSession is not { } session || e.Pointer.PointerId != _itemDragPointerId) return;
        if (sender is Border host) _itemDragLastPointerPoint = e.GetCurrentPoint(host);
        Point pointer = _itemDragPointerType == PointerDeviceType.Mouse && TryGetMousePointerContent(out Point mousePointer)
            ? mousePointer
            : e.GetCurrentPoint(ItemsRepeater).Position;
        session.Track(pointer);
        if (!session.IsActive)
        {
            if (HasActiveFilter)
            {
                double dx = pointer.X - session.PressPointerContent.X;
                double dy = pointer.Y - session.PressPointerContent.Y;
                if (dx * dx + dy * dy >= ItemReorderSession.ActivationThresholdDip * ItemReorderSession.ActivationThresholdDip)
                {
                    CancelItemReorder();
                    ShowExpandedToast(
                        LocalizeToast("筛选状态下暂不支持拖动排序，请先清除筛选", "Clear filter before reordering items", "フィルター中は並び替えできません。先にフィルターを解除してください"),
                        InfoBarSeverity.Informational);
                }
                return;
            }
            if (_itemDragPointerType == PointerDeviceType.Mouse)
            {
                if (!session.TryActivate(pointer)) return;
                StartItemReorder();
                e.Handled = true;
                return;
            }
            else
            {
                double x = pointer.X - session.PressPointerContent.X;
                double y = pointer.Y - session.PressPointerContent.Y;
                if (x * x + y * y >= ItemReorderSession.ActivationThresholdDip * ItemReorderSession.ActivationThresholdDip)
                {
                    CancelItemReorder();
                }
                return;
            }
        }
        e.Handled = true;
    }

    private async void Item_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        await RunSafelyAsync(() => HandleItemPointerReleasedAsync(sender, e), "项目拖动释放处理失败");
    }

    private async Task HandleItemPointerReleasedAsync(object sender, PointerRoutedEventArgs e)
    {
        if (_itemReorderSession is not { } session || e.Pointer.PointerId != _itemDragPointerId) return;
        if (sender is Border releasedHost)
        {
            Point position = e.GetCurrentPoint(releasedHost).Position;
            bool pointerInside = position.X >= 0 && position.Y >= 0 &&
                position.X <= releasedHost.ActualWidth && position.Y <= releasedHost.ActualHeight;
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && pointerInside)
                SetItemInteractionVisual(releasedHost, pressed: false);
            else ResetItemInteractionVisual(releasedHost);
        }
        if (_itemDragTraceEnabled)
            AppLogger.Info($"换序跟踪：释放 source={session.SourceIndex} target={session.TargetIndex} state={session.State}。");
        if (IsCompactList && e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && session.State == ItemDragState.Pressed)
        {
            if (TryHandleItemSelectionClick(session.RelativeName))
            {
                CancelItemReorder(preserveHover: _hoverWave.HasPointer);
                FocusExpandedItemsForKeyboardInput();
                e.Handled = true;
                return;
            }
            string clickedName = session.RelativeName;
            _itemSelection.SelectSingle(clickedName, IndexOfItemInVisualOrder(clickedName));
            RefreshItemSelectionVisuals();
            SyncKeyboardFocusToItem(clickedName);
            FocusExpandedItemsForKeyboardInput();
            await CompleteCompactClickAsync(e);
            return;
        }
        if (session.IsActive && !session.IsNativeDragging)
        {
            e.Handled = true;
            await CommitItemReorderAsync(session);
        }
        else if (!session.IsNativeDragging)
        {
            WidgetItem? item = _items.FirstOrDefault(candidate =>
                candidate.RelativeName.Equals(session.RelativeName, StringComparison.OrdinalIgnoreCase));
            bool wasPressedClick = session.State == ItemDragState.Pressed;
            CancelItemReorder(preserveHover: UseHoverPresentation && wasPressedClick && _hoverWave.HasPointer);
            if (wasPressedClick && TryHandleItemSelectionClick(session.RelativeName))
            {
                FocusExpandedItemsForKeyboardInput();
                e.Handled = true;
                return;
            }
            if (wasPressedClick && !IsDock && item is not null)
            {
                _itemSelection.SelectSingle(item.RelativeName, IndexOfItemInVisualOrder(item.RelativeName));
                RefreshItemSelectionVisuals();
                SyncKeyboardFocusToItem(item.RelativeName);
            }
            if (item is { Kind: WidgetItemKind.Organizer, OrganizerId: Guid } && sender is FrameworkElement host)
            {
                await OpenTaggedItemAsync(host);
                e.Handled = true;
            }
            else if (!IsCompactList && item is { Kind: WidgetItemKind.Note, NoteId: Guid })
            {
                await OpenItemAsync(item, doubleTap: false);
                e.Handled = true;
            }
            else if (!IsCompactList && item?.Kind == WidgetItemKind.PortableNote)
            {
                await OpenItemAsync(item, doubleTap: false);
                e.Handled = true;
            }
            else if (!IsCompactList && item?.Kind == WidgetItemKind.PortableTodo)
            {
                await OpenItemAsync(item, doubleTap: false);
                e.Handled = true;
            }
            else if (wasPressedClick && !IsDock)
            {
                FocusExpandedItemsForKeyboardInput();
            }
        }
        else e.Handled = true;
    }

    private void Item_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _compactClicks.Reset();
        if (sender is Border host) ResetItemInteractionVisual(host);
        if (_itemDragTraceEnabled) AppLogger.Info("换序跟踪：指针取消。");
        if (_itemReorderSession is null || _itemDragPointerId == 0 || e.Pointer.PointerId != _itemDragPointerId) return;
        if (_itemDragLanding || _shellPromotionPending || _shellDragActive) return;
        CancelItemReorder();
    }

    private void Item_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border host) ResetItemInteractionVisual(host);
        if (_itemDragTraceEnabled) AppLogger.Info($"换序跟踪：捕获丢失 pointer={_itemDragPointerId}。");
        if (_itemDragPointerId != 0) CancelItemReorder();
    }

    private void ItemTouchHoldTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (HasActiveFilter)
        {
            CancelItemReorder();
            return;
        }
        if (_itemReorderSession is not { IsActive: false } session || _itemDragHost is null) return;
        session.Activate(session.LatestPointerContent);
        StartItemReorder();
    }

    private void StartItemReorder()
    {
        if (HasActiveFilter)
        {
            CancelItemReorder();
            return;
        }
        if (_itemReorderSession is not { IsActive: true } session || _itemDragHost is null) return;
        _compactClicks.Reset();
        if (_itemCapturedPointer is { } pointer && _itemPointerCaptureOwner is { } previousOwner &&
            !ReferenceEquals(previousOwner, _itemDragHost))
        {
            // Set the new owner before transfer: the old proxy's capture-lost event
            // must not cancel the drag that is taking over from the press.
            _itemPointerCaptureOwner = _itemDragHost;
            previousOwner.ReleasePointerCaptures();
            if (!_itemDragHost.CapturePointer(pointer))
            {
                CancelItemReorder();
                return;
            }
        }
        ResetHoverWave();
        _externalDragCoordinator.BeginNoteDragPreparation(session, _itemDragPointerId);
        _itemTouchHoldTimer.Stop();
        ResetGapTransitionState();
        _itemReorderUsesAnimations = UseCustomAnimations;
        _gapTransitionRevision = session.PreviewRevision;
        _gapMaterializedRevision = session.PreviewRevision;
        _itemDragIdentityWarnings.Clear();
        _itemDragLanding = false;
        ResetItemContentTransform(_itemDragHost);
        _itemDragHost.Background = _draggedItemBrush;
        Canvas.SetZIndex(_itemDragHost, 1000);
        _itemDragHost.CenterPoint = new Vector3((float)(_itemDragHost.ActualWidth / 2), (float)(_itemDragHost.ActualHeight / 2), 0);
        ItemMotionState draggedMotion = GetItemMotion(_itemDragHost);
        draggedMotion.ScaleTarget = _itemReorderUsesAnimations ? new Vector3(1.06f, 1.06f, 1) : Vector3.One;
        draggedMotion.Scale = draggedMotion.ScaleTarget;
        draggedMotion.ScaleVelocity = Vector3.Zero;
        _itemDragHost.Scale = draggedMotion.Scale;
        _itemDragLastFrame = Stopwatch.GetTimestamp();
        if (!_itemDragRenderingSubscribed)
        {
            CompositionTarget.Rendering += ItemDragRendering;
            _itemDragRenderingSubscribed = true;
        }
        TrySetItemDragClockBoost(true);
        if (IsDock) UpdateDockInputRegion();
        if (_itemDragPointerType == PointerDeviceType.Mouse) StartItemDragBoundaryHook();
        BeginIosFluidReorder(session);
        if (_freeGridEngine is null)
            ApplyAllProvisionalItemVisuals(animate: false);
        if (_hoverDragOriginOffset != Vector3.Zero)
        {
            Vector3 position = _hoverDragOriginOffset + new Vector3(
                (float)(session.LatestPointerContent.X - session.PressPointerContent.X),
                (float)(session.LatestPointerContent.Y - session.PressPointerContent.Y), 0);
            draggedMotion.Translation = draggedMotion.TranslationTarget = position;
            _itemDragHost.Translation = position;
        }
    }

    private void ItemDragRendering(object? sender, object args)
    {
        if (_itemReorderSession is not { IsActive: true } session || _itemDragHost is null) return;

        long now = Stopwatch.GetTimestamp();
        double seconds = _itemDragLastFrame == 0 ? 0 : Math.Clamp(Stopwatch.GetElapsedTime(_itemDragLastFrame, now).TotalSeconds, 0, 1d / 20);
        _itemDragLastFrame = now;
        if (_gapTransitionCompleting)
        {
            if (!StepGapTransition(session, now)) _gapTransitionSettled?.TrySetResult();
            return;
        }
        if (_itemDragLanding)
        {
            bool settled = _freeGridEngine is not null
                ? StepIosFluidItemSprings(seconds, directDragHost: null)
                : StepItemMotions(seconds, directDragHost: null);
            if (settled) _itemMotionSettled?.TrySetResult();
            return;
        }

        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        Point viewportOrigin = ItemsScrollView.TransformToVisual(WindowRoot).TransformPoint(new Point());
        Thickness repeaterMargin = ItemsRepeater.Margin;
        Thickness scrollPadding = ItemsScrollView.Padding;
        double contentOffsetX = repeaterMargin.Left + scrollPadding.Left;
        double contentOffsetY = repeaterMargin.Top + scrollPadding.Top;
        Point pointerInWindow;
        if (_itemDragPointerType == PointerDeviceType.Mouse)
        {
            if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)) return;
            if (!DragBoundaryMath.Contains(_itemDragBoundaryBounds, cursor))
            {
                TryPostItemExternalDragPromotion();
                return;
            }
            if (!NativeMethods.ScreenToClient(_hwnd, ref cursor)) return;
            pointerInWindow = new(cursor.X / scale, cursor.Y / scale);
        }
        else
        {
            pointerInWindow = new(
                viewportOrigin.X + contentOffsetX + session.LatestPointerContent.X - ItemsScrollView.HorizontalOffset,
                viewportOrigin.Y + contentOffsetY + session.LatestPointerContent.Y - ItemsScrollView.VerticalOffset);
        }
        Point pointerInViewport = new(pointerInWindow.X - viewportOrigin.X, pointerInWindow.Y - viewportOrigin.Y);
        ApplyItemDragAutoScroll(pointerInViewport.Y, seconds);
        Point pointerContent = new(
            pointerInViewport.X + ItemsScrollView.HorizontalOffset - contentOffsetX,
            pointerInViewport.Y + ItemsScrollView.VerticalOffset - contentOffsetY);
        if (_freeGridEngine is not null)
        {
            UpdateIosFluidReorderFrame(session, pointerContent, seconds);
            return;
        }
        session.UpdateTarget(
            pointerContent,
            Math.Max(1, _itemDragHost.Width),
            Math.Max(1, _itemDragHost.Height),
            GetItemLayoutGapDip(),
            GetItemLayoutColumnCount(),
            _items.Count);
        bool transitionRunning = StepGapTransition(session, now);
        if (!transitionRunning) EnsureGapPreviewMaterialized(session);

        ItemMotionState draggedMotion = GetItemMotion(_itemDragHost);
        Vector3 nextTranslation = new(
            (float)(pointerContent.X - session.PressPointerContent.X) + _hoverDragOriginOffset.X,
            (float)(pointerContent.Y - session.PressPointerContent.Y) + _hoverDragOriginOffset.Y,
            16);
        if (seconds > 0) draggedMotion.TranslationVelocity = (nextTranslation - draggedMotion.Translation) / (float)seconds;
        draggedMotion.Translation = nextTranslation;
        draggedMotion.TranslationTarget = nextTranslation;
        _itemDragHost.Translation = nextTranslation;
    }

    private bool TryGetMousePointerContent(out Point pointerContent)
    {
        pointerContent = default;
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor) ||
            !NativeMethods.ScreenToClient(_hwnd, ref cursor)) return false;

        double scale = Math.Max(1, NativeMethods.GetDpiForWindow(_hwnd) / 96d);
        Point repeaterOrigin = ItemsRepeater.TransformToVisual(WindowRoot).TransformPoint(new Point());
        pointerContent = new Point(
            cursor.X / scale - repeaterOrigin.X,
            cursor.Y / scale - repeaterOrigin.Y);
        return true;
    }

    private void StartItemDragBoundaryHook()
    {
        StopItemDragBoundaryHook();
        if (!TryGetLogicalWindowRect(out NativeMethods.RECT window)) return;
        _itemDragBoundaryBounds = window;
        DragMessageRelay.Reset(ref _itemDragOleMouseMovePending, ref _itemDragLastOleMouseMoveForwardedAt);
        Volatile.Write(ref _itemDragBoundaryPromotionPosted, 0);
        Interlocked.Exchange(ref _itemDragBoundaryDetectedAt, 0);

        Volatile.Write(ref _itemDragBoundaryArmed, 1);
        if (!EnsureItemDragBoundaryHook())
        {
            AppLogger.Info("项目外拖钩子不可用，将使用渲染循环边界检测。");
            return;
        }
        if (_itemDragTraceEnabled)
            AppLogger.Info($"换序跟踪：边界钩子 hwnd=0x{_itemDragBoundaryHook.ToInt64():X} " +
                $"bounds={_itemDragBoundaryBounds.Left},{_itemDragBoundaryBounds.Top},{_itemDragBoundaryBounds.Right},{_itemDragBoundaryBounds.Bottom}。");
    }

    private bool EnsureItemDragBoundaryHook()
    {
        Thread? existingThread = _itemDragBoundaryHookThread;
        if (existingThread is not null && existingThread.IsAlive && _itemDragBoundaryHook != IntPtr.Zero) return true;
        if (existingThread is not null)
        {
            ShutdownItemDragBoundaryHook();
            if (_itemDragBoundaryHookThread is not null)
            {
                AppLogger.Error("上一个项目外拖边界钩子仍在退出，本次不再重复安装。");
                return false;
            }
        }

        _itemDragBoundaryHookReady.Reset();
        Volatile.Write(ref _itemDragBoundaryHookStopRequested, 0);
        _itemDragBoundaryHookThread = new Thread(ItemDragBoundaryHookThreadMain)
        {
            IsBackground = true,
            Name = "TuckPane.ItemDragBoundary"
        };
        _itemDragBoundaryHookThread.Start();
        AppLogger.Performance("item-drag-hook install queued");
        return false;
    }

    private void ItemDragBoundaryHookThreadMain()
    {
        try
        {
            // Force Win32 thread message queue creation BEFORE publishing the thread ID so PostThreadMessage(WM_QUIT) can never race!
            _ = NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            Volatile.Write(ref _itemDragBoundaryHookThreadId, NativeMethods.GetCurrentThreadId());
            if (Volatile.Read(ref _itemDragBoundaryHookStopRequested) != 0) return;
            _itemDragBoundaryHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                _itemDragBoundaryHookProc,
                NativeMethods.GetModuleHandle(null),
                0);
            try { _itemDragBoundaryHookReady.Set(); }
            catch (ObjectDisposedException) { }
            if (_itemDragBoundaryHook == IntPtr.Zero ||
                Volatile.Read(ref _itemDragBoundaryHookStopRequested) != 0) return;
            while (Volatile.Read(ref _itemDragBoundaryHookStopRequested) == 0 &&
                   NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        }
        finally
        {
            IntPtr hook = Interlocked.Exchange(ref _itemDragBoundaryHook, IntPtr.Zero);
            if (hook != IntPtr.Zero) _ = NativeMethods.UnhookWindowsHookEx(hook);
            Volatile.Write(ref _itemDragBoundaryHookThreadId, 0);
            Interlocked.CompareExchange(ref _itemDragBoundaryHookThread, null, Thread.CurrentThread);
            try { _itemDragBoundaryHookReady.Set(); }
            catch (ObjectDisposedException) { }
        }
    }

    private IntPtr ItemDragBoundaryHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            int mouseMessage = wParam.ToInt32();
            NativeMethods.MSLLHOOKSTRUCT? hookData = code >= 0 &&
                mouseMessage is NativeMethods.WM_MOUSEMOVE or NativeMethods.WM_LBUTTONUP
                    ? Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam)
                    : null;
            if (code >= 0 && mouseMessage == NativeMethods.WM_MOUSEMOVE &&
                Volatile.Read(ref _itemDragBoundaryArmed) != 0 &&
                hookData is { } data &&
                (data.Point.X < _itemDragBoundaryBounds.Left || data.Point.X >= _itemDragBoundaryBounds.Right ||
                 data.Point.Y < _itemDragBoundaryBounds.Top || data.Point.Y >= _itemDragBoundaryBounds.Bottom))
            {
                TryPostItemExternalDragPromotion();
            }

            bool shouldForwardToOle = mouseMessage == NativeMethods.WM_LBUTTONUP ||
                mouseMessage == NativeMethods.WM_MOUSEMOVE && Volatile.Read(ref _shellDragActive) && ShouldForwardOleMouseMove();
            if (code >= 0 && Volatile.Read(ref _itemDragBoundaryPromotionPosted) != 0 && shouldForwardToOle &&
                hookData is { } dataToForward)
            {
                NativeMethods.POINT clientPoint = dataToForward.Point;
                if (!NativeMethods.ScreenToClient(_hwnd, ref clientPoint))
                {
                    if (mouseMessage == NativeMethods.WM_MOUSEMOVE)
                        DragMessageRelay.Complete(ref _itemDragOleMouseMovePending);
                }
                else
                {
                    uint keyState = mouseMessage == NativeMethods.WM_MOUSEMOVE
                        ? GetForwardedMouseKeyState()
                        : 0u;
                    bool posted = NativeMethods.PostMessage(
                        _hwnd,
                        (uint)mouseMessage,
                        new UIntPtr(keyState),
                        DragMessageRelay.PackClientPosition(clientPoint));
                    if (!posted && mouseMessage == NativeMethods.WM_MOUSEMOVE)
                        DragMessageRelay.Complete(ref _itemDragOleMouseMovePending);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("项目外拖边界钩子处理失败。", ex);
            DragMessageRelay.Complete(ref _itemDragOleMouseMovePending);
        }
        return NativeMethods.CallNextHookEx(_itemDragBoundaryHook, code, wParam, lParam);
    }

    private static uint GetForwardedMouseKeyState()
    {
        const int VkRButton = 0x02;
        const int VkMButton = 0x04;
        const int VkShift = 0x10;
        uint state = NativeMethods.MK_LBUTTON;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0) state |= 0x0008;
        if ((NativeMethods.GetAsyncKeyState(VkShift) & 0x8000) != 0) state |= 0x0004;
        if ((NativeMethods.GetAsyncKeyState(VkRButton) & 0x8000) != 0) state |= 0x0002;
        if ((NativeMethods.GetAsyncKeyState(VkMButton) & 0x8000) != 0) state |= 0x0010;
        return state;
    }

    private void TryPostItemExternalDragPromotion()
    {
        if (Volatile.Read(ref _itemDragBoundaryArmed) == 0 ||
            Interlocked.Exchange(ref _itemDragBoundaryPromotionPosted, 1) != 0) return;
        if (_itemDragTraceEnabled) AppLogger.Info("换序跟踪：命中画布边界。");
        Interlocked.Exchange(ref _itemDragBoundaryDetectedAt, Stopwatch.GetTimestamp());
        if (NativeMethods.PostMessage(
                _hwnd,
                NativeMethods.WM_APP_START_ITEM_EXTERNAL_DRAG,
                UIntPtr.Zero,
                IntPtr.Zero)) return;
        Volatile.Write(ref _itemDragBoundaryPromotionPosted, 0);
        AppLogger.Error("无法投递项目外拖启动消息。");
    }

    private bool ShouldForwardOleMouseMove()
    {
        return DragMessageRelay.TryReserve(
            ref _itemDragOleMouseMovePending,
            ref _itemDragLastOleMouseMoveForwardedAt,
            Stopwatch.GetTimestamp(),
            OleMouseMoveMinimumTicks);
    }

    private void StopItemDragBoundaryHook()
    {
        Volatile.Write(ref _itemDragBoundaryArmed, 0);
        Volatile.Write(ref _itemDragBoundaryPromotionPosted, 0);
        DragMessageRelay.Reset(ref _itemDragOleMouseMovePending, ref _itemDragLastOleMouseMoveForwardedAt);
    }

    private void ShutdownItemDragBoundaryHook()
    {
        StopItemDragBoundaryHook();
        Volatile.Write(ref _itemDragBoundaryHookStopRequested, 1);
        Thread? hookThread = _itemDragBoundaryHookThread;
        if (hookThread is null) return;

        uint threadId = Volatile.Read(ref _itemDragBoundaryHookThreadId);
        if (threadId != 0 && !NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero))
            AppLogger.Error("无法通知项目外拖边界钩子线程退出。");

        if (hookThread == Thread.CurrentThread)
        {
            AppLogger.Error("项目外拖边界钩子线程不能等待自身退出。");
            return;
        }
        // Do not synchronously Join from the UI thread. The hook thread owns
        // its message loop and will unhook itself after processing WM_QUIT.
        AppLogger.Performance("item-drag-hook shutdown queued");
    }

    private void ApplyItemDragAutoScroll(double pointerY, double seconds)
    {
        if (ItemsScrollView.ScrollableHeight <= .5) return;
        const double edge = 48;
        double penetration = pointerY < edge
            ? Math.Clamp((edge - pointerY) / edge, 0, 1)
            : pointerY > ItemsScrollView.ActualHeight - edge
                ? Math.Clamp((pointerY - (ItemsScrollView.ActualHeight - edge)) / edge, 0, 1)
                : 0;
        if (penetration <= 0 || seconds <= 0) return;
        double direction = pointerY < edge ? -1 : 1;
        if (direction < 0 && ItemsScrollView.VerticalOffset <= .5 ||
            direction > 0 && ItemsScrollView.VerticalOffset >= ItemsScrollView.ScrollableHeight - .5) return;
        double speed = 120 + 600 * penetration * penetration;
        ItemsScrollView.ScrollBy(0, direction * speed * seconds, InstantScrollOptions);
    }

    private void ApplyAllProvisionalItemVisuals(bool animate)
    {
        for (int index = 0; index < _items.Count; index++)
        {
            if (TryGetRealizedItemHost(index, out Border host)) ApplyProvisionalItemVisual(host, index, animate);
        }
    }

    private void ApplyProvisionalItemVisual(Border host, int index, bool animate)
    {
        if (_itemReorderSession is not { IsActive: true } session || index == session.SourceIndex) return;
        Vector2 target;
        if (_gapTransitionFrom is { } from && _gapTransitionTo is { } to &&
            index < from.Length && index < to.Length)
        {
            target = GridGapTransitionMath.GetTranslation(
                index,
                from[index],
                to[index],
                GetItemLayoutColumnCount(),
                Math.Max(1, host.Width),
                Math.Max(1, host.Height),
                GetItemLayoutGapDip(),
                GridGapTransitionMath.GetProgress(Stopwatch.GetElapsedTime(_gapTransitionStartedAt)));
        }
        else
        {
            target = session.GetSlotTranslation(
                index,
                GetItemLayoutColumnCount(),
                Math.Max(1, host.Width),
                Math.Max(1, host.Height),
                GetItemLayoutGapDip());
        }
        Vector3 translation = new(target, 0);
        ItemMotionState motion = GetItemMotion(host);
        motion.Translation = translation;
        motion.TranslationTarget = translation;
        motion.TranslationVelocity = Vector3.Zero;
        motion.Scale = Vector3.One;
        motion.ScaleTarget = Vector3.One;
        motion.ScaleVelocity = Vector3.Zero;
        host.Translation = translation;
        host.Scale = Vector3.One;
    }

    private bool StartGapTransitionIfReady(ItemReorderSession session, long now)
    {
        if (_gapTransitionFrom is not null) return true;
        if (!session.TryBeginPreviewTransition(_items.Count, out int[] from, out int[] to)) return false;
        _gapTransitionFrom = from;
        _gapTransitionTo = to;
        _gapTransitionStartedAt = now;
        _gapTransitionRevision = session.PreviewRevision;
        if (_itemDragTraceEnabled)
            AppLogger.Info($"换序跟踪：预览开始 revision={_gapTransitionRevision} target={session.TargetIndex}。");
        if (_itemReorderUsesAnimations) return true;
        ApplyGapTransitionFrame(1);
        _gapMaterializedRevision = _gapTransitionRevision;
        _gapTransitionFrom = null;
        _gapTransitionTo = null;
        _gapTransitionStartedAt = 0;
        return false;
    }

    private bool StepGapTransition(ItemReorderSession session, long now)
    {
        if (_gapTransitionFrom is null && !StartGapTransitionIfReady(session, now)) return false;
        double progress = GridGapTransitionMath.GetProgress(Stopwatch.GetElapsedTime(_gapTransitionStartedAt, now));
        ApplyGapTransitionFrame(progress);
        if (progress < 1) return true;

        _gapMaterializedRevision = _gapTransitionRevision;
        if (_itemDragTraceEnabled)
            AppLogger.Info($"换序跟踪：预览完成 revision={_gapMaterializedRevision} target={session.TargetIndex}。");
        _gapTransitionFrom = null;
        _gapTransitionTo = null;
        _gapTransitionStartedAt = 0;
        return StartGapTransitionIfReady(session, now);
    }

    private void EnsureGapPreviewMaterialized(ItemReorderSession session)
    {
        if (_gapTransitionFrom is not null || _gapMaterializedRevision == session.PreviewRevision) return;
        ApplyAllProvisionalItemVisuals(animate: false);
        _gapMaterializedRevision = session.PreviewRevision;
        if (_itemDragTraceEnabled)
            AppLogger.Info($"换序跟踪：预览收敛 revision={_gapMaterializedRevision} target={session.TargetIndex}。");
    }

    private void ApplyGapTransitionFrame(double progress)
    {
        if (_itemReorderSession is not { } session || _gapTransitionFrom is not { } from || _gapTransitionTo is not { } to) return;
        int columns = GetItemLayoutColumnCount();
        for (int index = 0; index < _items.Count && index < from.Length && index < to.Length; index++)
        {
            if (index == session.SourceIndex || !TryGetRealizedItemHost(index, out Border host)) continue;
            Vector2 value = GridGapTransitionMath.GetTranslation(
                index,
                from[index],
                to[index],
                columns,
                Math.Max(1, host.Width),
                Math.Max(1, host.Height),
                GetItemLayoutGapDip(),
                progress);
            ItemMotionState motion = GetItemMotion(host);
            motion.Translation = new Vector3(value, 0);
            motion.TranslationTarget = motion.Translation;
            motion.TranslationVelocity = Vector3.Zero;
            motion.Scale = Vector3.One;
            motion.ScaleTarget = Vector3.One;
            motion.ScaleVelocity = Vector3.Zero;
            host.Translation = motion.Translation;
            host.Scale = Vector3.One;
        }
    }

    private ItemMotionState GetItemMotion(Border host)
    {
        if (_itemMotionStates.TryGetValue(host, out ItemMotionState? motion)) return motion;
        // Never touch Composition ElementVisual on item hosts: item hosts use WinUI UIElement
        // Translation / Scale / Rotation / CenterPoint, and mixing them throws 0x80070005 (E_ACCESSDENIED).
        motion = new ItemMotionState
        {
            Translation = host.Translation,
            TranslationTarget = host.Translation,
            Scale = host.Scale,
            ScaleTarget = host.Scale
        };
        _itemMotionStates.Add(host, motion);
        return motion;
    }

    private void ResetItemMotion(Border host)
    {
        ItemMotionState motion = GetItemMotion(host);
        motion.Translation = Vector3.Zero;
        motion.TranslationTarget = Vector3.Zero;
        motion.TranslationVelocity = Vector3.Zero;
        motion.Scale = Vector3.One;
        motion.ScaleTarget = Vector3.One;
        motion.ScaleVelocity = Vector3.Zero;
        host.Translation = Vector3.Zero;
        host.Scale = Vector3.One;
    }

    private bool StepItemMotions(double seconds, Border? directDragHost)
    {
        if (!_itemReorderUsesAnimations)
        {
            foreach ((Border host, ItemMotionState motion) in _itemMotionStates)
            {
                if (ReferenceEquals(host, directDragHost)) continue;
                motion.Translation = motion.TranslationTarget;
                motion.TranslationVelocity = Vector3.Zero;
                motion.Scale = motion.ScaleTarget;
                motion.ScaleVelocity = Vector3.Zero;
                host.Translation = motion.Translation;
                host.Scale = motion.Scale;
            }
            return true;
        }

        bool settled = true;
        foreach ((Border host, ItemMotionState motion) in _itemMotionStates)
        {
            if (!IsFinite(motion.Translation) || !IsFinite(motion.TranslationTarget) || !IsFinite(motion.TranslationVelocity))
            {
                motion.Translation = Vector3.Zero;
                motion.TranslationTarget = Vector3.Zero;
                motion.TranslationVelocity = Vector3.Zero;
            }
            if (!IsFinite(motion.Scale) || !IsFinite(motion.ScaleTarget) || !IsFinite(motion.ScaleVelocity))
            {
                motion.Scale = Vector3.One;
                motion.ScaleTarget = Vector3.One;
                motion.ScaleVelocity = Vector3.Zero;
            }
            if (!ReferenceEquals(host, directDragHost))
            {
                settled &= ItemMotionMath.StepCriticalSpring(ref motion.Translation, ref motion.TranslationVelocity, motion.TranslationTarget, seconds);
                host.Translation = motion.Translation;
            }
            settled &= ItemMotionMath.StepCriticalSpring(ref motion.Scale, ref motion.ScaleVelocity, motion.ScaleTarget, seconds);
            host.Scale = motion.Scale;
        }
        return settled;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private async Task CompleteGapTransitionsAsync(ItemReorderSession session)
    {
        _gapTransitionCompleting = true;
        _gapTransitionSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long now = Stopwatch.GetTimestamp();
        if (!StepGapTransition(session, now))
        {
            EnsureGapPreviewMaterialized(session);
            _gapTransitionSettled.TrySetResult();
        }
        Task completed = await Task.WhenAny(_gapTransitionSettled.Task, Task.Delay(180));
        if (completed != _gapTransitionSettled.Task && ReferenceEquals(_itemReorderSession, session))
            CompleteGapTransitionsImmediately(session);
        _gapTransitionSettled = null;
        _gapTransitionCompleting = false;
    }

    private void CompleteGapTransitionsImmediately(ItemReorderSession session)
    {
        if (_gapTransitionFrom is not null)
        {
            ApplyGapTransitionFrame(1);
            _gapMaterializedRevision = _gapTransitionRevision;
            _gapTransitionFrom = null;
            _gapTransitionTo = null;
            _gapTransitionStartedAt = 0;
        }
        while (session.TryBeginPreviewTransition(_items.Count, out int[] from, out int[] to))
        {
            _gapTransitionFrom = from;
            _gapTransitionTo = to;
            _gapTransitionRevision = session.PreviewRevision;
            ApplyGapTransitionFrame(1);
            _gapMaterializedRevision = _gapTransitionRevision;
            _gapTransitionFrom = null;
            _gapTransitionTo = null;
        }
        _gapTransitionStartedAt = 0;
        EnsureGapPreviewMaterialized(session);
        _gapTransitionSettled?.TrySetResult();
    }

    private void ResetGapTransitionState()
    {
        _gapTransitionFrom = null;
        _gapTransitionTo = null;
        _gapTransitionStartedAt = 0;
        _gapTransitionRevision = 0;
        _gapMaterializedRevision = 0;
        _gapTransitionCompleting = false;
        _gapTransitionSettled?.TrySetCanceled();
        _gapTransitionSettled = null;
    }

    private async Task CommitItemReorderAsync(ItemReorderSession session, bool nativeDrop = false)
    {
        if (nativeDrop && ConsumePendingInternalOleFreeGridSlots() is { } oleFreeGridSlots)
        {
            await CommitInternalOleFreeGridReorderAsync(session, oleFreeGridSlots);
            return;
        }
        if (!nativeDrop && _freeGridEngine is not null)
        {
            await CommitIosFluidReorderAsync(session);
            return;
        }
        try
        {
        if (_itemDragTraceEnabled) AppLogger.Info($"换序跟踪：开始提交 {session.SourceIndex}->{session.TargetIndex}。");
        StopItemDragInput(keepRendering: true);
        if (!nativeDrop) await CompleteGapTransitionsAsync(session);
        if (!ReferenceEquals(_itemReorderSession, session)) return;
        int[] visualIndices = session.SealVisualIndices(_items.Count);
        _itemDragLanding = true;
        if (!nativeDrop && _itemDragHost is { } draggedHost)
        {
            Vector2 target = session.GetSlotTranslation(
                session.SourceIndex,
                GetItemLayoutColumnCount(),
                Math.Max(1, draggedHost.Width),
                Math.Max(1, draggedHost.Height),
                GetItemLayoutGapDip());
            if (_itemReorderUsesAnimations)
            {
                ItemMotionState motion = GetItemMotion(draggedHost);
                motion.TranslationTarget = new Vector3(target, 0);
                motion.ScaleTarget = Vector3.One;
                _itemMotionSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await Task.WhenAny(_itemMotionSettled.Task, Task.Delay(260));
            }
        }
        if (!ReferenceEquals(_itemReorderSession, session)) return;

        StopItemDragInput(keepRendering: false);
        session.BeginCommit();
        if (nativeDrop) MaterializeNativeSourceAtTarget(session);
        Dictionary<string, Point>? visualPositions = _itemReorderUsesAnimations ? CaptureRealizedItemPositions() : null;
        ResetItemDragVisuals();
        bool changed = false;
        for (int original = 0; original < visualIndices.Length; original++)
        {
            if (visualIndices[original] == original) continue;
            changed = true;
            break;
        }
        if (changed)
        {
            int preparedBeforeCommit = _itemElementsPrepared;
            int clearedBeforeCommit = _itemElementsCleared;
            WidgetItem[] originalItems = _items.ToArray();
            var desiredItems = new WidgetItem?[originalItems.Length];
            for (int original = 0; original < originalItems.Length; original++)
                desiredItems[visualIndices[original]] = originalItems[original];
            if (desiredItems.Any(item => item is null)) throw new InvalidOperationException("The gap preview is not a complete permutation.");
            WidgetItem[] desired = desiredItems.Select(item => item!).ToArray();
            List<string> previousOrder = _definition.ItemOrder.ToList();
            _definition.ItemOrder = desired.Select(item => item.RelativeName).ToList();
            try
            {
                await _host.SaveStateAsync();
            }
            catch
            {
                _definition.ItemOrder = previousOrder;
                throw;
            }
            var realizedSlots = new Border?[originalItems.Length];
            for (int slot = 0; slot < originalItems.Length; slot++)
            {
                if (TryGetRealizedItemHost(originalItems[slot].RelativeName, out Border host)) realizedSlots[slot] = host;
            }
            int[] affectedSlots = Enumerable.Range(0, visualIndices.Length)
                .Where(index => visualIndices[index] != index)
                .ToArray();
            bool canRotateRealizedContent = affectedSlots.All(slot => realizedSlots[slot]?.Child is not null);
            var originalContent = new UIElement?[originalItems.Length];
            var originalParts = new ItemHostParts?[originalItems.Length];

            _itemCollectionMoveInProgress = true;
            try
            {
                if (canRotateRealizedContent)
                {
                    foreach (int slot in affectedSlots)
                    {
                        Border sourceHost = realizedSlots[slot]!;
                        UnregisterHoverItem(sourceHost);
                        if (_itemHostParts.Remove(sourceHost, out ItemHostParts? movedParts))
                            originalParts[slot] = movedParts;
                        originalContent[slot] = sourceHost.Child;
                        sourceHost.Child = null;
                    }
                    foreach (int original in affectedSlots)
                    {
                        Border destination = realizedSlots[visualIndices[original]]!;
                        destination.Child = originalContent[original];
                        if (originalParts[original] is { } movedParts)
                            _itemHostParts[destination] = movedParts;
                        RegisterHoverItem(destination);
                    }
                }
                foreach (Border? host in realizedSlots)
                {
                    if (host is not null) _realizedItemHosts.Unregister(host);
                }
                _ = CatalogCollectionSync.ApplyReorderInPlace(_items, desired);
                for (int slot = 0; slot < realizedSlots.Length; slot++)
                {
                    if (realizedSlots[slot] is not { } host) continue;
                    WidgetItem item = _items[slot];
                    _realizedItemHosts.Register(item.RelativeName, host);
                    host.Tag = item.RelativeName;
                    SetItemToolTip(host, IsDock || IsDocumentItem(item) || item.Kind == WidgetItemKind.Organizer ? item.Name : item.FullPath);
                    if (!canRotateRealizedContent && affectedSlots.Contains(slot)) PrepareReorderedItemIdentity(host, item);
                }
                _animationTrace?.Metrics.RecordWork(AnimationTraceWork.UpdateLayout);
                ItemsRepeater.UpdateLayout();
                if (visualPositions is not null) ApplyFlipFromCapturedPositions(visualPositions);
            }
            finally
            {
                _itemCollectionMoveInProgress = false;
            }
            InvalidateActiveSortCriterion();
            RenderCompactPreview();
            _host.NotifyOrganizerPreviewChanged(_definition.Id);
            if (_itemDragTraceEnabled)
                AppLogger.Info($"换序跟踪：宿主生命周期 prepared={_itemElementsPrepared - preparedBeforeCommit} " +
                    $"cleared={_itemElementsCleared - clearedBeforeCommit}。");
        }
        session.MarkOutcome(ItemDragState.Committed);
        FinishItemReorderSession();
        if (_itemDragTraceEnabled) AppLogger.Info($"换序跟踪：提交完成 {session.SourceIndex}->{session.TargetIndex}。");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_itemReorderSession, session))
            {
                session.MarkOutcome(ItemDragState.Cancelled);
                StopItemDragInput(keepRendering: false);
                ResetItemDragVisuals();
                FinishItemReorderSession();
            }
            AppLogger.Error("保存项目换序失败。", ex);
            ShowMessage(AppStrings.Get("SaveConfigurationError"), InfoBarSeverity.Warning);
        }
        finally
        {
            _itemDragLanding = false;
        }
    }

    private Dictionary<string, Point> CaptureRealizedItemPositions()
    {
        var positions = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < _items.Count; index++)
        {
            if (TryGetRealizedItemHost(index, out Border host))
                positions[_items[index].RelativeName] = host.TransformToVisual(ItemsRepeater).TransformPoint(new Point());
        }
        return positions;
    }

    private void ApplyFlipFromCapturedPositions(IReadOnlyDictionary<string, Point> positions)
    {
        bool animated = false;
        for (int index = 0; index < _items.Count; index++)
        {
            string relativeName = _items[index].RelativeName;
            if (!TryGetRealizedItemHost(relativeName, out Border host) ||
                !positions.TryGetValue(relativeName, out Point before)) continue;
            Point after = host.TransformToVisual(ItemsRepeater).TransformPoint(new Point());
            Vector3 inverse = new((float)(before.X - after.X), (float)(before.Y - after.Y), 0);
            if (inverse.LengthSquared() < .01f) continue;
            ItemMotionState motion = GetItemMotion(host);
            motion.Translation = inverse;
            motion.TranslationVelocity = Vector3.Zero;
            motion.TranslationTarget = Vector3.Zero;
            host.Translation = inverse;
            animated = true;
        }
        if (animated) StartNativeItemMotionRendering();
    }

    private void MaterializeNativeSourceAtTarget(ItemReorderSession session)
    {
        if (!TryGetRealizedItemHost(session.RelativeName, out Border host)) return;
        _itemDragHost = host;
        Vector2 target = session.GetSlotTranslation(
            session.SourceIndex,
            GetItemLayoutColumnCount(),
            Math.Max(1, host.Width),
            Math.Max(1, host.Height),
            GetItemLayoutGapDip());
        ItemMotionState motion = GetItemMotion(host);
        motion.Translation = new Vector3(target, 16);
        motion.TranslationTarget = motion.Translation;
        motion.TranslationVelocity = Vector3.Zero;
        motion.Scale = Vector3.One;
        motion.ScaleTarget = Vector3.One;
        motion.ScaleVelocity = Vector3.Zero;
        host.Translation = motion.Translation;
        host.Scale = Vector3.One;
        host.Opacity = 1;
    }

    private void StartNativeItemMotionRendering()
    {
        _itemDragLastFrame = Stopwatch.GetTimestamp();
        _nativeItemMotionRenderingStartedAt = _itemDragLastFrame;
        if (!_nativeItemMotionRenderingSubscribed)
        {
            CompositionTarget.Rendering += NativeItemMotionRendering;
            _nativeItemMotionRenderingSubscribed = true;
        }
        TrySetItemDragClockBoost(true);
    }

    private void NativeItemMotionRendering(object? sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        bool activePreview = _itemReorderSession is { IsActive: true };
        if (_itemReorderSession is { IsActive: true } session && _freeGridEngine is null)
        {
            bool transitionRunning = StepGapTransition(session, now);
            if (!transitionRunning) EnsureGapPreviewMaterialized(session);
            if (transitionRunning) return;
        }
        double seconds = Math.Clamp(Stopwatch.GetElapsedTime(_itemDragLastFrame, now).TotalSeconds, 0, 1d / 20);
        _itemDragLastFrame = now;
        bool settled = _freeGridEngine is not null
            ? StepIosFluidItemSprings(seconds, directDragHost: null)
            : StepItemMotions(seconds, directDragHost: null);
        if (_freeGridEngine is not null && activePreview)
        {
            StepFreeGridDropGhostSpring(seconds);
            return;
        }
        if (!settled && Stopwatch.GetElapsedTime(_nativeItemMotionRenderingStartedAt, now) < TimeSpan.FromMilliseconds(500)) return;
        if (activePreview) StopNativeItemMotionRendering(snapToTargets: !settled);
        else NormalizeItemMotionsToLayout(settled ? "FlipCompleted" : "FlipTimeout");
        _ = StartCatalogRefreshIfReady();
    }

    private void StopNativeItemMotionRendering(bool snapToTargets)
    {
        if (_nativeItemMotionRenderingSubscribed)
        {
            CompositionTarget.Rendering -= NativeItemMotionRendering;
            _nativeItemMotionRenderingSubscribed = false;
        }
        _nativeItemMotionRenderingStartedAt = 0;
        if (snapToTargets)
        {
            foreach ((Border host, ItemMotionState motion) in _itemMotionStates)
            {
                motion.Translation = IsFinite(motion.TranslationTarget) ? motion.TranslationTarget : Vector3.Zero;
                motion.TranslationVelocity = Vector3.Zero;
                motion.Scale = IsFinite(motion.ScaleTarget) ? motion.ScaleTarget : Vector3.One;
                motion.ScaleVelocity = Vector3.Zero;
                host.Translation = motion.Translation;
                host.Scale = motion.Scale;
            }
        }
        TrySetItemDragClockBoost(false);
    }

    private void NormalizeItemMotionsToLayout(string reason)
    {
        if (_itemReorderSession is not null || _shellDragActive) return;
        StopNativeItemMotionRendering(snapToTargets: false);
        var repaired = new List<object>();
        float maximumOffset = 0;
        foreach ((Border host, ItemMotionState motion) in _itemMotionStates)
        {
            float offset = Math.Max(motion.Translation.Length(), motion.TranslationTarget.Length());
            float diagnosticThreshold = reason == "FlipTimeout" ? .02f : 1f;
            if (offset >= diagnosticThreshold ||
                reason == "FlipTimeout" && motion.TranslationVelocity.LengthSquared() >= .01f)
            {
                maximumOffset = Math.Max(maximumOffset, offset);
                repaired.Add(new
                {
                    identity = host.DataContext is WidgetItem item ? item.RelativeName : host.Tag as string ?? "unknown",
                    x = motion.Translation.X,
                    y = motion.Translation.Y,
                    targetX = motion.TranslationTarget.X,
                    targetY = motion.TranslationTarget.Y
                });
            }
            motion.Translation = Vector3.Zero;
            motion.TranslationTarget = Vector3.Zero;
            motion.TranslationVelocity = Vector3.Zero;
            motion.Scale = Vector3.One;
            motion.ScaleTarget = Vector3.One;
            motion.ScaleVelocity = Vector3.Zero;
            host.Translation = Vector3.Zero;
            host.Scale = Vector3.One;
        }
        _itemMotionStates.Clear();
        if (repaired.Count > 0)
        {
            AppLogger.Info("换序回位修复JSON=" + JsonSerializer.Serialize(new
            {
                reason,
                repairedCount = repaired.Count,
                maximumOffset,
                items = repaired
            }));
        }
    }

    private void StartMouseNativeDrag(ItemReorderSession session)
    {
        if (_shellPromotionPending) return;
        _shellPromotionPending = true;
        _shellPromotionRequestedAt = Stopwatch.GetTimestamp();
        if (!DispatcherQueue.TryEnqueue(() => _ = RunSafelyAsync(
                () => _externalDragCoordinator.CompleteMouseNativeDragAsync(session),
                "原生文件拖出启动失败")))
        {
            _shellPromotionPending = false;
            CancelItemReorder();
        }
    }

    private void ApplyNativeSourcePlaceholder(Border host)
    {
        _itemDragHost = host;
        host.Opacity = 0;
        host.Shadow = null;
        Canvas.SetZIndex(host, 0);
        ItemMotionState motion = GetItemMotion(host);
        motion.Scale = Vector3.One;
        motion.ScaleTarget = Vector3.One;
        motion.ScaleVelocity = Vector3.Zero;
        host.Scale = Vector3.One;
    }

    private void CancelItemReorder(bool runPendingRefresh = true, bool preserveHover = false, bool preserveClickSequence = false)
    {
        if (!preserveClickSequence) _compactClicks.Reset();
        if (_itemReorderSession is not { } session) return;
        session.MarkOutcome(ItemDragState.Cancelled);
        StopItemDragInput(keepRendering: false);
        CleanupIosFreeGridOverlays();
        _freeGridEngine = null;
        ResetItemDragVisuals(preserveHover);
        FinishItemReorderSession(runPendingRefresh);
    }

    private void StopItemDragInput(bool keepRendering, bool keepBoundaryHook = false, bool releasePointerCapture = true)
    {
        _itemTouchHoldTimer.Stop();
        if (!keepBoundaryHook) StopItemDragBoundaryHook();
        if (!keepRendering && _itemDragRenderingSubscribed)
        {
            CompositionTarget.Rendering -= ItemDragRendering;
            _itemDragRenderingSubscribed = false;
        }
        TrySetItemDragClockBoost(false);
        _itemDragPointerId = 0;
        _itemDragLastPointerPoint = null;
        if (releasePointerCapture)
        {
            UIElement? captureOwner = _itemPointerCaptureOwner;
            _itemPointerCaptureOwner = null;
            _itemCapturedPointer = null;
            captureOwner?.ReleasePointerCaptures();
            if (!ReferenceEquals(captureOwner, _itemDragHost)) _itemDragHost?.ReleasePointerCaptures();
            // OLE drag/drop cannot take over while the XAML island still owns capture.
            _ = NativeMethods.ReleaseCapture();
        }
    }

    private void ResetItemDragVisuals(bool preserveHover = false)
    {
        if (!preserveHover) ReturnHoverWave();
        if (_itemDragHost is { } pressedHost) ResetItemInteractionVisual(pressedHost);
        InvalidateHoverGeometry();
        ResetGapTransitionState();
        StopNativeItemMotionRendering(snapToTargets: false);
        _itemMotionSettled?.TrySetCanceled();
        _itemMotionSettled = null;
        _itemDragLanding = false;
        foreach (Border host in _itemMotionStates.Keys.ToArray())
        {
            ResetItemMotion(host);
            host.Shadow = null;
            Canvas.SetZIndex(host, 0);
            ResetItemInteractionVisual(host);
            host.Opacity = 1;
        }
    }

    private void FinishItemReorderSession(bool runPendingRefresh = true)
    {
        _externalDragCoordinator.DiscardNoteDragPreparation();
        if (_itemReorderSession is not { State: ItemDragState.Committed })
        {
            StopNativeItemMotionRendering(snapToTargets: true);
        }
        if (_itemDragRenderingSubscribed)
        {
            CompositionTarget.Rendering -= ItemDragRendering;
            _itemDragRenderingSubscribed = false;
        }
        TrySetItemDragClockBoost(false);
        CleanupIosFreeGridOverlays();
        _freeGridEngine = null;
        _pendingInternalOleFreeGridSlots = null;
        _itemReorderSession = null;
        _itemDragIdentityWarnings.Clear();
        _shellPromotionPending = false;
        _itemDragLanding = false;
        _itemDragHost = null;
        _hoverDragOriginOffset = Vector3.Zero;
        _itemDragPointerType = PointerDeviceType.Mouse;
        RefreshCompactRowHighlight();
        if (IsDock) UpdateDockInputRegion();
        if (runPendingRefresh) _ = StartCatalogRefreshIfReady();
    }

    private void TrySetItemDragClockBoost(bool enabled)
    {
        if (enabled && !_itemDragClockBoosted && CompositorClockBoost.TryAcquire())
        {
            _itemDragClockBoosted = true;
        }
        else if (!enabled && _itemDragClockBoosted &&
            !_itemDragRenderingSubscribed && !_nativeItemMotionRenderingSubscribed)
        {
            CompositorClockBoost.Release();
            _itemDragClockBoosted = false;
        }
    }

    private int IndexOfItem(string relativeName)
    {
        for (int index = 0; index < _items.Count; index++)
        {
            if (_items[index].RelativeName.Equals(relativeName, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
    }

    private void FinishShellDragVisualState(ItemReorderSession originalSession)
    {
        StopItemDragBoundaryHook();
        if (ReferenceEquals(_itemReorderSession, originalSession))
        {
            ResetItemDragVisuals();
            FinishItemReorderSession(runPendingRefresh: false);
        }
        Volatile.Write(ref _shellDragActive, false);
        _shellPromotionPending = false;
        _draggedRelativeName = null;
        if (IsDock) UpdateDockInputRegion();
    }

    private void TryFinishShellDragVisualState(ItemReorderSession session)
    {
        try
        {
            FinishShellDragVisualState(session);
        }
        catch (Exception ex)
        {
            AppLogger.Error("拖动视觉状态二次清理失败。", ex);
            Volatile.Write(ref _shellDragActive, false);
            _shellPromotionPending = false;
            _shellDropFinalizing = false;
            _draggedRelativeName = null;
            if (IsDock) UpdateDockInputRegion();
        }
    }

    private void ItemsGrid_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedRelativeName is not null && _itemReorderSession is { NativeDragStarted: true } session)
        {
            AutoScrollForDrag(e);
            UpdateInternalOleTarget(e, session);
            e.AcceptedOperation = InternalOleOperation();
            e.Handled = true;
        }
        else if (_dragHasLocalFile)
        {
            AutoScrollForDrag(e);
            bool controlPressed = (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0;
            e.AcceptedOperation = OrganizerInteractionMath.SelectIncomingDropOperation(
                e.AllowedOperations,
                fromDesktop: false,
                controlPressed: controlPressed);
            if (e.AcceptedOperation != DataPackageOperation.None)
                e.DragUIOverride.Caption = AppStrings.Get("DragIntoApp");
        }
    }

    private void ItemsGrid_DragEnter(object sender, DragEventArgs e)
    {
        ResetHoverWave();
        _dragHasLocalFile = HasLocalFileDrop(e.DataView);
        if (_draggedRelativeName is null || _itemReorderSession is not { NativeDragStarted: true } session) return;
        UpdateInternalOleTarget(e, session);
        e.AcceptedOperation = InternalOleOperation();
        e.Handled = true;
    }

    private void ItemsGrid_DragLeave(object sender, DragEventArgs e)
    {
        if (_draggedRelativeName is null || _itemReorderSession is not { NativeDragStarted: true } session) return;
        if (IsFreeGridLayoutActive)
        {
            LeaveInternalOleFreeGridPreview();
            e.Handled = true;
            return;
        }
        session.LeaveInternalPreview();
        ResetGapTransitionState();
        ApplyAllProvisionalItemVisuals(animate: false);
        e.Handled = true;
    }

    private void UpdateInternalOleTarget(DragEventArgs e, ItemReorderSession session)
    {
        if (IsFreeGridLayoutActive)
        {
            UpdateInternalOleFreeGridTarget(e, session);
            return;
        }
        session.MarkInternalPreview();
        session.UpdateTarget(
            e.GetPosition(ItemsRepeater),
            _nativeDragCellWidth,
            _nativeDragCellHeight,
            GetItemLayoutGapDip(),
            GetItemLayoutColumnCount(),
            _items.Count);
        _ = StartGapTransitionIfReady(session, Stopwatch.GetTimestamp());
        StartNativeItemMotionRendering();
    }

    private void AutoScrollForDrag(DragEventArgs e)
    {
        if (ItemsScrollView.ScrollableHeight <= .5) return;
        const double edge = 52;
        const double step = 18;
        Point point = e.GetPosition(ItemsScrollView);
        double vertical = 0;
        if (point.Y < edge) vertical = -step;
        else if (point.Y > ItemsScrollView.ActualHeight - edge) vertical = step;
        if (vertical < 0 && ItemsScrollView.VerticalOffset <= .5 ||
            vertical > 0 && ItemsScrollView.VerticalOffset >= ItemsScrollView.ScrollableHeight - .5) return;
        if (vertical != 0)
        {
            ItemsScrollView.ScrollBy(0, vertical, InstantScrollOptions);
        }
    }

    private async void ItemsGrid_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        await IncomingDropOperation.RunAsync(deferral.Complete,
            HoldIncomingDrop, HoldExpansionModeInteraction,
            () => RunSafelyAsync(() => HandleItemsGridDropAsync(sender, e), "项目拖入处理失败"), () => _dragHasLocalFile = false);
    }

    private async Task HandleItemsGridDropAsync(object sender, DragEventArgs e)
    {
        if (_draggedRelativeName is not null)
        {
            if (_itemReorderSession is { NativeDragStarted: true } session)
            {
                if (IsFreeGridLayoutActive)
                {
                    CaptureInternalOleFreeGridDropSlots(e, session);
                }
                else
                {
                    UpdateInternalOleTarget(e, session);
                    CompleteGapTransitionsImmediately(session);
                    session.SealVisualIndices(_items.Count);
                }
                _internalOleDropAccepted = true;
                e.AcceptedOperation = InternalOleOperation();
            }
            _dragHasLocalFile = false;
            e.Handled = true;
            return;
        }
        e.Handled = true;
        RecordExternalDropSlotFromEvent(e);
        try { await _catalogController.ImportFromDragAsync(e); }
        finally { _dragHasLocalFile = false; }
    }

    private void WindowRoot_DragEnter(object sender, DragEventArgs e)
    {
        ResetHoverWave();
        _dragHasLocalFile = HasLocalFileDrop(e.DataView);
        if (!_expanded && _draggedRelativeName is null && _dragHasLocalFile)
        {
            StartHoverExpand(scrollToEnd: true);
        }
    }

    private DataPackageOperation InternalOleOperation() =>
        _draggedRelativeName?.StartsWith("note:", StringComparison.OrdinalIgnoreCase) == true
            ? DataPackageOperation.Move
            : DataPackageOperation.Link;

    private void WindowRoot_DragLeave(object sender, DragEventArgs e)
    {
        _dragHasLocalFile = false;
        _externalHoverTimer.Stop();
        _hoverExpandScrollToEnd = false;
        if (_draggedRelativeName is null || _itemReorderSession is not { NativeDragStarted: true } session) return;
        if (IsFreeGridLayoutActive)
        {
            LeaveInternalOleFreeGridPreview();
            return;
        }
        session.LeaveInternalPreview();
        ResetGapTransitionState();
        ApplyAllProvisionalItemVisuals(animate: false);
    }

    private void WindowRoot_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedRelativeName is not null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }
        if (_dragHasLocalFile)
        {
            bool controlPressed = (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0;
            e.AcceptedOperation = OrganizerInteractionMath.SelectIncomingDropOperation(
                e.AllowedOperations,
                fromDesktop: false,
                controlPressed: controlPressed);
            if (e.AcceptedOperation != DataPackageOperation.None)
                e.DragUIOverride.Caption = AppStrings.Get("DragIntoApp");
        }
    }

    private async void WindowRoot_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        await IncomingDropOperation.RunAsync(deferral.Complete,
            HoldIncomingDrop, HoldExpansionModeInteraction,
            () => RunSafelyAsync(() => HandleWindowDropAsync(e), "窗口文件拖入处理失败"),
            () => _dragHasLocalFile = false);
    }

    private async Task HandleWindowDropAsync(DragEventArgs e)
    {
        _externalHoverTimer.Stop();
        if (_draggedRelativeName is not null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
        }
        else
        {
            e.Handled = true;
            try
            {
                await RunSafelyAsync(() => _catalogController.ImportFromDragAsync(e), "窗口文件拖入处理失败");
            }
            finally
            {
                _dragHasLocalFile = false;
            }
        }
    }
}
