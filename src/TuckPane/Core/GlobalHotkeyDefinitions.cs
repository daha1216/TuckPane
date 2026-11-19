namespace TuckPane.Core;

public enum QuickHideHotkeyPreset
{
    Off = 0,
    CtrlAltH = 1,
    CtrlAltSpace = 2,
    CtrlShiftSpace = 3,
    CtrlAltBackquote = 4
}

public enum SearchHotkeyPreset
{
    Off = 0,
    CtrlAltF = 1,
    CtrlAltK = 2,
    CtrlAltSpace = 3
}

public enum PeekHotkeyPreset
{
    Off = 0,
    CtrlAltP = 1,
    CtrlAltG = 2,
    CtrlAltShiftP = 3
}

internal readonly record struct HotkeyBinding(uint Modifiers, uint VirtualKey, string Label)
{
    internal bool SameKeyCombo(HotkeyBinding other) =>
        (Modifiers & ~GlobalHotkeyDefinitions.ModNoRepeat) == (other.Modifiers & ~GlobalHotkeyDefinitions.ModNoRepeat) &&
        VirtualKey == other.VirtualKey;
}

internal static class GlobalHotkeyDefinitions
{
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModNoRepeat = 0x4000;

    internal const int QuickHideHotkeyId = 0x545001;
    internal const int SearchHotkeyId = 0x545002;
    internal const int PeekHotkeyId = 0x545003;

    private const uint VkSpace = 0x20;
    private const uint VkF = 0x46;
    private const uint VkG = 0x47;
    private const uint VkH = 0x48;
    private const uint VkK = 0x4B;
    private const uint VkP = 0x50;
    private const uint VkOem3 = 0xC0; // ` / ~

    internal static QuickHideHotkeyPreset Normalize(QuickHideHotkeyPreset value) =>
        Enum.IsDefined(value) ? value : QuickHideHotkeyPreset.Off;

    internal static SearchHotkeyPreset Normalize(SearchHotkeyPreset value) =>
        Enum.IsDefined(value) ? value : SearchHotkeyPreset.Off;

    internal static PeekHotkeyPreset Normalize(PeekHotkeyPreset value) =>
        Enum.IsDefined(value) ? value : PeekHotkeyPreset.Off;

    internal static HotkeyBinding? Resolve(QuickHideHotkeyPreset preset) => preset switch
    {
        QuickHideHotkeyPreset.CtrlAltH => new(ModControl | ModAlt | ModNoRepeat, VkH, "Ctrl+Alt+H"),
        QuickHideHotkeyPreset.CtrlAltSpace => new(ModControl | ModAlt | ModNoRepeat, VkSpace, "Ctrl+Alt+Space"),
        QuickHideHotkeyPreset.CtrlShiftSpace => new(ModControl | ModShift | ModNoRepeat, VkSpace, "Ctrl+Shift+Space"),
        QuickHideHotkeyPreset.CtrlAltBackquote => new(ModControl | ModAlt | ModNoRepeat, VkOem3, "Ctrl+Alt+`"),
        _ => null
    };

    internal static HotkeyBinding? Resolve(SearchHotkeyPreset preset) => preset switch
    {
        SearchHotkeyPreset.CtrlAltF => new(ModControl | ModAlt | ModNoRepeat, VkF, "Ctrl+Alt+F"),
        SearchHotkeyPreset.CtrlAltK => new(ModControl | ModAlt | ModNoRepeat, VkK, "Ctrl+Alt+K"),
        SearchHotkeyPreset.CtrlAltSpace => new(ModControl | ModAlt | ModNoRepeat, VkSpace, "Ctrl+Alt+Space"),
        _ => null
    };

    internal static HotkeyBinding? Resolve(PeekHotkeyPreset preset) => preset switch
    {
        PeekHotkeyPreset.CtrlAltP => new(ModControl | ModAlt | ModNoRepeat, VkP, "Ctrl+Alt+P"),
        PeekHotkeyPreset.CtrlAltG => new(ModControl | ModAlt | ModNoRepeat, VkG, "Ctrl+Alt+G"),
        PeekHotkeyPreset.CtrlAltShiftP => new(ModControl | ModAlt | ModShift | ModNoRepeat, VkP, "Ctrl+Alt+Shift+P"),
        _ => null
    };

    internal static bool ConflictsWithAny(HotkeyBinding? candidate, params HotkeyBinding?[] others)
    {
        if (candidate is not HotkeyBinding active) return false;
        foreach (HotkeyBinding? other in others)
        {
            if (other is HotkeyBinding existing && active.SameKeyCombo(existing))
                return true;
        }
        return false;
    }
}
