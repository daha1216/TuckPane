using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using TuckPane.Core;
using TuckPane.Models;
using Windows.UI;

namespace TuckPane.Services;

/// <summary>
/// Dedicated neutral glass edge layer. The overlay host is separate from the
/// backdrop host so later XAML content cannot cover the edge visual.
/// </summary>
internal sealed class ThemeEdgeSurface : IDisposable
{
    private readonly FrameworkElement _host;
    private readonly FrameworkElement _geometryHost;
    private readonly ShapeVisual _visual;
    private readonly ShapeVisual _pointerRimVisual;
    private readonly CompositionRoundedRectangleGeometry _outerGeometry;
    private readonly CompositionRoundedRectangleGeometry _innerGeometry;
    private readonly CompositionRoundedRectangleGeometry _highlightGeometry;
    private readonly CompositionRoundedRectangleGeometry _counterArcGeometry;
    private readonly CompositionRoundedRectangleGeometry _textureGeometry;
    private readonly CompositionRoundedRectangleGeometry _pointerRimGeometry;
    private readonly CompositionSpriteShape _outerShape;
    private readonly CompositionSpriteShape _innerShape;
    private readonly CompositionSpriteShape _highlightShape;
    private readonly CompositionSpriteShape _counterArcShape;
    private readonly CompositionSpriteShape _textureShape;
    private readonly CompositionSpriteShape _pointerRimShape;
    private readonly CompositionLinearGradientBrush _outerBrush;
    private readonly CompositionLinearGradientBrush _innerBrush;
    private readonly CompositionLinearGradientBrush _highlightBrush;
    private readonly CompositionLinearGradientBrush _counterArcBrush;
    private readonly CompositionLinearGradientBrush _textureBrush;
    private readonly CompositionRadialGradientBrush _pointerRimBrush;
    private readonly ImplicitAnimationCollection _pointerRimImplicitAnims;
    private readonly ScalarKeyFrameAnimation _pointerRimOpacityAnim;
    private XamlRoot? _xamlRoot;
    private double _cornerRadius;
    private bool _disposed;

