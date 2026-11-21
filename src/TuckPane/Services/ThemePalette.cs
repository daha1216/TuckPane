using TuckPane.Models;
using Windows.UI;

namespace TuckPane.Services;

internal readonly record struct ThemeEffectParameters(
    float BlurAmount,
    float Saturation,
    float LuminosityOpacity);

/// <summary>
/// Pure-data description of the single Glass composition pipeline. Keeping
/// this separate from Composition objects makes transparency, blur and
/// highlight semantics testable without creating a window or GPU graph.
/// </summary>
internal readonly record struct ThemeCompositionPlan(
    // The sampled Glass result is already composited. Only colour-only
    // branches have a translucent output; TintOpacity is the user's weight.
    float SurfaceOpacity,
    float TintOpacity,
    float DesktopOpacity,
    float BlurAmount,
    bool UsesGaussianBlur,
    bool RequiresHostBackdrop,
    float Saturation,
    float LuminosityOpacity,
    float HighlightOpacity,
    Color TintColor,
    Color LuminosityColor,
    bool UseEffects);

internal static class ThemePalette
{
    internal const float OrganizerGlassOuterEdgeThicknessDip = 1.25f;
    internal const float OrganizerGlassInnerEdgeThicknessDip = .75f;
    internal const float OrganizerGlassInnerEdgeInsetDip = 1.5f;
    internal const float GlassEdgeOuterPureThicknessDip = 1.0f;
    internal const float GlassEdgeHighlightThicknessDip = 1.35f;
    internal const float GlassEdgeHighlightInsetDip = 0.0f;
    internal const float GlassEdgeCounterArcThicknessDip = 1.10f;
    internal const float GlassEdgeCounterArcInsetDip = 0.0f;
    internal const float GlassEdgeTextureThicknessDip = 0.75f;
    internal const float GlassEdgeTextureInsetDip = 0.25f;
    internal const float GlassEdgePointerRimThicknessDip = 1.55f;
    internal const float GlassEdgePointerRimInsetDip = 0.0f;
    internal const float GlassEdgeMinimumOpacity = .92f;

    private const float GlassBlurAmount = 18f;
    private const float GlassSaturation = 1.75f;
    private const float GlassLuminosityOpacity = .06f;

    internal static Color SurfaceColor(ThemeValues theme) => FromArgb(theme.ColorArgb);

    internal static Color TintColor(ThemeValues theme) => SurfaceColor(theme);

    internal static Color ForegroundColor(ThemeValues theme)
    {
        Color background = SurfaceColor(theme);
        double luminance = RelativeLuminance(background);
        double blackContrast = (luminance + .05) / .05;
        double whiteContrast = 1.05 / (luminance + .05);
        return blackContrast >= whiteContrast
            ? Color.FromArgb(255, 31, 31, 31)
            : Color.FromArgb(255, 245, 245, 245);
    }

    internal static Color ResolveOrganizerTextColor(OrganizerTextColor mode, ThemeValues theme) =>
        Color.FromArgb(255, 255, 255, 255);

    internal static bool IsDark(ThemeValues theme) => ForegroundColor(theme).R > 128;

    /// <summary>
    /// The persisted field keeps its legacy name for JSON compatibility, but
    /// its value is the user-facing background opacity: 0 is transparent and
    /// 1 is a fully opaque theme surface.
    /// </summary>
    internal static float TintOpacity(ThemeValues theme) =>
        (float)(theme.SolidColorMode
            ? GlobalSettings.NormalizeSolidThemeOpacity(theme.SolidOpacity)
            : GlobalSettings.NormalizeThemeTransparency(theme.Transparency));

    internal static float DesktopOpacity(ThemeValues theme) =>
        theme.SolidColorMode ? 0f : 1f - TintOpacity(theme);

    internal static float OpticalProgress(ThemeValues theme) =>
        Math.Clamp(
            (float)GlobalSettings.NormalizeThemeBlurStrength(theme.BlurStrength),
            0,
            1);

    internal static float HighlightOpacity(ThemeValues theme) =>
        theme.SolidColorMode ? 0f : 4f * TintOpacity(theme) * (1f - TintOpacity(theme));

    internal static Color LuminosityColor(ThemeValues theme) =>
        Color.FromArgb(255, 128, 128, 128);

    internal static ThemeEffectParameters Effect() =>
        new(GlassBlurAmount, GlassSaturation, GlassLuminosityOpacity);

