using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using TuckPane.Models;
using TuckPane.Services;
using Color = Windows.UI.Color;

internal static class ThemeOpacityBlurChecks
{
    private const uint Red = 0xFFE02030;

    internal static async Task RunAsync()
    {
        // Inspect the factory used by production. No compositor, window, GPU
        // rendering or desktop input is created by these checks.
        CheckEndpoints();
        CheckSingleOpacity();
        CheckIndependentControls();
        CheckSolidAndFallback();
        await CheckIndependentStateAsync();
        CheckGraphConnections();
        Console.WriteLine("PASS: theme opacity blur arc (6 focused groups; desktop visuals not tested)");
    }

    private static ThemeCompositionPlan Plan(double opacity, double blur, bool effects = true) =>
        ThemePalette.BuildCompositionPlan(new ThemeValues(Red, opacity, blur), effects);

    private static void CheckEndpoints()
    {
        Require(GlobalSettings.NormalizeThemeTransparency(-1) == 0 &&
                GlobalSettings.NormalizeThemeTransparency(.99) == .99 &&
                GlobalSettings.NormalizeThemeTransparency(1) == 1 &&
                GlobalSettings.NormalizeThemeTransparency(2) == 1 &&
                GlobalSettings.NormalizeThemeTransparency(double.NaN) == GlobalSettings.DefaultThemeTransparency &&
                GlobalSettings.NormalizeThemeBlurStrength(-1) == 0 &&
                GlobalSettings.NormalizeThemeBlurStrength(0) == 0 &&
                GlobalSettings.NormalizeThemeBlurStrength(2) == 2 &&
                GlobalSettings.NormalizeThemeBlurStrength(3) == 2,
            "主题范围或端点归一化错误。");
        ThemeCompositionPlan opaque = Plan(1, 2);
        Require(ThemeEffectGraph.ResolveBranch(opaque, true) == ThemeBackdropBranch.Opaque &&
                !opaque.RequiresHostBackdrop && !opaque.UsesGaussianBlur && opaque.TintColor == Color.FromArgb(255, 224, 32, 48),
            "100% 不透明度没有旁路背景处理并保留完整选色。");
        Near(opaque.TintOpacity, 1, "100% 选色权重");
        Near(opaque.DesktopOpacity, 0, "100% 桌面权重");
        ThemeCompositionPlan untinted = Plan(0, 2);
        Require(ThemeEffectGraph.ResolveBranch(untinted, true) == ThemeBackdropBranch.Glass &&
                untinted.RequiresHostBackdrop && untinted.UsesGaussianBlur,
            "0% 不透明度错误关闭了非零模糊。");
        Near(untinted.TintOpacity, 0, "0% 染色权重");
        Near(untinted.SurfaceOpacity, 1, "0% 染色时模糊表面仍应完整输出");
        Require(ThemeEffectGraph.ResolveBranch(Plan(0, 0), true) == ThemeBackdropBranch.Transparent &&
                ThemeEffectGraph.ResolveBranch(Plan(.5, 0), true) == ThemeBackdropBranch.ClearTint,
            "零模糊的透明/清晰颜色端点错误。");
    }

    private static void CheckSingleOpacity()
    {
        ThemeCompositionPlan plan = Plan(.5, 1);
        var backdrop = new ColorSourceEffect { Name = "TestBackdrop", Color = Color.FromArgb(255, 8, 16, 24) };
        var mixed = As<CompositeEffect>(ThemeEffectGraph.Create(plan, backdrop), "最终节点应为 TintComposite，不能再衰减整层 alpha");
        Require(mixed.Name == "TintComposite" && mixed.Mode == CanvasComposite.SourceOver && mixed.Sources.Count == 2,
            "选色必须是两个输入的 SourceOver。");
        var balanced = As<CompositeEffect>(mixed.Sources[0], "选色底层必须为经过模糊、饱和与明暗均衡的背景");
        var saturation = As<SaturationEffect>(balanced.Sources[0], "饱和度增强必须只作用于模糊背景，不能畸变用户选色");
        _ = As<GaussianBlurEffect>(saturation.Source, "必须先对实时背景执行高斯模糊再增强饱和度");
        var tint = As<OpacityEffect>(mixed.Sources[1], "选色必须位于背景之上并单独控制 alpha");
        Near(tint.Opacity, .5, "选色只能使用一次 50% 不透明度");
        Require(As<ColorSourceEffect>(tint.Source, "选色源").Color == plan.TintColor && plan.TintColor.A == 255,
            "不透明度被提前写入选色 alpha，或实际节点没有使用用户选色。");
        Near(plan.SurfaceOpacity, 1, "玻璃整层输出不能再次乘用户不透明度");
    }

