using TuckPane.Models;

namespace TuckPane.Core;

internal static class OrganizerMenuVisibility
{
    // Stable resource keys are also the persisted identities, independent of language and position.
    internal static readonly string[] Actions =
    [
        "ContextAddItem", "ContextNewFolder", "ContextNewNote", "ContextNewTodo",
        "ContextPaste", "ContextDeleteWindow", "ContextOpenStorage", "ContextRename",
        "ContextManage", "ContextDuplicate", "ContextSwitchPlacement", "ContextSwitchContent",
        "ContextSwitchExpansion", "ContextHideName", "ContextSort", "ContextTidyEdgeStack",
        "ContextAlignToDesktopGrid", "ContextImportDesktopSelection", "ContextCompactGridGaps"
    ];

    internal static bool IsEnabled(GlobalSettings settings, string key) =>
        settings.OrganizerMenuVisibility?.GetValueOrDefault(key, true) ?? true;

    internal static bool IsVisible(GlobalSettings settings, string key, OrganizerPlacementMode mode)
    {
        if (!IsEnabled(settings, key)) return false;
        return key switch
        {
            "ContextDuplicate" or "ContextRename" => mode != OrganizerPlacementMode.Station,
            "ContextSwitchPlacement" or "ContextSwitchContent" or "ContextSwitchExpansion" or "ContextHideName" or "ContextCompactGridGaps"
                => OrganizerKinds.IsRegular(mode),
            "ContextTidyEdgeStack" or "ContextAlignToDesktopGrid" => mode == OrganizerPlacementMode.Floating,
            _ => true
        };
    }

    internal static string[] VisibleActions(GlobalSettings settings, OrganizerPlacementMode mode) =>
        Actions.Where(key => IsVisible(settings, key, mode)).ToArray();

    internal static bool ShowSeparator(IReadOnlyCollection<string> visible) =>
        Actions.Take(8).Any(visible.Contains) && Actions.Skip(8).Any(visible.Contains);
}
