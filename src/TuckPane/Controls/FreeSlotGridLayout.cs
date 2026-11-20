using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TuckPane.Core;
using Windows.Foundation;

namespace TuckPane.Controls;

/// <summary>
/// WinUI 3 NonVirtualizingLayout that places real WidgetItem elements at arbitrary
/// sparse (column, row) grid slots, enabling iOS 18 / iOS 26 style free placement
/// with empty spaces and blank rows without polluting the items collection.
/// </summary>
internal sealed class FreeSlotGridLayout : NonVirtualizingLayout
{
    private double _cellWidth = 100;
    private double _cellHeight = 100;
    private double _gap = 12;
    private int _columns = 3;
    private int _minimumVisibleRows = 3;
    private bool _includeDragBufferRow;
    private Func<int, int>? _slotResolver;

    internal double CellWidth
    {
        get => _cellWidth;
        set
        {
            double next = Math.Max(1, value);
            if (Math.Abs(_cellWidth - next) < 0.01) return;
            _cellWidth = next;
            InvalidateMeasure();
        }
    }

    internal double CellHeight
    {
        get => _cellHeight;
        set
        {
            double next = Math.Max(1, value);
            if (Math.Abs(_cellHeight - next) < 0.01) return;
            _cellHeight = next;
            InvalidateMeasure();
        }
    }

    internal double Gap
    {
        get => _gap;
        set
        {
            double next = Math.Max(0, value);
            if (Math.Abs(_gap - next) < 0.01) return;
            _gap = next;
            InvalidateMeasure();
        }
    }

    internal int Columns
    {
        get => _columns;
        set
        {
            int next = Math.Max(1, value);
            if (_columns == next) return;
            _columns = next;
            InvalidateMeasure();
        }
    }

    internal int MinimumVisibleRows
    {
        get => _minimumVisibleRows;
        set
        {
            int next = Math.Max(1, value);
            if (_minimumVisibleRows == next) return;
            _minimumVisibleRows = next;
            InvalidateMeasure();
        }
    }

    internal bool IncludeDragBufferRow
    {
        get => _includeDragBufferRow;
        set
        {
            if (_includeDragBufferRow == value) return;
            _includeDragBufferRow = value;
            InvalidateMeasure();
        }
    }

    internal Func<int, int>? SlotResolver
    {
        get => _slotResolver;
        set
        {
            _slotResolver = value;
            InvalidateMeasure();
        }
    }

    internal void InvalidateSlots()
    {
        InvalidateMeasure();
        InvalidateArrange();
    }

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        int count = context.Children.Count;
        var slotSize = new Size(_cellWidth, _cellHeight);
        int maxRow = Math.Max(1, _minimumVisibleRows) - 1;

        for (int index = 0; index < count; index++)
        {
            UIElement child = context.Children[index];
            child.Measure(slotSize);
            int slot = ResolveSlot(index);
            maxRow = Math.Max(maxRow, slot / _columns);
        }

        int totalRows = maxRow + 1 + (_includeDragBufferRow ? 1 : 0);
        double totalWidth = _columns * _cellWidth + Math.Max(0, _columns - 1) * _gap;
        double totalHeight = totalRows * _cellHeight + Math.Max(0, totalRows - 1) * _gap;
        return new Size(Math.Max(1, totalWidth), Math.Max(1, totalHeight));
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        int count = context.Children.Count;
        for (int index = 0; index < count; index++)
        {
            UIElement child = context.Children[index];
            int slot = ResolveSlot(index);
            Point origin = FreeGridSlotMap.SlotOrigin(slot, _columns, _cellWidth, _cellHeight, _gap);
            child.Arrange(new Rect(origin.X, origin.Y, _cellWidth, _cellHeight));
        }
        return finalSize;
    }

    private int ResolveSlot(int index)
    {
        if (_slotResolver is null) return index;
        int slot = _slotResolver(index);
        return slot >= 0 ? slot : index;
    }
}