    private static void CheckIndependentControls()
    {
        ThemeCompositionPlan low = Plan(.5, .25);
        ThemeCompositionPlan normal = Plan(.5, 1);
        ThemeCompositionPlan high = Plan(.5, 2);
        Near(low.BlurAmount, 4.5, "25% 模糊");
        Near(normal.BlurAmount, 18, "100% 模糊");
        Near(high.BlurAmount, 36, "200% 模糊");
        Near(low.Saturation, 1.75, "25% 模糊饱和度");
        Near(normal.Saturation, 1.75, "100% 模糊饱和度");
        Near(high.Saturation, 1.75, "200% 模糊饱和度");
        Near(normal.LuminosityOpacity, .06, "液态玻璃明暗均衡不透明度");
        Require(low.TintColor == normal.TintColor && normal.TintColor == high.TintColor &&
                low.TintOpacity == normal.TintOpacity && normal.TintOpacity == high.TintOpacity &&
                low.Saturation == normal.Saturation && normal.Saturation == high.Saturation &&
                low.LuminosityOpacity == normal.LuminosityOpacity && normal.LuminosityOpacity == high.LuminosityOpacity &&
                low.LuminosityColor == normal.LuminosityColor && normal.LuminosityColor == high.LuminosityColor &&
                low.HighlightOpacity == normal.HighlightOpacity && normal.HighlightOpacity == high.HighlightOpacity,
            "非零模糊仍联动选色、饱和度、明暗均衡或高光。");
        Near(low.HighlightOpacity, 1, "中间不透明度应保留既有满档高光");
        Near(Plan(.2, 1).BlurAmount, Plan(.8, 1).BlurAmount, "不透明度不能改变模糊半径");
        ThemeCompositionPlan zeroBlur = Plan(.5, 0);
        Near(zeroBlur.BlurAmount, 0, "零模糊必须绕过磨砂处理");
        Near(zeroBlur.Saturation, 1, "零模糊绕过磨砂时饱和度应回落为中性 1");
        Near(zeroBlur.LuminosityOpacity, 0, "零模糊绕过磨砂时明暗均衡应关闭");
    }

    private static void CheckSolidAndFallback()
    {
        ThemeCompositionPlan solid = ThemePalette.BuildCompositionPlan(
            new ThemeValues(Red, .8, 2, SolidColorMode: true, SolidOpacity: .35), true);
        Require(!solid.RequiresHostBackdrop && !solid.UsesGaussianBlur && solid.HighlightOpacity == 0 &&
                ThemeEffectGraph.ResolveBranch(solid, true) == ThemeBackdropBranch.ClearTint,
            "纯色模式仍请求玻璃效果。");
        Near(solid.TintOpacity, .35, "纯色独立不透明度");
        Require(ThemePalette.WithOpacity(solid.TintColor, solid.TintOpacity).A == 89,
            "纯色画刷 alpha 没有遵守独立不透明度。");
        ThemeCompositionPlan disabled = Plan(.5, 1, effects: false);
        Require(!disabled.RequiresHostBackdrop && !disabled.UsesGaussianBlur &&
                ThemeEffectGraph.ResolveBranch(disabled, true) == ThemeBackdropBranch.ClearTint &&
                ThemeEffectGraph.ResolveBranch(Plan(.5, 1), false) == ThemeBackdropBranch.Fallback &&
                ThemePalette.WithOpacity(disabled.TintColor, disabled.TintOpacity).A == 128,
            "效果关闭或背景能力不可用时没有回退到遵守选色不透明度的清晰背景。");
    }

    private static async Task CheckIndependentStateAsync()
    {
        string root = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT") ??
            throw new InvalidOperationException("主题检查必须设置独立 TUCKPANE_TEST_ROOT。");
        string statePath = Path.Combine(Path.GetFullPath(root), "theme-opacity-blur-state.json");
        var settings = new GlobalSettings();
        ThemeValues organizer = new(Red, .99, 0, SolidOpacity: .27);
        ThemeValues initialSettings = new(0xFF2070D0, .43, 1.5, SolidOpacity: .61);
        settings.SetTheme(ThemeTarget.Organizer, organizer);
        settings.SetTheme(ThemeTarget.Settings, initialSettings);
        ThemeValues solid = GlobalSettings.ResolveThemeUpdate(initialSettings,
            initialSettings.ColorArgb, initialSettings.Transparency, initialSettings.BlurStrength, solidColorMode: true);
        Require(solid == (initialSettings with { SolidColorMode = true }),
            "切入纯色模式覆盖了玻璃或纯色的保存参数。");
        solid = GlobalSettings.ResolveThemeUpdate(solid,
            solid.ColorArgb, solid.Transparency, solid.BlurStrength, solidColorMode: true, solidOpacity: .73);
        Require(solid.Transparency == .43 && solid.SolidOpacity == .73 && solid.BlurStrength == 1.5,
            "纯色不透明度修改覆盖了玻璃参数。");
        ThemeValues glass = GlobalSettings.ResolveThemeUpdate(solid,
            solid.ColorArgb, solid.Transparency, solid.BlurStrength, solidColorMode: false);
        Require(glass.Transparency == .43 && glass.SolidOpacity == .73,
            "返回玻璃模式没有恢复原玻璃不透明度。");
        glass = GlobalSettings.ResolveThemeUpdate(glass,
            colorArgb: 0xFF5080B0, transparency: .56, blurStrength: 2, solidColorMode: false);
        Require(glass == new ThemeValues(0xFF5080B0, .56, 2, SolidOpacity: .73),
            "返回玻璃模式或修改玻璃参数覆盖了独立纯色不透明度。");
        ThemeValues restored = GlobalSettings.ResolveThemeUpdate(glass,
            solid.ColorArgb, solid.Transparency, solid.BlurStrength, solid.SolidColorMode, solid.SolidOpacity);
        Require(restored == solid, "保存失败回滚没有完整恢复玻璃和纯色主题快照。");
        ThemeValues savedSettings = GlobalSettings.ResolveThemeUpdate(glass,
            glass.ColorArgb, glass.Transparency, glass.BlurStrength, solidColorMode: true);
        settings.SetTheme(ThemeTarget.Settings, savedSettings);
        Require(settings.GetTheme(ThemeTarget.Organizer) == organizer,
            "设置界面主题修改覆盖了收纳窗主题。");
        var store = new StateStore(statePath);
        await store.SaveAsync(new AppStateV2 { GlobalSettings = settings, Organizers = [] });
        AppStateV2 reloaded = await store.LoadAsync();
        Require(reloaded.GlobalSettings.GetTheme(ThemeTarget.Organizer) == organizer &&
                reloaded.GlobalSettings.GetTheme(ThemeTarget.Settings) == savedSettings,
            "保存/重新加载修改了两套主题、模式或非当前模式参数。");
    }

