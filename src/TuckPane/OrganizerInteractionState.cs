using System;

namespace TuckPane;

[Flags]
internal enum OrganizerBusyFlags : uint
{
    None = 0,
    ChangingExpansionMode = 1u << 0,
    Animating = 1u << 1,
    PressActive = 1u << 2,
    WidgetDragging = 1u << 3,
    NativeMouseCapture = 1u << 4,
    CanvasResizing = 1u << 5,
    WidgetGestureCompleting = 1u << 6,
    ResizeGestureCompleting = 1u << 7,
    ItemReordering = 1u << 8,
    ShellDragActive = 1u << 9,
    ShellPromotionPending = 1u << 10,
    ShellDropFinalizing = 1u << 11,
    ItemDragLanding = 1u << 12,
    ItemCollectionMoving = 1u << 13,
    NativeItemMotionRendering = 1u << 14,
    DragHasLocalFile = 1u << 15,
    ReceivingDrop = 1u << 16,
    ShellContextMenuOpen = 1u << 17,
    ContextMenuActivated = 1u << 18,
    OverlayOpen = 1u << 19,
    AddingItem = 1u << 20,
    TitleEditing = 1u << 21,
    TransferQueueActive = 1u << 22,
    ModeInteractionHeld = 1u << 23,
    DesktopLayerUnavailable = 1u << 24,
    DesktopInputActivated = 1u << 25
}

/// <summary>
/// Single source of truth for organizer window busy/interaction predicates,
/// preventing drift across hover, expansion-mode transitions, catalog reconciliation,
/// desktop z-order repair, and pointer-leave auto-collapse (both regular and Station modes).
/// </summary>
internal readonly record struct OrganizerInteractionSnapshot(OrganizerBusyFlags Flags)
{
    private const OrganizerBusyFlags HoverBusyMask =
        OrganizerBusyFlags.ChangingExpansionMode |
        OrganizerBusyFlags.PressActive |
        OrganizerBusyFlags.WidgetDragging |
        OrganizerBusyFlags.CanvasResizing |
        OrganizerBusyFlags.ItemReordering |
        OrganizerBusyFlags.ShellDragActive |
        OrganizerBusyFlags.ShellDropFinalizing |
        OrganizerBusyFlags.ShellPromotionPending |
        OrganizerBusyFlags.ItemDragLanding |
        OrganizerBusyFlags.NativeItemMotionRendering |
        OrganizerBusyFlags.DragHasLocalFile |
        OrganizerBusyFlags.ShellContextMenuOpen |
        OrganizerBusyFlags.ContextMenuActivated |
        OrganizerBusyFlags.OverlayOpen |
        OrganizerBusyFlags.AddingItem;

    private const OrganizerBusyFlags ExpansionModeBusyMask =
        OrganizerBusyFlags.PressActive |
        OrganizerBusyFlags.WidgetDragging |
        OrganizerBusyFlags.NativeMouseCapture |
        OrganizerBusyFlags.CanvasResizing |
        OrganizerBusyFlags.WidgetGestureCompleting |
        OrganizerBusyFlags.ResizeGestureCompleting |
        OrganizerBusyFlags.ShellDragActive |
        OrganizerBusyFlags.ShellDropFinalizing |
        OrganizerBusyFlags.ShellPromotionPending |
        OrganizerBusyFlags.ItemReordering |
        OrganizerBusyFlags.ModeInteractionHeld |
        OrganizerBusyFlags.DragHasLocalFile;

    private const OrganizerBusyFlags CatalogRefreshBusyMask =
        OrganizerBusyFlags.ItemReordering |
        OrganizerBusyFlags.ShellDragActive |
        OrganizerBusyFlags.ShellPromotionPending |
        OrganizerBusyFlags.ShellDropFinalizing |
        OrganizerBusyFlags.ItemDragLanding |
        OrganizerBusyFlags.ItemCollectionMoving |
        OrganizerBusyFlags.NativeItemMotionRendering;

    private const OrganizerBusyFlags DesktopLayerBusyMask =
        OrganizerBusyFlags.WidgetDragging |
        OrganizerBusyFlags.ItemReordering |
        OrganizerBusyFlags.ShellDragActive |
        OrganizerBusyFlags.ShellContextMenuOpen |
        OrganizerBusyFlags.OverlayOpen |
        OrganizerBusyFlags.DesktopLayerUnavailable |
        OrganizerBusyFlags.DesktopInputActivated;

    private const OrganizerBusyFlags PointerLeaveCollapseBusyMask =
        OrganizerBusyFlags.Animating |
        OrganizerBusyFlags.TransferQueueActive |
        OrganizerBusyFlags.ShellDragActive |
        OrganizerBusyFlags.ShellPromotionPending |
        OrganizerBusyFlags.ShellDropFinalizing |
        OrganizerBusyFlags.ItemReordering |
        OrganizerBusyFlags.CanvasResizing |
        OrganizerBusyFlags.PressActive |
        OrganizerBusyFlags.WidgetDragging |
        OrganizerBusyFlags.NativeMouseCapture |
        OrganizerBusyFlags.ShellContextMenuOpen |
        OrganizerBusyFlags.OverlayOpen |
        OrganizerBusyFlags.ReceivingDrop |
        OrganizerBusyFlags.TitleEditing;

    private const OrganizerBusyFlags DragGestureMask =
        OrganizerBusyFlags.PressActive |
        OrganizerBusyFlags.WidgetDragging |
        OrganizerBusyFlags.ItemReordering |
        OrganizerBusyFlags.ShellDragActive;

    private const OrganizerBusyFlags IdleHoverExpandBusyMask =
        OrganizerBusyFlags.PressActive |
        OrganizerBusyFlags.WidgetDragging |
        OrganizerBusyFlags.NativeMouseCapture;

    public bool BlocksHoverInteraction => (Flags & HoverBusyMask) != 0;

    public bool BlocksExpansionModeSwitch => (Flags & ExpansionModeBusyMask) != 0;

    public bool BlocksCatalogRefresh => (Flags & CatalogRefreshBusyMask) != 0;

    public bool BlocksDesktopLayerRepair => (Flags & DesktopLayerBusyMask) != 0;

    public bool BlocksPointerLeaveCollapse => (Flags & PointerLeaveCollapseBusyMask) != 0;

    public bool IsDragGestureActive => (Flags & DragGestureMask) != 0;

    public bool BlocksIdleHoverExpand => (Flags & IdleHoverExpandBusyMask) != 0;
}
