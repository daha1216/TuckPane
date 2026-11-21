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
    private readonly CompositionRoundedRectangleGeometry _meniscusGeometry;
    private readonly CompositionRoundedRectangleGeometry _outerGeometry;
    private readonly CompositionRoundedRectangleGeometry _highlightGeometry;
    private readonly CompositionRoundedRectangleGeometry _counterArcGeometry;
    private readonly CompositionRoundedRectangleGeometry _textureGeometry;
    private readonly CompositionRoundedRectangleGeometry _pointerRimGeometry;
    private readonly CompositionSpriteShape _meniscusShape;
    private readonly CompositionSpriteShape _outerShape;
    private readonly CompositionSpriteShape _highlightShape;
    private readonly CompositionSpriteShape _counterArcShape;
    private readonly CompositionSpriteShape _textureShape;
    private readonly CompositionSpriteShape _pointerRimShape;
    private readonly CompositionLinearGradientBrush _meniscusBrush;
    private readonly CompositionLinearGradientBrush _outerBrush;
    private readonly CompositionLinearGradientBrush _highlightBrush;
    private readonly CompositionLinearGradientBrush _counterArcBrush;
    private readonly CompositionLinearGradientBrush _textureBrush;
    private readonly CompositionRadialGradientBrush _pointerRimBrush;
    private readonly ImplicitAnimationCollection _pointerRimImplicitAnims;
    private readonly ScalarKeyFrameAnimation _pointerRimOpacityAnim;
    private XamlRoot? _xamlRoot;
    private double _cornerRadius;
    private bool _isLightTheme;
    private RoundedSurfaceGeometry _lastSurface;
    private bool _hasLastSurface;
    private bool _disposed;

    internal ThemeEdgeSurface(FrameworkElement host, FrameworkElement? geometryHost = null)
    {
        _host = host;
        _geometryHost = geometryHost ?? host;
        Visual hostVisual = ElementCompositionPreview.GetElementVisual(host);
        Compositor compositor = hostVisual.Compositor;
        _visual = compositor.CreateShapeVisual();
        _visual.BorderMode = CompositionBorderMode.Soft;

        _meniscusGeometry = compositor.CreateRoundedRectangleGeometry();
        _outerGeometry = compositor.CreateRoundedRectangleGeometry();
        _highlightGeometry = compositor.CreateRoundedRectangleGeometry();
        _counterArcGeometry = compositor.CreateRoundedRectangleGeometry();
        _textureGeometry = compositor.CreateRoundedRectangleGeometry();
        _pointerRimGeometry = compositor.CreateRoundedRectangleGeometry();

        _meniscusShape = compositor.CreateSpriteShape(_meniscusGeometry);
        _outerShape = compositor.CreateSpriteShape(_outerGeometry);
        _highlightShape = compositor.CreateSpriteShape(_highlightGeometry);
        _counterArcShape = compositor.CreateSpriteShape(_counterArcGeometry);
        _textureShape = compositor.CreateSpriteShape(_textureGeometry);
        _pointerRimShape = compositor.CreateSpriteShape(_pointerRimGeometry);

        _meniscusBrush = CreateBrush(ThemePalette.GlassEdgeLightMeniscusStops, new(0f, 0f), new(1f, 1f));
        _outerBrush = CreateBrush(ThemePalette.GlassEdgePureBaseStops, new(0f, 0f), new(1f, 1f));
        _highlightBrush = CreateBrush(ThemePalette.GlassEdgeHighlightStops, new(0f, 0f), new(0.52f, 0.52f));
        _counterArcBrush = CreateBrush(ThemePalette.GlassEdgeCounterArcStops, new(1f, 1f), new(0.48f, 0.48f));
        _textureBrush = CreateBrush(ThemePalette.GlassEdgeTextureStops, new(0f, 0f), new(1f, 1f));

        _pointerRimBrush = compositor.CreateRadialGradientBrush();
        _pointerRimBrush.MappingMode = CompositionMappingMode.Absolute;
        _pointerRimBrush.EllipseCenter = Vector2.Zero;
        _pointerRimBrush.EllipseRadius = new Vector2(72f, 72f);
        _pointerRimBrush.GradientOriginOffset = Vector2.Zero;
        foreach ((float offset, Color color) in ThemePalette.GlassEdgePointerRimStops)
            _pointerRimBrush.ColorStops.Add(compositor.CreateColorGradientStop(offset, color));

        _meniscusShape.StrokeBrush = null;
        _outerShape.StrokeBrush = _outerBrush;
        _highlightShape.StrokeBrush = _highlightBrush;
        _counterArcShape.StrokeBrush = _counterArcBrush;
        _textureShape.StrokeBrush = _textureBrush;
        _pointerRimShape.StrokeBrush = _pointerRimBrush;

        _meniscusShape.StrokeThickness = ThemePalette.GlassEdgeLightMeniscusThicknessDip;
        _outerShape.StrokeThickness = ThemePalette.GlassEdgeOuterPureThicknessDip;
        _highlightShape.StrokeThickness = ThemePalette.GlassEdgeHighlightThicknessDip;
        _counterArcShape.StrokeThickness = ThemePalette.GlassEdgeCounterArcThicknessDip;
        _textureShape.StrokeThickness = ThemePalette.GlassEdgeTextureThicknessDip;
        _pointerRimShape.StrokeThickness = ThemePalette.GlassEdgePointerRimThicknessDip;

        _meniscusShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _outerShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _highlightShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _counterArcShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _textureShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        _pointerRimShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;

        // Authoritative single-stack crystal rim: optional light-theme outer crystal meniscus,
        // pure luminous base ring, prismatic dispersion rim, bottom-right caustic bounce arc,
        // and primary top-left Fresnel caustic arc.
        _visual.Shapes.Add(_meniscusShape);
        _visual.Shapes.Add(_outerShape);
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
        if (_disposed) return;
        _visual.Opacity = ThemePalette.EdgeOpacity(theme);
        bool isLight = !ThemePalette.IsDark(theme) && !theme.FullyTransparent;
        if (_isLightTheme != isLight)
        {
            _isLightTheme = isLight;
            _meniscusShape.StrokeBrush = _isLightTheme ? _meniscusBrush : null;
            if (_hasLastSurface) ApplyGeometry(_lastSurface);
        }
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
        float minDim = Math.Min(_visual.Size.X, _visual.Size.Y);
        float radius = Math.Clamp(minDim * 0.48f, 18f, 180f);
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
        _lastSurface = surface;
        _hasLastSurface = true;
        if (surface.Width <= 0 || surface.Height <= 0)
        {
            _visual.Size = Vector2.Zero;
            _pointerRimVisual.Size = Vector2.Zero;
            return;
        }
        Vector2 surfaceSize = new(surface.Width, surface.Height);
        _visual.Size = surfaceSize;
        _pointerRimVisual.Size = surfaceSize;
        float fresnelInset = _isLightTheme ? ThemePalette.GlassEdgeLightInnerInsetDip : 0.0f;
        ConfigureGeometry(_meniscusGeometry, surface.InsetStroke(
            ThemePalette.GlassEdgeLightMeniscusThicknessDip, ThemePalette.GlassEdgeLightMeniscusInsetDip));
        ConfigureGeometry(_outerGeometry, surface.InsetStroke(
            ThemePalette.GlassEdgeOuterPureThicknessDip, fresnelInset));
        ConfigureGeometry(_textureGeometry, surface.InsetStroke(
            ThemePalette.GlassEdgeTextureThicknessDip, fresnelInset + ThemePalette.GlassEdgeTextureInsetDip));
        ConfigureGeometry(_counterArcGeometry, surface.InsetStroke(
            ThemePalette.GlassEdgeCounterArcThicknessDip, fresnelInset + ThemePalette.GlassEdgeCounterArcInsetDip));
        ConfigureGeometry(_highlightGeometry, surface.InsetStroke(
            ThemePalette.GlassEdgeHighlightThicknessDip, fresnelInset + ThemePalette.GlassEdgeHighlightInsetDip));
        ConfigureGeometry(_pointerRimGeometry, surface.InsetStroke(
            ThemePalette.GlassEdgePointerRimThicknessDip, fresnelInset + ThemePalette.GlassEdgePointerRimInsetDip));
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
        _meniscusBrush.Dispose();
        _outerBrush.Dispose();
        _highlightBrush.Dispose();
        _textureBrush.Dispose();
        _pointerRimShape.Dispose();
        _counterArcShape.Dispose();
        _meniscusShape.Dispose();
        _outerShape.Dispose();
        _highlightShape.Dispose();
        _textureShape.Dispose();
        _pointerRimGeometry.Dispose();
        _counterArcGeometry.Dispose();
        _meniscusGeometry.Dispose();
        _outerGeometry.Dispose();
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