    internal static float EffectiveBlurAmount(ThemeValues theme) =>
        Effect().BlurAmount *
        (float)GlobalSettings.NormalizeThemeBlurStrength(theme.BlurStrength);

    internal static ThemeCompositionPlan BuildCompositionPlan(
        ThemeValues theme,
        bool useEffects)
    {
        if (theme.FullyTransparent)
            return new ThemeCompositionPlan(0, 0, 1, 0, false, false, 1, 0, 0,
                TintColor(theme), LuminosityColor(theme), useEffects);
        ThemeEffectParameters parameters = Effect();
        float tintOpacity = TintOpacity(theme);
        float desktopOpacity = DesktopOpacity(theme);
        float blurStrength = (float)GlobalSettings.NormalizeThemeBlurStrength(theme.BlurStrength);
        // Zero tint is still frosted when blur is positive. Fully opaque tint
        // and clear/solid paths need no sampled background.
        bool requiresHostBackdrop = useEffects && !theme.SolidColorMode && tintOpacity < 1 && blurStrength > 0;
        float blurAmount = requiresHostBackdrop ? parameters.BlurAmount * blurStrength : 0;
        return new ThemeCompositionPlan(
            SurfaceOpacity: requiresHostBackdrop ? 1 : tintOpacity,
            TintOpacity: tintOpacity,
            DesktopOpacity: desktopOpacity,
            BlurAmount: blurAmount,
            UsesGaussianBlur: requiresHostBackdrop,
            RequiresHostBackdrop: requiresHostBackdrop,
            Saturation: requiresHostBackdrop ? parameters.Saturation : 1,
            LuminosityOpacity: requiresHostBackdrop ? parameters.LuminosityOpacity : 0,
            HighlightOpacity: useEffects ? HighlightOpacity(theme) : 0,
            TintColor: TintColor(theme),
            LuminosityColor: LuminosityColor(theme),
            UseEffects: useEffects);
    }

    internal static Color WithOpacity(Color color, float opacity) => Color.FromArgb(
        (byte)Math.Clamp(Math.Round(color.A * Math.Clamp(opacity, 0, 1)), 0, 255),
        color.R,
        color.G,
        color.B);

    internal static float EdgeOpacity(ThemeValues theme) => theme.FullyTransparent ? 0 : GlassEdgeMinimumOpacity;

    internal static IReadOnlyList<(float Offset, Color Color)> GlassHighlightStops { get; } =
    [
        (0f, Color.FromArgb(0, 255, 255, 255)),
        (.38f, Color.FromArgb(0, 255, 255, 255)),
        (.49f, Color.FromArgb(42, 255, 255, 255)),
        (.58f, Color.FromArgb(0, 255, 255, 255)),
        (1f, Color.FromArgb(0, 255, 255, 255))
    ];

    // A restrained arc-like sheen used by local glass surfaces.  The broad
    // low-alpha band keeps the effect legible on both light and dark themes
    // without introducing another backdrop or bitmap resource.
    internal static IReadOnlyList<(float Offset, Color Color)> GlassArcStops { get; } =
    [
        (0f, Color.FromArgb(0, 255, 255, 255)),
        (.16f, Color.FromArgb(18, 255, 255, 255)),
        (.28f, Color.FromArgb(34, 255, 255, 255)),
        (.4f, Color.FromArgb(10, 255, 255, 255)),
        (.62f, Color.FromArgb(0, 255, 255, 255)),
        (1f, Color.FromArgb(0, 0, 0, 0))
    ];

    // Deterministic pseudo-noise represented as many tiny gradient bands. It
    // is static, generated from code, and deliberately kept below perceptual
    // threshold so text and controls remain crisp.
    internal static IReadOnlyList<(float Offset, Color Color)> GlassTextureStops { get; } =
    [
        (0f, Color.FromArgb(0, 255, 255, 255)),
        (.07f, Color.FromArgb(5, 255, 255, 255)),
        (.13f, Color.FromArgb(0, 255, 255, 255)),
        (.21f, Color.FromArgb(4, 255, 255, 255)),
        (.29f, Color.FromArgb(0, 255, 255, 255)),
        (.37f, Color.FromArgb(5, 255, 255, 255)),
        (.44f, Color.FromArgb(0, 255, 255, 255)),
        (.53f, Color.FromArgb(4, 0, 0, 0)),
        (.61f, Color.FromArgb(0, 0, 0, 0)),
        (.69f, Color.FromArgb(5, 255, 255, 255)),
        (.77f, Color.FromArgb(0, 255, 255, 255)),
        (.86f, Color.FromArgb(4, 0, 0, 0)),
        (.94f, Color.FromArgb(0, 0, 0, 0)),
        (1f, Color.FromArgb(0, 0, 0, 0))
    ];

