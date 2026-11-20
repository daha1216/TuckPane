using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics.Effects;

namespace TuckPane.Services;

internal enum ThemeBackdropBranch
{
    Opaque,
    Transparent,
    ClearTint,
    Glass,
    Fallback
}

/// <summary>
/// Constructs the actual production effects without a window, compositor or
/// device. This is also the seam for checking source order and tint weight.
/// </summary>
internal static class ThemeEffectGraph
{
    internal static ThemeBackdropBranch ResolveBranch(ThemeCompositionPlan plan, bool hostBackdropAvailable)
    {
        if (plan.TintOpacity >= 1) return ThemeBackdropBranch.Opaque;
        if (plan.RequiresHostBackdrop)
            return hostBackdropAvailable ? ThemeBackdropBranch.Glass : ThemeBackdropBranch.Fallback;
        return plan.TintOpacity <= 0 ? ThemeBackdropBranch.Transparent : ThemeBackdropBranch.ClearTint;
    }

    internal static IGraphicsEffect Create(ThemeCompositionPlan plan, IGraphicsEffectSource backdrop)
    {
        ArgumentNullException.ThrowIfNull(backdrop);
        if (!plan.RequiresHostBackdrop)
            throw new InvalidOperationException("A colour-only theme must bypass the sampled effect graph.");

        // Blur the live backdrop first, then boost chroma vibrancy on the
        // blurred backdrop alone so desktop shapes bloom optically while the
        // user's selected TintColor remains completely faithful.
        var blurredBackdrop = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = plan.BlurAmount,
            BorderMode = EffectBorderMode.Hard,
            Optimization = EffectOptimization.Balanced,
            Source = backdrop
        };

        var saturatedBackdrop = new SaturationEffect
        {
            Name = "Saturation",
            Saturation = Math.Clamp(plan.Saturation, 0f, 2f),
            Source = blurredBackdrop
        };

        var balancedBackdrop = new CompositeEffect { Name = "BalancedBackdrop", Mode = CanvasComposite.SourceOver };
        balancedBackdrop.Sources.Add(saturatedBackdrop);
        balancedBackdrop.Sources.Add(new OpacityEffect
        {
            Name = "NeutralOpacity",
            Opacity = plan.LuminosityOpacity,
            Source = new ColorSourceEffect { Name = "NeutralColor", Color = plan.LuminosityColor }
        });

        // SourceOver: input 0 is the luminous blurred backdrop, input 1 is the
        // user's tint. Keep float opacity on the colour branch so user opacity
        // is applied exactly once with zero double-attenuation.
        var tintedBackdrop = new CompositeEffect { Name = "TintComposite", Mode = CanvasComposite.SourceOver };
        tintedBackdrop.Sources.Add(balancedBackdrop);
        tintedBackdrop.Sources.Add(new OpacityEffect
        {
            Name = "TintOpacity",
            Opacity = plan.TintOpacity,
            Source = new ColorSourceEffect { Name = "TintColor", Color = plan.TintColor }
        });

        return tintedBackdrop;
    }
}
