namespace TuckPane.Core;

/// <summary>
/// Controls how collapsed floating/positioned organizers blend into the Windows desktop.
/// </summary>
public enum CompactVisualStyle
{
    /// <summary>Classic TuckPane frosted rounded tile card.</summary>
    GlassTile = 0,

    /// <summary>
    /// Frameless native Windows 11 desktop blend: removes the heavy resting card
    /// behind the 2x2 icon cluster and shows a crisp Windows-native translucent
    /// hover/selection plate + multi-pass label shadow halo.
    /// </summary>
    NativeDesktop = 1,

    /// <summary>
    /// Refined Windows 11 Fluent folder silhouette with subtle folder tab and
    /// translucent front pocket so organizers read naturally as desktop folders.
    /// </summary>
    FluentFolder = 2
}

internal static class CompactVisualStyles
{
    internal static CompactVisualStyle Normalize(CompactVisualStyle value) =>
        Enum.IsDefined(value) ? value : CompactVisualStyle.GlassTile;

    internal static bool UsesNativeBlendSurface(CompactVisualStyle style) =>
        style is CompactVisualStyle.NativeDesktop or CompactVisualStyle.FluentFolder;

    internal static bool ShowsFolderSilhouette(CompactVisualStyle style) =>
        style == CompactVisualStyle.FluentFolder;

    internal static double BackdropOpacity(CompactVisualStyle style) => style switch
    {
        CompactVisualStyle.NativeDesktop => 0.0,
        CompactVisualStyle.FluentFolder => 0.42,
        _ => 1.0
    };

    internal static double LabelShadowOpacity(CompactVisualStyle style, bool highContrast)
    {
        if (highContrast) return 0.0;
        return style switch
        {
            CompactVisualStyle.NativeDesktop => 0.88,
            CompactVisualStyle.FluentFolder => 0.82,
            _ => 0.65
        };
    }

    internal static int LabelMaxLines(CompactVisualStyle style) =>
        style is CompactVisualStyle.NativeDesktop or CompactVisualStyle.FluentFolder ? 2 : 1;
}