    internal ThemeEdgeSurface(FrameworkElement host, FrameworkElement? geometryHost = null)
    {
        _host = host;
        _geometryHost = geometryHost ?? host;
        Visual hostVisual = ElementCompositionPreview.GetElementVisual(host);
        Compositor compositor = hostVisual.Compositor;
        _visual = compositor.CreateShapeVisual();
        _visual.BorderMode = CompositionBorderMode.Soft;

        _outerGeometry = compositor.CreateRoundedRectangleGeometry();
        _innerGeometry = compositor.CreateRoundedRectangleGeometry();
        _highlightGeometry = compositor.CreateRoundedRectangleGeometry();
        _counterArcGeometry = compositor.CreateRoundedRectangleGeometry();
        _textureGeometry = compositor.CreateRoundedRectangleGeometry();
        _pointerRimGeometry = compositor.CreateRoundedRectangleGeometry();

        _outerShape = compositor.CreateSpriteShape(_outerGeometry);
        _innerShape = compositor.CreateSpriteShape(_innerGeometry);
        _highlightShape = compositor.CreateSpriteShape(_highlightGeometry);
        _counterArcShape = compositor.CreateSpriteShape(_counterArcGeometry);
        _textureShape = compositor.CreateSpriteShape(_textureGeometry);
        _pointerRimShape = compositor.CreateSpriteShape(_pointerRimGeometry);

        _outerBrush = CreateBrush(ThemePalette.OrganizerGlassOuterEdgeStops, new(0f, 0f), new(1f, 1f));
        _innerBrush = CreateBrush(ThemePalette.OrganizerGlassInnerEdgeStops, new(1f, 0f), new(0f, 1f));
        _highlightBrush = CreateBrush(ThemePalette.GlassEdgeHighlightStops, new(0f, 0f), new(0.64f, 0.64f));
        _counterArcBrush = CreateBrush(ThemePalette.GlassEdgeCounterArcStops, new(1f, 1f), new(0.40f, 0.40f));
        _textureBrush = CreateBrush(ThemePalette.GlassEdgeTextureStops, new(0f, 0f), new(1f, 1f));

        _pointerRimBrush = compositor.CreateRadialGradientBrush();
        _pointerRimBrush.MappingMode = CompositionMappingMode.Absolute;
        _pointerRimBrush.EllipseCenter = Vector2.Zero;
        _pointerRimBrush.EllipseRadius = new Vector2(72f, 72f);
        _pointerRimBrush.GradientOriginOffset = Vector2.Zero;
        foreach ((float offset, Color color) in ThemePalette.GlassEdgePointerRimStops)
            _pointerRimBrush.ColorStops.Add(compositor.CreateColorGradientStop(offset, color));

        _outerShape.StrokeBrush = _outerBrush;
        _innerShape.StrokeBrush = _innerBrush;
        _highlightShape.StrokeBrush = _highlightBrush;
        _counterArcShape.StrokeBrush = _counterArcBrush;
        _textureShape.StrokeBrush = _textureBrush;
        _pointerRimShape.StrokeBrush = _pointerRimBrush;

        _outerShape.StrokeThickness = ThemePalette.OrganizerGlassOuterEdgeThicknessDip;
        _innerShape.StrokeThickness = ThemePalette.OrganizerGlassInnerEdgeThicknessDip;
        _highlightShape.StrokeThickness = ThemePalette.GlassEdgeHighlightThicknessDip;
        _counterArcShape.StrokeThickness = ThemePalette.GlassEdgeCounterArcThicknessDip;
        _textureShape.StrokeThickness = ThemePalette.GlassEdgeTextureThicknessDip;
        _pointerRimShape.StrokeThickness = ThemePalette.GlassEdgePointerRimThicknessDip;

        _outerShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _innerShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _highlightShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _counterArcShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _textureShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _pointerRimShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;

        _visual.Shapes.Add(_outerShape);
        _visual.Shapes.Add(_innerShape);
        // Keep the prismatic dispersion rim, bottom-right caustic bounce arc, and
        // primary top-left Fresnel caustic arc above the base rings so their
        // optical gradient stops remain luminous rather than covered by base shapes.
        _visual.Shapes.Add(_textureShape);
        _visual.Shapes.Add(_counterArcShape);
        _visual.Shapes.Add(_highlightShape);
        _visual.Opacity = ThemePalette.GlassEdgeMinimumOpacity;

        _pointerRimOpacityAnim = compositor.CreateScalarKeyFrameAnimation();
        _pointerRimOpacityAnim.Target = "Opacity";
        _pointerRimOpacityAnim.InsertExpressionKeyFrame(1f, "this.FinalValue");
        _pointerRimOpacityAnim.Duration = TimeSpan.FromMilliseconds(180);

        _pointerRimImplicitAnims = compositor.CreateImplicitAnimationCollection();
        _pointerRimImplicitAnims["Opacity"] = _pointerRimOpacityAnim;

        _pointerRimVisual = compositor.CreateShapeVisual();
        _pointerRimVisual.BorderMode = CompositionBorderMode.Soft;
        _pointerRimVisual.Opacity = 0f;
        _pointerRimVisual.Shapes.Add(_pointerRimShape);
        _pointerRimVisual.ImplicitAnimations = _pointerRimImplicitAnims;
        _visual.Children.InsertAtTop(_pointerRimVisual);

        ElementCompositionPreview.SetElementChildVisual(host, _visual);
        _geometryHost.Loaded += Host_Loaded;
        _geometryHost.SizeChanged += Host_SizeChanged;
        AttachXamlRoot(_geometryHost.XamlRoot);
        RefreshGeometry();
    }

    internal void SetTheme(ThemeValues theme, bool useEffects)
    {
        // These brushes intentionally never read theme colour, opacity or
        // blur settings. A non-zero floor keeps the neutral edge visible in
        // transparent, solid-colour, zero-blur and fallback states.
        if (!_disposed) _visual.Opacity = ThemePalette.EdgeOpacity(theme);
    }

    internal void SetEnabled(bool enabled)
    {
        if (!_disposed)
        {
            _visual.IsVisible = enabled;
            if (!enabled && _pointerRimVisual.Opacity > 0.001f)
                _pointerRimVisual.Opacity = 0f;
        }
    }

    internal void UpdatePointerRim(double xDip, double yDip)
    {
        if (_disposed || !_visual.IsVisible) return;
        float radius = Math.Clamp(Math.Min(_visual.Size.X, _visual.Size.Y) * 0.44f, 44f, 180f);
        _pointerRimBrush.EllipseCenter = new Vector2((float)xDip, (float)yDip);
        _pointerRimBrush.EllipseRadius = new Vector2(radius, radius);
        _pointerRimBrush.GradientOriginOffset = Vector2.Zero;
        if (_pointerRimVisual.Opacity < 0.99f)
            _pointerRimVisual.Opacity = 1f;
    }

    internal void ClearPointerRim()
    {
        if (_disposed) return;
        if (_pointerRimVisual.Opacity > 0.001f)
            _pointerRimVisual.Opacity = 0f;
    }

    internal void SetCornerRadius(double radius)
    {
        _cornerRadius = Math.Max(0, radius);
        RefreshGeometry();
    }

    internal void SetRoundedGeometry(RoundedSurfaceGeometry geometry)
    {
        _cornerRadius = geometry.Radius;
        ApplyGeometry(geometry);
    }

