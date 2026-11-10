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

    /// <summary>Null, empty or an unknown saved value means the shortcut is off.</summary>
    internal static QuickHideHotkey? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Presets.FirstOrDefault(preset => preset.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    internal static string? Normalize(string? id) => Find(id)?.Id;
}