    private static void CheckGraphConnections()
    {
        var backdrop = new ColorSourceEffect { Name = "TestBackdrop", Color = Color.FromArgb(255, 8, 16, 24) };
        foreach (double opacity in new[] { 0d, .5d })
        foreach ((double blurStrength, double expectedBlur) in new[] { (1d, 18d), (2d, 36d) })
        {
            ThemeCompositionPlan plan = Plan(opacity, blurStrength);
            var mixed = As<CompositeEffect>(ThemeEffectGraph.Create(plan, backdrop), "最终节点必须为 TintComposite");
            Require(mixed.Name == "TintComposite" && mixed.Mode == CanvasComposite.SourceOver && mixed.Sources.Count == 2,
                "选色叠加名称、顺序或节点数量错误。");
            var balanced = As<CompositeEffect>(mixed.Sources[0], "混色背景必须先经过轻度明暗均衡");
            Require(balanced.Name == "BalancedBackdrop" && balanced.Mode == CanvasComposite.SourceOver && balanced.Sources.Count == 2,
                "明暗均衡节点名称、混合模式或输入数量错误。");
            var saturation = As<SaturationEffect>(balanced.Sources[0], "明暗均衡底层必须为背景饱和度增强节点");
            Require(saturation.Name == "Saturation", "饱和度节点名称错误，影响 animatable property 绑定。");
            Near(saturation.Saturation, 1.75, "液态玻璃背景饱和度增强参数");
            var blur = As<GaussianBlurEffect>(saturation.Source, "饱和度输入必须来自实时背景高斯模糊");
            Require(blur.Name == "Blur" &&
                    blur.BorderMode == EffectBorderMode.Hard &&
                    blur.Optimization == EffectOptimization.Balanced &&
                    ReferenceEquals(blur.Source, backdrop),
                "高斯模糊节点名称、边界、优化模式或原背景连接错误。");
            Near(blur.BlurAmount, expectedBlur, $"实际效果图的模糊参数（强度 {blurStrength:0.##}）");
            var gray = As<OpacityEffect>(balanced.Sources[1], "明暗均衡颜色层");
            Require(gray.Name == "NeutralOpacity", "明暗均衡透明度节点名称错误。");
            Near(gray.Opacity, .06, "轻度明暗均衡不透明度");
            var grayColor = As<ColorSourceEffect>(gray.Source, "明暗均衡色源");
            Require(grayColor.Name == "NeutralColor" && grayColor.Color == Color.FromArgb(255, 128, 128, 128),
                "背景均衡不是固定不透明中灰。");
            var tint = As<OpacityEffect>(mixed.Sources[1], "独立选色层");
            Require(tint.Name == "TintOpacity", "选色透明度节点名称错误。");
            Near(tint.Opacity, opacity, "实际选色权重");
            var tintColor = As<ColorSourceEffect>(tint.Source, "选色色源");
            Require(tintColor.Name == "TintColor" && tintColor.Color == plan.TintColor,
                "实际效果图选色错误。");
        }
        bool rejected = false;
        try { ThemeEffectGraph.Create(Plan(1, 2), backdrop); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "完全不透明分支不应创建背景效果图。");
    }

    private static T As<T>(object? value, string message) where T : class =>
        value as T ?? throw new InvalidOperationException(message);

    private static void Near(double actual, double expected, string message) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) < .0001,
            $"{message}：实际 {actual:0.####}，期望 {expected:0.####}。");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
