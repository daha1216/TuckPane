using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using Windows.UI.Text;
using Windows.UI.ViewManagement;

namespace TuckPane.Services;

internal readonly record struct DesktopTypographyMetrics(
    string FontFamilyName,
    double IconFontSizeDip,
    ushort FontWeight);

/// <summary>
/// Queries the authentic Windows 11 desktop icon font (via SPI_GETICONMETRICS at 96 DPI)
/// and caches accessibility high-contrast state so collapsed organizer labels match native
/// Windows desktop icons pixel-for-pixel with zero per-frame COM/WinRT allocations.
/// </summary>
internal static class DesktopTypographyService
{
    private const uint SpiGetIconMetrics = 0x002D;

    private static readonly object Sync = new();
    private static DesktopTypographyMetrics? _cachedMetrics;
    private static FontFamily? _cachedFontFamily;
    private static bool? _cachedHighContrast;

    internal static DesktopTypographyMetrics GetMetrics()
    {
        lock (Sync)
        {
            return _cachedMetrics ??= ReadSystemDesktopTypography();
        }
    }

    internal static FontFamily GetIconFontFamily()
    {
        lock (Sync)
        {
            if (_cachedFontFamily is not null) return _cachedFontFamily;
            DesktopTypographyMetrics metrics = _cachedMetrics ??= ReadSystemDesktopTypography();
            _cachedFontFamily = new FontFamily(metrics.FontFamilyName);
            return _cachedFontFamily;
        }
    }

    internal static bool IsHighContrast()
    {
        lock (Sync)
        {
            if (_cachedHighContrast is bool cached) return cached;
            bool value;
            try { value = new AccessibilitySettings().HighContrast; }
            catch { value = false; }
            _cachedHighContrast = value;
            return value;
        }
    }

    internal static void InvalidateCache()
    {
        lock (Sync)
        {
            _cachedMetrics = null;
            _cachedFontFamily = null;
            _cachedHighContrast = null;
        }
    }

    /// <summary>
    /// Computes crisp, desktop-harmonious label typography for each visual style:
    /// - NativeDesktop / FluentFolder match Windows Explorer's ~12 DIP Regular icon label
    ///   (never inflated to giant 20px billboard text by CompactScale!).
    /// - GlassTile (iOS 26 Liquid Glass) uses a refined, compact-proportioned ~12.5–14.5 DIP Medium label.
    /// </summary>
    internal static (double FontSizeDip, FontWeight Weight) ResolveCompactLabelTypography(
        CompactVisualStyle style,
        double compactScale,
        double compactNameScale,
        bool positioned)
    {
        DesktopTypographyMetrics sys = GetMetrics();
        double baseDip = Math.Clamp(sys.IconFontSizeDip, 11.5, 13.5);
        double safeNameScale = Math.Clamp(compactNameScale, 0.75, 1.45);

        if (CompactVisualStyles.UsesNativeBlendSurface(style))
        {
            // Match native Windows 11 desktop icons (~12 DIP Normal) regardless of tile zoom!
            double nativeSize = Math.Clamp(baseDip * safeNameScale, 11.5, 14.5);
            return (nativeSize, FontWeights.Normal);
        }

        // iOS 26 Liquid Glass: refined, sleek Medium label that gently scales with tile size without bloating.
        double gentleScaleFactor = positioned ? 1.0 : Math.Pow(Math.Clamp(compactScale, 1.0, 2.6), 0.20);
        double glassSize = Math.Clamp((baseDip + 0.3) * gentleScaleFactor * safeNameScale, 12.0, 14.5);
        return (glassSize, FontWeights.Medium);
    }

    internal static DesktopTypographyMetrics ReadSystemDesktopTypography()
    {
        try
        {
            var metrics = new NativeMethods.ICONMETRICS
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.ICONMETRICS>()
            };
            if (NativeMethods.SystemParametersInfoForDpi(SpiGetIconMetrics, metrics.Size, ref metrics, 0, 96))
            {
                string face = string.IsNullOrWhiteSpace(metrics.Font.FaceName)
                    ? "Segoe UI Variable Text"
                    : metrics.Font.FaceName.Trim();
                double sizeDip = metrics.Font.Height != 0
                    ? Math.Clamp(Math.Abs(metrics.Font.Height), 11.5, 14.0)
                    : 12.0;
                ushort weight = metrics.Font.Weight is >= 100 and <= 900
                    ? (ushort)metrics.Font.Weight
                    : (ushort)400;
                return new DesktopTypographyMetrics(face, sizeDip, weight);
            }
        }
        catch
        {
            // Fall through to clean system default.
        }

        return new DesktopTypographyMetrics("Segoe UI Variable Text", 12.0, 400);
    }
}
