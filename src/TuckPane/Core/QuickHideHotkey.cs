namespace TuckPane.Core;

/// <summary>A global shortcut that shows or hides every pane, note and to-do at once.</summary>
internal sealed record QuickHideHotkey(string Id, uint Modifiers, uint VirtualKey, string Display)
{
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;

    /// <summary>
    /// Fixed presets instead of a free-form recorder: each avoids Win-key combinations reserved
    /// by Windows and keys that type characters. Another program may still own one of them, so
    /// the host registers before it saves and offers the others when that fails.
    /// </summary>
    internal static readonly QuickHideHotkey[] Presets =
    [
        new("Ctrl+Alt+H", ModControl | ModAlt, 0x48, "Ctrl + Alt + H"),
        new("Ctrl+Alt+Space", ModControl | ModAlt, 0x20, "Ctrl + Alt + Space"),
        new("Ctrl+Shift+Space", ModControl | ModShift, 0x20, "Ctrl + Shift + Space"),
        new("Ctrl+Alt+Backquote", ModControl | ModAlt, 0xC0, "Ctrl + Alt + `"),
    ];

    /// <summary>Presets for the "Find in organizers" shortcut. Ctrl+Shift+F is avoided on purpose:
    /// editors and browsers use it, and a global hotkey would silently steal it from them.</summary>
    internal static readonly QuickHideHotkey[] SearchPresets =
    [
        new("Ctrl+Alt+F", ModControl | ModAlt, 0x46, "Ctrl + Alt + F"),
        new("Ctrl+Alt+K", ModControl | ModAlt, 0x4B, "Ctrl + Alt + K"),
        new("Ctrl+Alt+Space", ModControl | ModAlt, 0x20, "Ctrl + Alt + Space"),
    ];

    /// <summary>Null, empty or an unknown saved value means the shortcut is off.</summary>
    internal static QuickHideHotkey? Find(string? id) => Find(Presets, id);

    internal static string? Normalize(string? id) => Find(id)?.Id;

    internal static QuickHideHotkey? FindSearch(string? id) => Find(SearchPresets, id);

    internal static string? NormalizeSearch(string? id) => FindSearch(id)?.Id;

    private static QuickHideHotkey? Find(QuickHideHotkey[] presets, string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : presets.FirstOrDefault(preset => preset.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