    internal void RefreshGeometry()
    {
        if (_disposed) return;
        RoundedSurfaceGeometry surface = RoundedSurfaceGeometry.Create(
            _geometryHost.ActualWidth, _geometryHost.ActualHeight, _cornerRadius, _geometryHost.XamlRoot?.RasterizationScale ?? 1);
        ApplyGeometry(surface);
    }

    private void ApplyGeometry(RoundedSurfaceGeometry surface)
    {
        if (_disposed) return;
        if (surface.Width <= 0 || surface.Height <= 0)
        {
            _visual.Size = Vector2.Zero;
            _pointerRimVisual.Size = Vector2.Zero;
            return;
        }
        Vector2 surfaceSize = new(surface.Width, surface.Height);
        _visual.Size = surfaceSize;
        _pointerRimVisual.Size = surfaceSize;
        ConfigureGeometry(_outerGeometry, surface.InsetStroke(ThemePalette.OrganizerGlassOuterEdgeThicknessDip));
        ConfigureGeometry(_innerGeometry, surface.InsetStroke(
            ThemePalette.OrganizerGlassInnerEdgeThicknessDip, ThemePalette.OrganizerGlassInnerEdgeInsetDip));
        ConfigureGeometry(_textureGeometry, surface.InsetStroke(
            ThemePalette.GlassEdgeTextureThicknessDip, ThemePalette.GlassEdgeTextureInsetDip));
        ConfigureGeometry(_counterArcGeometry, surface.InsetStroke(
            ThemePalette.GlassEdgeCounterArcThicknessDip, ThemePalette.GlassEdgeCounterArcInsetDip));
        ConfigureGeometry(_highlightGeometry, surface.InsetStroke(
            ThemePalette.GlassEdgeHighlightThicknessDip, ThemePalette.GlassEdgeHighlightInsetDip));
        ConfigureGeometry(_pointerRimGeometry, surface.InsetStroke(
            ThemePalette.GlassEdgePointerRimThicknessDip, ThemePalette.GlassEdgePointerRimInsetDip));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _geometryHost.Loaded -= Host_Loaded;
        _geometryHost.SizeChanged -= Host_SizeChanged;
        AttachXamlRoot(null);
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _pointerRimVisual.ImplicitAnimations = null;
        _pointerRimVisual.Shapes.Clear();
        _visual.Children.RemoveAll();
        _visual.Shapes.Clear();
        _pointerRimImplicitAnims.Dispose();
        _pointerRimOpacityAnim.Dispose();
        _pointerRimBrush.Dispose();
        _counterArcBrush.Dispose();
        _outerBrush.Dispose();
        _innerBrush.Dispose();
        _highlightBrush.Dispose();
        _textureBrush.Dispose();
        _pointerRimShape.Dispose();
        _counterArcShape.Dispose();
        _outerShape.Dispose();
        _innerShape.Dispose();
        _highlightShape.Dispose();
        _textureShape.Dispose();
        _pointerRimGeometry.Dispose();
        _counterArcGeometry.Dispose();
        _outerGeometry.Dispose();
        _innerGeometry.Dispose();
        _highlightGeometry.Dispose();
        _textureGeometry.Dispose();
        _pointerRimVisual.Dispose();
        _visual.Dispose();
    }

    private CompositionLinearGradientBrush CreateBrush(
        IReadOnlyList<(float Offset, Color Color)> stops,
        Vector2 start,
        Vector2 end)
    {
        CompositionLinearGradientBrush brush = _visual.Compositor.CreateLinearGradientBrush();
        brush.StartPoint = start;
        brush.EndPoint = end;
        foreach ((float offset, Color color) in stops)
            brush.ColorStops.Add(_visual.Compositor.CreateColorGradientStop(offset, color));
        return brush;
    }

    private void Host_Loaded(object sender, RoutedEventArgs e)
    {
        AttachXamlRoot(_geometryHost.XamlRoot);
        RefreshGeometry();
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshGeometry();

    private void AttachXamlRoot(XamlRoot? next)
    {
        if (ReferenceEquals(_xamlRoot, next)) return;
        if (_xamlRoot is not null) _xamlRoot.Changed -= XamlRoot_Changed;
        _xamlRoot = next;
        if (_xamlRoot is not null) _xamlRoot.Changed += XamlRoot_Changed;
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => RefreshGeometry();

    private static void ConfigureGeometry(
        CompositionRoundedRectangleGeometry geometry,
        RoundedStrokeGeometry stroke)
    {
        geometry.Offset = new Vector2(stroke.Offset);
        geometry.Size = new Vector2(stroke.Width, stroke.Height);
        geometry.CornerRadius = new Vector2(stroke.Radius);
    }
}
