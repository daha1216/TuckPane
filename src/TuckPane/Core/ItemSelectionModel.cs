namespace TuckPane.Core;

/// <summary>
/// Pure Windows Explorer-compatible multi-selection state machine for items inside an
/// expanded organizer (single click, Ctrl-toggle, Shift-range, Ctrl+A, and marquee lasso).
/// </summary>
internal sealed class ItemSelectionModel
{
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private int _anchorIndex = -1;

    internal int Count => _selected.Count;
    internal bool HasSelection => _selected.Count > 0;
    internal bool HasMultipleSelection => _selected.Count > 1;
    internal int AnchorIndex => _anchorIndex;

    internal bool Contains(string relativeName) =>
        !string.IsNullOrEmpty(relativeName) && _selected.Contains(relativeName);

    internal IReadOnlyList<string> OrderedSelection(IReadOnlyList<string> visibleOrder)
    {
        if (_selected.Count == 0 || visibleOrder.Count == 0) return [];
        var ordered = new List<string>(Math.Min(_selected.Count, visibleOrder.Count));
        foreach (string name in visibleOrder)
        {
            if (_selected.Contains(name)) ordered.Add(name);
        }
        return ordered;
    }

    internal IReadOnlyCollection<string> Snapshot() => _selected.ToArray();

    internal bool Clear()
    {
        bool hadAny = _selected.Count > 0 || _anchorIndex >= 0;
        _selected.Clear();
        _anchorIndex = -1;
        return hadAny;
    }

    internal void SelectSingle(string relativeName, int index)
    {
        _selected.Clear();
        if (!string.IsNullOrEmpty(relativeName))
            _selected.Add(relativeName);
        _anchorIndex = index;
    }

    internal void HandleClick(IReadOnlyList<string> visibleOrder, int index, bool ctrl, bool shift)
    {
        if (index < 0 || index >= visibleOrder.Count)
        {
            if (!ctrl) Clear();
            return;
        }

        string target = visibleOrder[index];
        if (shift && _anchorIndex >= 0 && _anchorIndex < visibleOrder.Count)
        {
            if (!ctrl) _selected.Clear();
            int start = Math.Min(_anchorIndex, index);
            int end = Math.Max(_anchorIndex, index);
            for (int i = start; i <= end; i++)
                _selected.Add(visibleOrder[i]);
            return;
        }

        if (ctrl)
        {
            if (!_selected.Remove(target))
                _selected.Add(target);
            _anchorIndex = index;
            return;
        }

        SelectSingle(target, index);
    }

    internal void ApplyMarquee(
        IEnumerable<string> intersectedNames,
        bool additiveCtrl,
        IReadOnlyCollection<string>? baselineBeforeDrag = null)
    {
        _selected.Clear();
        if (additiveCtrl && baselineBeforeDrag is not null)
        {
            foreach (string existing in baselineBeforeDrag)
                _selected.Add(existing);
            foreach (string hit in intersectedNames)
            {
                if (!_selected.Add(hit))
                    _selected.Remove(hit);
            }
            return;
        }

        foreach (string hit in intersectedNames)
            _selected.Add(hit);
    }

    internal void SelectAll(IReadOnlyList<string> visibleOrder)
    {
        _selected.Clear();
        foreach (string name in visibleOrder)
            _selected.Add(name);
        _anchorIndex = visibleOrder.Count > 0 ? 0 : -1;
    }

    internal bool Prune(IEnumerable<string> validNames)
    {
        if (_selected.Count == 0) return false;
        var validSet = new HashSet<string>(validNames, StringComparer.OrdinalIgnoreCase);
        int removed = _selected.RemoveWhere(name => !validSet.Contains(name));
        if (_selected.Count == 0) _anchorIndex = -1;
        return removed > 0;
    }
}