    internal static IReadOnlyList<(float Offset, Color Color)> OrganizerGlassOuterEdgeStops { get; } =
    [
        (0f, Color.FromArgb(112, 255, 255, 255)),
        (.55f, Color.FromArgb(16, 255, 255, 255)),
        (1f, Color.FromArgb(44, 0, 0, 0))
    ];

    internal static IReadOnlyList<(float Offset, Color Color)> OrganizerGlassInnerEdgeStops { get; } =
    [
        (0f, Color.FromArgb(64, 255, 255, 255)),
        (.45f, Color.FromArgb(8, 255, 255, 255)),
        (1f, Color.FromArgb(32, 0, 0, 0))
    ];

    internal static IReadOnlyList<(float Offset, Color Color)> GlassEdgePureBaseStops { get; } =
    [
        (0f, Color.FromArgb(150, 255, 255, 255)),
        (.48f, Color.FromArgb(44, 255, 255, 255)),
        (1f, Color.FromArgb(96, 248, 252, 255))
    ];

    internal static IReadOnlyList<(float Offset, Color Color)> GlassEdgeHighlightStops { get; } =
    [
        (0f, Color.FromArgb(245, 255, 255, 255)),
        (.18f, Color.FromArgb(165, 255, 255, 255)),
        (.42f, Color.FromArgb(45, 248, 252, 255)),
        (.70f, Color.FromArgb(0, 255, 255, 255)),
        (1f, Color.FromArgb(0, 255, 255, 255))
    ];

    internal static IReadOnlyList<(float Offset, Color Color)> GlassEdgeCounterArcStops { get; } =
    [
        (0f, Color.FromArgb(155, 246, 251, 255)),
        (.24f, Color.FromArgb(68, 242, 249, 255)),
        (.55f, Color.FromArgb(0, 255, 255, 255)),
        (1f, Color.FromArgb(0, 255, 255, 255))
    ];

    internal static IReadOnlyList<(float Offset, Color Color)> GlassEdgeTextureStops { get; } =
    [
        (0f, Color.FromArgb(88, 255, 255, 255)),
        (.25f, Color.FromArgb(32, 232, 246, 255)),
        (.50f, Color.FromArgb(18, 255, 255, 255)),
        (.75f, Color.FromArgb(36, 244, 240, 255)),
        (1f, Color.FromArgb(72, 255, 255, 255))
    ];

    internal static IReadOnlyList<(float Offset, Color Color)> GlassEdgePointerRimStops { get; } =
    [
        (0f, Color.FromArgb(248, 255, 255, 255)),
        (.28f, Color.FromArgb(148, 235, 247, 255)),
        (.60f, Color.FromArgb(36, 255, 255, 255)),
        (1f, Color.FromArgb(0, 255, 255, 255))
    ];

    internal static Color LayerColor(ThemeValues theme, byte lightAlpha, byte darkAlpha)
    {
        // Surface layers are derived from the selected theme colour instead
        // of fixed white/black overlays so the user's hue stays visible.
        bool dark = IsDark(theme);
        byte layerAlpha = dark ? darkAlpha : lightAlpha;
        float neutralMix = Math.Clamp(layerAlpha / 255f * .75f, .04f, .28f);
        Color selected = SurfaceColor(theme);
        byte target = dark ? (byte)255 : (byte)0;
        return Color.FromArgb(
            layerAlpha,
            BlendChannel(selected.R, target, neutralMix),
            BlendChannel(selected.G, target, neutralMix),
            BlendChannel(selected.B, target, neutralMix));
    }

    private static Color FromArgb(uint argb) => Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    private static byte BlendChannel(byte value, byte target, float amount) =>
        (byte)Math.Clamp(Math.Round(value + (target - value) * amount), 0, 255);

    private static double RelativeLuminance(Color color) =>
        .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);

    private static double Linear(byte channel)
    {
        double value = channel / 255d;
        return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    }
}
