using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace TuckPane;

public sealed partial class MainWindow
{
    // Pre-allocated static brushes eliminate all per-hover/per-selection GC & COM overhead.
    private static readonly LinearGradientBrush CompactMultiSelectBgBrush = CreateVerticalGradientBrush(
        Color.FromArgb(0x24, 0x1C, 0x8A, 0xFF),
        Color.FromArgb(0x14, 0x00, 0x66, 0xCC));
    private static readonly LinearGradientBrush CompactMultiSelectBorderBrush = CreateVerticalGradientBrush(
        Color.FromArgb(0xCC, 0x3B, 0x9E, 0xFF),
        Color.FromArgb(0xAA, 0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush CompactNativeHoverBgBrush = new(Color.FromArgb(0x18, 0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush CompactNativeHoverBorderBrush = new(Color.FromArgb(0x66, 0x3B, 0x9E, 0xFF));
    private static readonly SolidColorBrush CompactTransparentPlateBrush = new(Colors.Transparent);

    private static LinearGradientBrush CreateVerticalGradientBrush(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop { Color = top, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = bottom, Offset = 1 });
        return brush;
    }

    private bool _compactPointerHovered;
    private bool _compactPointerPressed;
    private bool _organizerMultiSelected;

    // GPU-composited shadow & iOS 26/27 Liquid Glass live specular visuals (zero per-frame XAML churn).
    private ContainerVisual? _compactNameShadowRoot;
    private SpriteVisual? _compactNameShadowAuraVisual;
    private DropShadow? _compactNameDropShadowAura;
    private SpriteVisual? _compactNameShadowVisual;
    private DropShadow? _compactNameDropShadow;
    private CompositionBrush? _compactNameAlphaMask;
    private string? _compactNameMaskText;
    private double _compactNameMaskFontSize;
    private Vector2 _compactNameMaskSize;
    private bool _compactNameShadowHooked;

    private ContainerVisual? _expandedNameShadowRoot;
    private SpriteVisual? _expandedNameShadowAuraVisual;
    private DropShadow? _expandedNameDropShadowAura;
    private SpriteVisual? _expandedNameShadowVisual;
    private DropShadow? _expandedNameDropShadow;
    private CompositionBrush? _expandedNameAlphaMask;
    private string? _expandedNameMaskText;
    private double _expandedNameMaskFontSize;
    private Vector2 _expandedNameMaskSize;
    private bool _expandedNameAlphaMaskDirty;
    private bool _expandedNameShadowRefreshQueued;
    private bool _expandedNameShadowRefreshMarkDirty;
    private bool _expandedNameShadowHooked;
    private long _expandedNameVisibilityCallbackToken;
    private long _expandedEditorVisibilityCallbackToken;
    private long _expandedViewVisibilityCallbackToken;

    // Pure GPU Liquid Glass diagonal specular refraction sweep driven by DWM ExpressionAnimations.
    private SpriteVisual? _expandedSweepVisual;
    private CompositionLinearGradientBrush? _expandedSweepBrush;
    private CompositionRoundedRectangleGeometry? _expandedSweepClipGeom;
    private CompositionGeometricClip? _expandedSweepClip;
    private ExpressionAnimation? _expandedSweepOpacityExpr;
    private ExpressionAnimation? _expandedSweepOffsetExpr;

    private SpriteVisual? _compactTileShadowVisual;
    private DropShadow? _compactTileDropShadow;
    private CompositionRoundedRectangleGeometry? _compactTileShadowMaskGeom;
    private CompositionSpriteShape? _compactTileShadowMaskShape;
    private CompositionColorBrush? _compactTileShadowMaskFill;
    private ShapeVisual? _compactTileShadowMaskVisual;
    private CompositionVisualSurface? _compactTileShadowMaskSurface;
    private CompositionSurfaceBrush? _compactTileShadowMaskBrush;
    private ImplicitAnimationCollection? _compactTileShadowImplicitAnims;
    private ScalarKeyFrameAnimation? _compactTileShadowBlurAnim;
    private Vector3KeyFrameAnimation? _compactTileShadowOffsetAnim;

    private CompositionRoundedRectangleGeometry? _compactTileShadowGeom;
    private CompositionGeometricClip? _compactTileShadowClip;

    private SpriteVisual? _compactSpecularVisual;
    private CompositionRadialGradientBrush? _compactSpecularBrush;
    private ImplicitAnimationCollection? _compactSpecularImplicitAnims;
    private ScalarKeyFrameAnimation? _compactSpecularOpacityAnim;

    // Reusable DWM Composition spring animations (zero per-hover/per-press WinRT allocations).
    private SpringVector3NaturalMotionAnimation? _tileScaleSpring;
    private SpringVector3NaturalMotionAnimation? _iconScaleSpring;
    private SpringVector3NaturalMotionAnimation? _multiSelectBadgeSpring;

    // Reusable GPU implicit show/hide animations for ExpandedFilterBar & SelectionCountBadge.
    private CompositionAnimationGroup? _overlayShowAnimGroup;
    private CompositionAnimationGroup? _overlayHideAnimGroup;
    private ScalarKeyFrameAnimation? _overlayShowOpacityAnim;
    private Vector3KeyFrameAnimation? _overlayShowTranslationAnim;
    private ScalarKeyFrameAnimation? _overlayHideOpacityAnim;
    private Vector3KeyFrameAnimation? _overlayHideTranslationAnim;
    private CubicBezierEasingFunction? _overlayEaseOut;
    private bool _overlayImplicitAnimsAttached;

    internal bool IsOrganizerMultiSelected => _organizerMultiSelected;

    internal bool IsEligibleForDesktopMultiSelect =>
        !_closing &&
        !_expanded &&
        !IsContained &&
        !IsDock &&
        _definition.PlacementMode is OrganizerPlacementMode.Floating or OrganizerPlacementMode.Positioned &&
        _hwnd != IntPtr.Zero &&
        NativeMethods.IsWindowVisible(_hwnd);

    internal NativeMethods.RECT GetCurrentCompactBounds() => _compactBounds;

    internal WindowAlignmentInsets GetCurrentCompactAlignmentInsets()
    {
        DisplayInfo display = DisplayPlacementService.ForBounds(_compactBounds);
        return CompactPlacementMath.CalculateCompactAlignmentInsets(
            _compactBounds.Width,
            _compactBounds.Height,
            _appliedCompactScale,
            display.Scale);
    }

    internal NativeMethods.RECT GetVisibleCompactFrameRect()
    {
        WindowAlignmentInsets insets = GetCurrentCompactAlignmentInsets();
        return insets.IsValidFor(_compactBounds)
            ? insets.ToFrame(_compactBounds)
            : _compactBounds;
    }

    internal void SetOrganizerMultiSelected(bool selected)
    {
        if (_organizerMultiSelected == selected) return;
        _organizerMultiSelected = selected;
        UpdateCompactSelectionPlateVisual();
        AnimateCompactMultiSelectBadge(selected);
        if (selected)
            PulseCompactTileSelectionSpring();
    }

    internal void ApplyCompactVisualStyle()
    {
        CompactVisualStyle style = CompactVisualStyles.Normalize(_host.State.GlobalSettings.CompactVisualStyle);
        bool highContrast = DesktopTypographyService.IsHighContrast();

        double backdropOpacity = CompactVisualStyles.BackdropOpacity(style);
        bool hideBackdrop = backdropOpacity <= 0.001;
        // Collapsing CompactSurfaceHost when hidden completely shuts down DWM real-time Acrylic blur cost!
        CompactSurfaceHost.Visibility = hideBackdrop ? Visibility.Collapsed : Visibility.Visible;
        CompactSurfaceHost.Opacity = backdropOpacity;

        bool glassStyle = style == CompactVisualStyle.GlassTile;
        bool nativeStyle = style == CompactVisualStyle.NativeDesktop;
        bool folderStyle = CompactVisualStyles.ShowsFolderSilhouette(style);
        bool edgeGlow = _host.State.GlobalSettings.EdgeGlowEnabled && !highContrast;

        CompactLiquidSheen.Visibility = glassStyle && !highContrast ? Visibility.Visible : Visibility.Collapsed;
        CompactLiquidInnerRim.Visibility = glassStyle && edgeGlow ? Visibility.Visible : Visibility.Collapsed;
        CompactLiquidSheen.BorderThickness = edgeGlow ? new Thickness(0.75) : new Thickness(0);
        CompactLiquidSheen.Opacity = edgeGlow ? 1.0 : 0.28;
        CompactNativeBadge.Visibility = nativeStyle ? Visibility.Visible : Visibility.Collapsed;
        CompactNativeInnerRim.Visibility = nativeStyle && edgeGlow ? Visibility.Visible : Visibility.Collapsed;
        CompactEdgeOverlay.Visibility = (nativeStyle || !edgeGlow) ? Visibility.Collapsed : Visibility.Visible;
        CompactEdgeOverlay.Opacity = (nativeStyle || !edgeGlow) ? 0.0 : backdropOpacity;

        CompactFolderTab.Visibility = folderStyle ? Visibility.Visible : Visibility.Collapsed;
        CompactFolderPocket.Visibility = folderStyle ? Visibility.Visible : Visibility.Collapsed;
        double tileSize = Math.Max(24, CompactTile.Width);
        if (folderStyle)
        {
            double tabOverlapOffset = Math.Max(4, Math.Round(tileSize * 0.11));
            CompactFolderTab.Width = Math.Max(16, Math.Round(tileSize * 0.44));
            CompactFolderTab.Height = Math.Max(6, Math.Round(tileSize * 0.14));
            CompactFolderTab.Margin = new Thickness(Math.Max(3, tileSize * 0.08), 0, 0, 0);
            CompactThumbnailHost.Height = Math.Max(20, tileSize - tabOverlapOffset);
            CompactThumbnailHost.VerticalAlignment = VerticalAlignment.Bottom;
            CompactShadowHost.VerticalAlignment = VerticalAlignment.Bottom;
        }
        else
        {
            CompactThumbnailHost.Height = tileSize;
            CompactThumbnailHost.VerticalAlignment = VerticalAlignment.Center;
            CompactShadowHost.VerticalAlignment = VerticalAlignment.Center;
        }
        UpdateCompactPreviewItemScale();

        // Authentic Windows 11 desktop typography & proportional sizing (never giant 20px billboard labels!).
        bool positioned = _definition.PlacementMode == OrganizerPlacementMode.Positioned;
        double compactNameScale = _host.State.GlobalSettings.ResolveCompactNameScale(_definition.PlacementMode);
        (double fontSizeDip, FontWeight fontWeight) = DesktopTypographyService.ResolveCompactLabelTypography(
            style,
            _appliedCompactScale,
            compactNameScale,
            positioned);

        CompactNameText.FontFamily = DesktopTypographyService.GetIconFontFamily();
        CompactNameText.FontWeight = fontWeight;
        CompactNameText.FontSize = fontSizeDip;
        CompactNameText.LineHeight = Math.Round(fontSizeDip * 1.25, 1);

        int maxLines = CompactVisualStyles.LabelMaxLines(style);
        CompactNameText.MaxLines = maxLines;
        CompactNameText.TextWrapping = maxLines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap;
        UpdateCompactLabelSafeWidth();

        // Keep legacy shadow clones permanently collapsed — replaced by true GPU Gaussian DropShadow!
        CompactNameShadow1.Visibility = Visibility.Collapsed;
        CompactNameShadow2.Visibility = Visibility.Collapsed;

        // Honor EdgeGlowEnabled and high-contrast accessibility mode for expanded specular rims.
        ExpandedLiquidSheen.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        ExpandedLiquidInnerRim.Visibility = edgeGlow ? Visibility.Visible : Visibility.Collapsed;
        ExpandedLiquidSheen.BorderThickness = edgeGlow ? new Thickness(0.75) : new Thickness(0);
        ExpandedLiquidSheen.Opacity = edgeGlow ? 1.0 : 0.25;

        EnsureCompactGpuShadowVisuals();
        UpdateCompactNameShadowVisual(style, highContrast);
        UpdateExpandedNameShadowVisual(highContrast);
        UpdateCompactTileShadowVisual(style, highContrast);
        UpdateCompactSelectionPlateVisual();
        UpdateAllRealizedItemNameShadows(highContrast);
    }

    /// <summary>
    /// Computes the on-screen safe label width and minimal edge-avoidance inward shift so tiles
    /// anchored flush against the left or right monitor bezel keep short names dead-center under
    /// the tile while longer names stay fully legible without premature ellipsis or bezel clipping.
    /// </summary>
    internal void UpdateCompactLabelSafeWidth()
    {
        double viewWidth = Math.Max(48, CompactView.Width);
        double fullWidth = Math.Max(48, viewWidth - 4);
        double minSafeWidth = Math.Max(48, CompactTile.Width);
        double safeWidth = fullWidth;
        double shiftX = 0;

        if (_hwnd != IntPtr.Zero &&
            _definition.PlacementMode == OrganizerPlacementMode.Floating &&
            _compactBounds.Width > 0)
        {
            DisplayInfo display = DisplayPlacementService.ForBounds(_compactBounds);
            double scale = Math.Max(1.0, display.Scale);
            double leftOverhangDip = Math.Max(0, (display.Work.Left - _compactBounds.Left) / scale);
            double rightOverhangDip = Math.Max(0, (_compactBounds.Right - display.Work.Right) / scale);
            double visibleLeft = leftOverhangDip + 1.0;
            double visibleRight = viewWidth - rightOverhangDip - 1.0;
            double visibleSpan = visibleRight - visibleLeft;
            if (visibleSpan > 1.0 && (leftOverhangDip > 0.5 || rightOverhangDip > 0.5))
            {
                safeWidth = Math.Clamp(visibleSpan, minSafeWidth, fullWidth);
                double measuredWidth = CompactNameText.ActualWidth > 1
                    ? CompactNameText.ActualWidth
                    : CompactNameText.DesiredSize.Width;
                if (measuredWidth > 1)
                {
                    double clampedTextWidth = Math.Min(measuredWidth, safeWidth);
                    double centerX = viewWidth / 2.0;
                    double naturalLeft = centerX - clampedTextWidth / 2.0;
                    double naturalRight = centerX + clampedTextWidth / 2.0;
                    if (naturalRight > visibleRight)
                        shiftX = visibleRight - naturalRight;
                    else if (naturalLeft < visibleLeft)
                        shiftX = visibleLeft - naturalLeft;
                }
            }
        }

        if (Math.Abs(CompactNameText.MaxWidth - safeWidth) > 0.5)
            CompactNameText.MaxWidth = safeWidth;

        float targetShiftX = (float)Math.Round(shiftX, 1);
        if (Math.Abs(CompactNameContainer.Translation.X - targetShiftX) > 0.25f)
            CompactNameContainer.Translation = new Vector3(targetShiftX, 0f, 0f);

        UpdateCompactSelectionPlateGeometry();
    }

    internal void SyncCompactNameShadow()
    {
        if (_closing) return;
        UpdateCompactLabelSafeWidth();
        CompactVisualStyle style = CompactVisualStyles.Normalize(_host.State.GlobalSettings.CompactVisualStyle);
        UpdateCompactNameShadowVisual(style, DesktopTypographyService.IsHighContrast());
    }

    internal void SyncExpandedNameShadow()
    {
        if (_closing) return;
        UpdateExpandedNameShadowVisual(DesktopTypographyService.IsHighContrast());
    }

    private void CompactNameText_SizeChanged(object sender, SizeChangedEventArgs e) => SyncCompactNameShadow();

    private void ExpandedNameText_SizeChanged(object sender, SizeChangedEventArgs e) => SyncExpandedNameShadow();

    private void ExpandedNameText_VisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        _expandedNameAlphaMaskDirty = true;
        SyncExpandedNameShadow();
        if (!_closing && ExpandedView.Visibility == Visibility.Visible)
        {
            UpdateAllRealizedItemNameShadows(DesktopTypographyService.IsHighContrast());
            QueueExpandedNameShadowRefresh(markMaskDirty: true);
        }
    }

    private void QueueExpandedNameShadowRefresh(bool markMaskDirty = false)
    {
        if (_closing) return;
        if (markMaskDirty)
            _expandedNameShadowRefreshMarkDirty = true;
        if (_expandedNameShadowRefreshQueued) return;
        _expandedNameShadowRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _expandedNameShadowRefreshQueued = false;
                if (_closing)
                {
                    _expandedNameShadowRefreshMarkDirty = false;
                    return;
                }
                if (_expandedNameShadowRefreshMarkDirty)
                {
                    _expandedNameShadowRefreshMarkDirty = false;
                    _expandedNameAlphaMaskDirty = true;
                }
                SyncExpandedNameShadow();
            }))
        {
            _expandedNameShadowRefreshQueued = false;
            _expandedNameShadowRefreshMarkDirty = false;
        }
    }

    internal void UpdateCompactShadowGeometry(double compactRadius)
    {
        CornerRadius radius = new(compactRadius);
        CornerRadius innerRadius = new(Math.Max(0, compactRadius - 1));
        CompactLiquidSheen.CornerRadius = radius;
        CompactLiquidInnerRim.CornerRadius = innerRadius;
        CompactNativeBadge.CornerRadius = radius;
        CompactNativeInnerRim.CornerRadius = innerRadius;
        CompactFolderPocket.CornerRadius = new CornerRadius(Math.Max(6, compactRadius * 0.85));

        double w = CompactThumbnailHost.ActualWidth > 1 ? CompactThumbnailHost.ActualWidth : CompactThumbnailHost.Width;
        double h = CompactThumbnailHost.ActualHeight > 1 ? CompactThumbnailHost.ActualHeight : CompactThumbnailHost.Height;
        CompactShadowHost.Width = w;
        CompactShadowHost.Height = h;

        if (w > 1 && h > 1)
        {
            Vector2 tileSize = new((float)w, (float)h);
            Vector2 cornerVec = new((float)compactRadius, (float)compactRadius);

            if (_compactTileShadowVisual is not null)
                _compactTileShadowVisual.Size = tileSize;

            if (_compactTileShadowMaskGeom is not null &&
                _compactTileShadowMaskVisual is not null &&
                _compactTileShadowMaskSurface is not null)
            {
                _compactTileShadowMaskGeom.Size = tileSize;
                _compactTileShadowMaskGeom.CornerRadius = cornerVec;
                _compactTileShadowMaskVisual.Size = tileSize;
                _compactTileShadowMaskSurface.SourceSize = tileSize;
            }

            if (_compactSpecularVisual is not null)
            {
                _compactSpecularVisual.Size = tileSize;
                if (_compactTileShadowGeom is not null)
                {
                    _compactTileShadowGeom.Size = tileSize;
                    _compactTileShadowGeom.CornerRadius = cornerVec;
                }
            }
        }

        UpdateCompactSelectionPlateGeometry(compactRadius);
    }

    internal void UpdateExpandedLiquidSheenGeometry(double expandedRadius)
    {
        double clamped = Math.Max(0, expandedRadius);
        var outerRadius = new CornerRadius(clamped);
        var innerRadius = new CornerRadius(Math.Max(0, clamped - 1));
        if (ExpandedLiquidSheen.CornerRadius != outerRadius)
            ExpandedLiquidSheen.CornerRadius = outerRadius;
        if (ExpandedLiquidInnerRim.CornerRadius != innerRadius)
            ExpandedLiquidInnerRim.CornerRadius = innerRadius;

        if (_expandedSweepVisual is not null)
        {
            double w = ExpandedPanel.ActualWidth > 1 ? ExpandedPanel.ActualWidth : ExpandedView.ActualWidth;
            double h = ExpandedPanel.ActualHeight > 1 ? ExpandedPanel.ActualHeight : ExpandedView.ActualHeight;
            SyncExpandedSweepGeometry(w, h, clamped);
        }
        if (ExpandedView.Visibility == Visibility.Visible)
        {
            _expandedNameAlphaMaskDirty = true;
            SyncExpandedNameShadow();
            QueueExpandedNameShadowRefresh(markMaskDirty: true);
        }
    }

    private void EnsureExpandedSpecularSweepVisual()
    {
        if (_expandedSweepVisual is not null || !WindowRoot.IsLoaded || _closing) return;

        Visual sweepHostVisual = ElementCompositionPreview.GetElementVisual(ExpandedSpecularSweep);
        Compositor compositor = sweepHostVisual.Compositor;

        _expandedSweepBrush = compositor.CreateLinearGradientBrush();
        _expandedSweepBrush.MappingMode = CompositionMappingMode.Relative;
        _expandedSweepBrush.ExtendMode = CompositionGradientExtendMode.Clamp;
        _expandedSweepBrush.StartPoint = new Vector2(0f, 0f);
        _expandedSweepBrush.EndPoint = new Vector2(1f, 0.35f);
        _expandedSweepBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Color.FromArgb(0, 255, 255, 255)));
        _expandedSweepBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.28f, Color.FromArgb(0x48, 255, 255, 255)));
        _expandedSweepBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.50f, Color.FromArgb(0x72, 255, 255, 255)));
        _expandedSweepBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.72f, Color.FromArgb(0x32, 255, 255, 255)));
        _expandedSweepBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));

        _expandedSweepClipGeom = compositor.CreateRoundedRectangleGeometry();
        _expandedSweepClip = compositor.CreateGeometricClip(_expandedSweepClipGeom);

        _expandedSweepVisual = compositor.CreateSpriteVisual();
        _expandedSweepVisual.BorderMode = CompositionBorderMode.Soft;
        _expandedSweepVisual.Brush = _expandedSweepBrush;
        _expandedSweepVisual.Clip = _expandedSweepClip;
        _expandedSweepVisual.Opacity = 0f;
        _expandedSweepVisual.IsVisible = false;
        ElementCompositionPreview.SetElementChildVisual(ExpandedSpecularSweep, _expandedSweepVisual);
    }

    private void SyncExpandedSweepGeometry(double width, double height, double radius)
    {
        if (_expandedSweepVisual is null || width <= 1 || height <= 1) return;

        RoundedSurfaceGeometry surface = RoundedSurfaceGeometry.Create(
            width,
            height,
            radius,
            WindowRoot.XamlRoot?.RasterizationScale ?? 1);
        Vector2 panelSize = new(surface.Width, surface.Height);
        Vector2 cornerVec = new(surface.Radius, surface.Radius);
        _expandedSweepVisual.Size = panelSize;
        if (_expandedSweepClipGeom is not null)
        {
            _expandedSweepClipGeom.Size = panelSize;
            _expandedSweepClipGeom.CornerRadius = cornerVec;
        }
    }

    internal void BindExpandedTransitionSweep(CompositionPropertySet transitionProps)
    {
        if (_closing || !WindowRoot.IsLoaded) return;
        _expandedNameAlphaMaskDirty = true;
        SyncExpandedNameShadow();
        if (DesktopTypographyService.IsHighContrast() || !UseCustomAnimations)
        {
            ClearExpandedTransitionSweep();
            return;
        }

        EnsureExpandedSpecularSweepVisual();
        if (_expandedSweepVisual is null || _expandedSweepBrush is null) return;

        double w = ExpandedPanel.ActualWidth > 1 ? ExpandedPanel.ActualWidth : Math.Max(1, ExpandedView.ActualWidth);
        double h = ExpandedPanel.ActualHeight > 1 ? ExpandedPanel.ActualHeight : Math.Max(1, ExpandedView.ActualHeight);
        double radius = _lastExpandedClipRadius > 0
            ? _lastExpandedClipRadius
            : (IsDock ? CurrentDockCornerRadius : SnapDip(ExpandedCornerRadiusDip));
        SyncExpandedSweepGeometry(w, h, radius);

        Compositor compositor = _expandedSweepVisual.Compositor;
        _expandedSweepOpacityExpr ??= compositor.CreateExpressionAnimation(
            "Sin(Clamp((props.Progress - 0.08) / 0.84, 0.0, 1.0) * 3.14159) * 0.72");
        _expandedSweepOpacityExpr.SetReferenceParameter("props", transitionProps);

        _expandedSweepOffsetExpr ??= compositor.CreateExpressionAnimation(
            "Vector2((-0.85 * visual.Size.X) + (1.90 * visual.Size.X) * Clamp(props.Progress, 0.0, 1.0), 0.0)");
        _expandedSweepOffsetExpr.SetReferenceParameter("props", transitionProps);
        _expandedSweepOffsetExpr.SetReferenceParameter("visual", _expandedSweepVisual);

        _expandedSweepVisual.IsVisible = true;
        _expandedSweepVisual.StartAnimation("Opacity", _expandedSweepOpacityExpr);
        _expandedSweepBrush.StartAnimation("Offset", _expandedSweepOffsetExpr);
    }

    internal void ClearExpandedTransitionSweep()
    {
        if (_expandedSweepVisual is not null)
        {
            _expandedSweepVisual.StopAnimation("Opacity");
            _expandedSweepVisual.Opacity = 0f;
            _expandedSweepVisual.IsVisible = false;
        }
        if (_expandedSweepBrush is not null)
        {
            _expandedSweepBrush.StopAnimation("Offset");
            _expandedSweepBrush.Offset = Vector2.Zero;
        }
    }

    private void EnsureCompactGpuShadowVisuals()
    {
        if (!WindowRoot.IsLoaded || _closing) return;

        // 1. Dual-Layer Hardware Gaussian DropShadow on CompactNameText using GetAlphaMask():
        //    - Tight Windows 11 DrawShadowText core (BlurRadius 1.15, Offset +1.0, full black)
        //      + crisp contact halo (BlurRadius 2.25) for sharp native contrast on pure white wallpaper without gray fog!
        if (_compactNameShadowRoot is null)
        {
            Visual hostVisual = ElementCompositionPreview.GetElementVisual(CompactNameShadowHost);
            Compositor compositor = hostVisual.Compositor;

            _compactNameDropShadowAura = compositor.CreateDropShadow();
            _compactNameDropShadowAura.Color = Color.FromArgb(245, 0, 0, 0);
            _compactNameDropShadowAura.BlurRadius = 2.25f;
            _compactNameDropShadowAura.Offset = new Vector3(0f, 0.9f, 0f);

            _compactNameShadowAuraVisual = compositor.CreateSpriteVisual();
            _compactNameShadowAuraVisual.Shadow = _compactNameDropShadowAura;

            _compactNameDropShadow = compositor.CreateDropShadow();
            _compactNameDropShadow.Color = Color.FromArgb(255, 0, 0, 0);
            _compactNameDropShadow.BlurRadius = 1.15f;
            _compactNameDropShadow.Offset = new Vector3(0f, 1.0f, 0f);

            _compactNameShadowVisual = compositor.CreateSpriteVisual();
            _compactNameShadowVisual.Shadow = _compactNameDropShadow;

            if (CompactView.Visibility == Visibility.Visible &&
                CompactNameText.ActualWidth > 1 &&
                CompactNameText.ActualHeight > 1)
            {
                _compactNameAlphaMask = CompactNameText.GetAlphaMask();
                _compactNameDropShadowAura.Mask = _compactNameAlphaMask;
                _compactNameDropShadow.Mask = _compactNameAlphaMask;
            }

            _compactNameShadowRoot = compositor.CreateContainerVisual();
            _compactNameShadowRoot.IsVisible = false;
            _compactNameShadowRoot.Children.InsertAtBottom(_compactNameShadowAuraVisual);
            _compactNameShadowRoot.Children.InsertAtTop(_compactNameShadowVisual);
            ElementCompositionPreview.SetElementChildVisual(CompactNameShadowHost, _compactNameShadowRoot);
        }
        if (!_compactNameShadowHooked)
        {
            _compactNameShadowHooked = true;
            CompactNameText.SizeChanged += CompactNameText_SizeChanged;
        }

        // 1b. Dual-Layer Hardware Gaussian DropShadow on ExpandedNameText using GetAlphaMask():
        //     Ensures razor-sharp legibility of 42px white expanded titles over pure #FFFFFF white wallpaper.
        //     CRITICAL: Never call ExpandedNameText.GetAlphaMask() while ExpandedView is Collapsed (0x0 unmeasured);
        //     bind it lazily inside UpdateExpandedNameShadowVisual once ActualWidth > 1 && ActualHeight > 1.
        if (_expandedNameShadowRoot is null)
        {
            Visual expandedHostVisual = ElementCompositionPreview.GetElementVisual(ExpandedNameShadowHost);
            Compositor compositor = expandedHostVisual.Compositor;

            _expandedNameDropShadowAura = compositor.CreateDropShadow();
            _expandedNameDropShadowAura.Color = Color.FromArgb(235, 0, 0, 0);
            _expandedNameDropShadowAura.BlurRadius = 9.0f;
            _expandedNameDropShadowAura.Offset = new Vector3(0f, 2.5f, 0f);
            _expandedNameDropShadowAura.Opacity = 0.92f;

            _expandedNameShadowAuraVisual = compositor.CreateSpriteVisual();
            _expandedNameShadowAuraVisual.Shadow = _expandedNameDropShadowAura;

            _expandedNameDropShadow = compositor.CreateDropShadow();
            _expandedNameDropShadow.Color = Color.FromArgb(255, 0, 0, 0);
            _expandedNameDropShadow.BlurRadius = 2.6f;
            _expandedNameDropShadow.Offset = new Vector3(0f, 1.0f, 0f);
            _expandedNameDropShadow.Opacity = 1.0f;

            _expandedNameShadowVisual = compositor.CreateSpriteVisual();
            _expandedNameShadowVisual.Shadow = _expandedNameDropShadow;

            _expandedNameShadowRoot = compositor.CreateContainerVisual();
            _expandedNameShadowRoot.IsVisible = false;
            _expandedNameShadowRoot.Children.InsertAtBottom(_expandedNameShadowAuraVisual);
            _expandedNameShadowRoot.Children.InsertAtTop(_expandedNameShadowVisual);
            ElementCompositionPreview.SetElementChildVisual(ExpandedNameShadowHost, _expandedNameShadowRoot);
        }
        if (!_expandedNameShadowHooked)
        {
            _expandedNameShadowHooked = true;
            ExpandedNameText.SizeChanged += ExpandedNameText_SizeChanged;
            _expandedNameVisibilityCallbackToken = ExpandedNameText.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, ExpandedNameText_VisibilityChanged);
            _expandedEditorVisibilityCallbackToken = ExpandedNameEditor.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, ExpandedNameText_VisibilityChanged);
            _expandedViewVisibilityCallbackToken = ExpandedView.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, ExpandedNameText_VisibilityChanged);
        }

        // 2. Sculptural floating ambient DropShadow beneath CompactThumbnailHost with exact
        //    rounded-rectangle alpha mask (zero square shadow ears!) + smooth GPU implicit bloom.
        if (_compactTileShadowVisual is null)
        {
            Visual shadowHostVisual = ElementCompositionPreview.GetElementVisual(CompactShadowHost);
            Compositor compositor = shadowHostVisual.Compositor;

            double initW = Math.Max(1, CompactThumbnailHost.ActualWidth > 1 ? CompactThumbnailHost.ActualWidth : CompactThumbnailHost.Width);
            double initH = Math.Max(1, CompactThumbnailHost.ActualHeight > 1 ? CompactThumbnailHost.ActualHeight : CompactThumbnailHost.Height);
            float initRadius = (float)(CompactCornerRadiusDip * _appliedCompactScale);
            Vector2 initSize = new((float)initW, (float)initH);

            _compactTileShadowMaskGeom = compositor.CreateRoundedRectangleGeometry();
            _compactTileShadowMaskGeom.Size = initSize;
            _compactTileShadowMaskGeom.CornerRadius = new Vector2(initRadius, initRadius);

            _compactTileShadowMaskFill = compositor.CreateColorBrush(Colors.White);
            _compactTileShadowMaskShape = compositor.CreateSpriteShape(_compactTileShadowMaskGeom);
            _compactTileShadowMaskShape.FillBrush = _compactTileShadowMaskFill;

            _compactTileShadowMaskVisual = compositor.CreateShapeVisual();
            _compactTileShadowMaskVisual.Size = initSize;
            _compactTileShadowMaskVisual.Shapes.Add(_compactTileShadowMaskShape);

            _compactTileShadowMaskSurface = compositor.CreateVisualSurface();
            _compactTileShadowMaskSurface.SourceVisual = _compactTileShadowMaskVisual;
            _compactTileShadowMaskSurface.SourceSize = initSize;

            _compactTileShadowMaskBrush = compositor.CreateSurfaceBrush(_compactTileShadowMaskSurface);
            _compactTileShadowMaskBrush.Stretch = CompositionStretch.Fill;

            _compactTileDropShadow = compositor.CreateDropShadow();
            _compactTileDropShadow.Color = Color.FromArgb(54, 0, 0, 0);
            _compactTileDropShadow.BlurRadius = 16f;
            _compactTileDropShadow.Offset = new Vector3(0f, 4.2f, 0f);
            _compactTileDropShadow.Mask = _compactTileShadowMaskBrush;

            _compactTileShadowBlurAnim = compositor.CreateScalarKeyFrameAnimation();
            _compactTileShadowBlurAnim.Target = "BlurRadius";
            _compactTileShadowBlurAnim.InsertExpressionKeyFrame(1f, "this.FinalValue");
            _compactTileShadowBlurAnim.Duration = TimeSpan.FromMilliseconds(180);

            _compactTileShadowOffsetAnim = compositor.CreateVector3KeyFrameAnimation();
            _compactTileShadowOffsetAnim.Target = "Offset";
            _compactTileShadowOffsetAnim.InsertExpressionKeyFrame(1f, "this.FinalValue");
            _compactTileShadowOffsetAnim.Duration = TimeSpan.FromMilliseconds(180);

            _compactTileShadowImplicitAnims = compositor.CreateImplicitAnimationCollection();
            _compactTileShadowImplicitAnims["BlurRadius"] = _compactTileShadowBlurAnim;
            _compactTileShadowImplicitAnims["Offset"] = _compactTileShadowOffsetAnim;
            _compactTileDropShadow.ImplicitAnimations = _compactTileShadowImplicitAnims;

            _compactTileShadowVisual = compositor.CreateSpriteVisual();
            _compactTileShadowVisual.Size = initSize;
            _compactTileShadowVisual.Shadow = _compactTileDropShadow;
            ElementCompositionPreview.SetElementChildVisual(CompactShadowHost, _compactTileShadowVisual);
        }

        // 3. iOS 26/27 Liquid Glass live cursor-tracking specular catchlight (pure GPU CompositionRadialGradientBrush + 160ms implicit bloom!).
        if (_compactSpecularVisual is null)
        {
            Visual specHostVisual = ElementCompositionPreview.GetElementVisual(CompactSpecularOverlay);
            Compositor compositor = specHostVisual.Compositor;

            _compactSpecularBrush = compositor.CreateRadialGradientBrush();
            _compactSpecularBrush.MappingMode = CompositionMappingMode.Relative;
            _compactSpecularBrush.EllipseCenter = new Vector2(0.25f, 0.2f);
            _compactSpecularBrush.EllipseRadius = new Vector2(0.65f, 0.65f);
            _compactSpecularBrush.ColorStops.Add(compositor.CreateColorGradientStop(0f, Color.FromArgb(96, 255, 255, 255)));
            _compactSpecularBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.55f, Color.FromArgb(26, 255, 255, 255)));
            _compactSpecularBrush.ColorStops.Add(compositor.CreateColorGradientStop(1f, Color.FromArgb(0, 255, 255, 255)));

            _compactTileShadowGeom = compositor.CreateRoundedRectangleGeometry();
            _compactTileShadowClip = compositor.CreateGeometricClip(_compactTileShadowGeom);

            _compactSpecularOpacityAnim = compositor.CreateScalarKeyFrameAnimation();
            _compactSpecularOpacityAnim.Target = "Opacity";
            _compactSpecularOpacityAnim.InsertExpressionKeyFrame(1f, "this.FinalValue");
            _compactSpecularOpacityAnim.Duration = TimeSpan.FromMilliseconds(160);

            _compactSpecularImplicitAnims = compositor.CreateImplicitAnimationCollection();
            _compactSpecularImplicitAnims["Opacity"] = _compactSpecularOpacityAnim;

            _compactSpecularVisual = compositor.CreateSpriteVisual();
            _compactSpecularVisual.Brush = _compactSpecularBrush;
            _compactSpecularVisual.Clip = _compactTileShadowClip;
            _compactSpecularVisual.Opacity = 0f;
            _compactSpecularVisual.ImplicitAnimations = _compactSpecularImplicitAnims;
            ElementCompositionPreview.SetElementChildVisual(CompactSpecularOverlay, _compactSpecularVisual);
        }

        // 4. GPU ImplicitShowAnimation / ImplicitHideAnimation on ExpandedFilterBar & SelectionCountBadge.
        if (!_overlayImplicitAnimsAttached)
        {
            _overlayImplicitAnimsAttached = true;
            Visual filterVisual = ElementCompositionPreview.GetElementVisual(ExpandedFilterBar);
            Compositor compositor = filterVisual.Compositor;
            ElementCompositionPreview.SetIsTranslationEnabled(ExpandedFilterBar, true);
            ElementCompositionPreview.SetIsTranslationEnabled(SelectionCountBadge, true);

            _overlayEaseOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

            _overlayShowOpacityAnim = compositor.CreateScalarKeyFrameAnimation();
            _overlayShowOpacityAnim.Target = "Opacity";
            _overlayShowOpacityAnim.InsertKeyFrame(0f, 0f);
            _overlayShowOpacityAnim.InsertKeyFrame(1f, 1f, _overlayEaseOut);
            _overlayShowOpacityAnim.Duration = TimeSpan.FromMilliseconds(160);

            _overlayShowTranslationAnim = compositor.CreateVector3KeyFrameAnimation();
            _overlayShowTranslationAnim.Target = "Translation";
            _overlayShowTranslationAnim.InsertKeyFrame(0f, new Vector3(0f, -8f, 0f));
            _overlayShowTranslationAnim.InsertKeyFrame(1f, Vector3.Zero, _overlayEaseOut);
            _overlayShowTranslationAnim.Duration = TimeSpan.FromMilliseconds(180);

            _overlayShowAnimGroup = compositor.CreateAnimationGroup();
            _overlayShowAnimGroup.Add(_overlayShowOpacityAnim);
            _overlayShowAnimGroup.Add(_overlayShowTranslationAnim);

            _overlayHideOpacityAnim = compositor.CreateScalarKeyFrameAnimation();
            _overlayHideOpacityAnim.Target = "Opacity";
            _overlayHideOpacityAnim.InsertKeyFrame(1f, 0f);
            _overlayHideOpacityAnim.Duration = TimeSpan.FromMilliseconds(120);

            _overlayHideTranslationAnim = compositor.CreateVector3KeyFrameAnimation();
            _overlayHideTranslationAnim.Target = "Translation";
            _overlayHideTranslationAnim.InsertKeyFrame(1f, new Vector3(0f, -6f, 0f));
            _overlayHideTranslationAnim.Duration = TimeSpan.FromMilliseconds(120);

            _overlayHideAnimGroup = compositor.CreateAnimationGroup();
            _overlayHideAnimGroup.Add(_overlayHideOpacityAnim);
            _overlayHideAnimGroup.Add(_overlayHideTranslationAnim);

            ElementCompositionPreview.SetImplicitShowAnimation(ExpandedFilterBar, _overlayShowAnimGroup);
            ElementCompositionPreview.SetImplicitHideAnimation(ExpandedFilterBar, _overlayHideAnimGroup);
            ElementCompositionPreview.SetImplicitShowAnimation(SelectionCountBadge, _overlayShowAnimGroup);
            ElementCompositionPreview.SetImplicitHideAnimation(SelectionCountBadge, _overlayHideAnimGroup);
        }
    }

    private void UpdateCompactNameShadowVisual(CompactVisualStyle style, bool highContrast)
    {
        if (_compactNameShadowRoot is null || _compactNameShadowVisual is null || _compactNameDropShadow is null ||
            _compactNameShadowAuraVisual is null || _compactNameDropShadowAura is null)
        {
            return;
        }

        bool visible = CompactView.Visibility == Visibility.Visible &&
                       CompactNameText.Visibility == Visibility.Visible &&
                       !string.IsNullOrEmpty(CompactNameText.Text) &&
                       !highContrast;
        if (!visible)
        {
            _compactNameShadowRoot.IsVisible = false;
            return;
        }

        double width = CompactNameText.ActualWidth > 0 ? CompactNameText.ActualWidth : CompactNameText.DesiredSize.Width;
        double height = CompactNameText.ActualHeight > 0 ? CompactNameText.ActualHeight : CompactNameText.DesiredSize.Height;
        if (width <= 1 || height <= 1)
        {
            _compactNameShadowRoot.IsVisible = false;
            return;
        }

        Vector2 size = new((float)width, (float)height);
        CompactNameShadowHost.Width = size.X;
        CompactNameShadowHost.Height = size.Y;
        _compactNameShadowRoot.Size = size;
        _compactNameShadowVisual.Size = size;
        _compactNameShadowAuraVisual.Size = size;

        float opacity = (float)Math.Clamp(CompactVisualStyles.LabelShadowOpacity(style, highContrast), 0.0, 1.0);
        string text = CompactNameText.Text;
        double fontSize = CompactNameText.FontSize;
        bool contentChanged = !string.Equals(_compactNameMaskText, text, StringComparison.Ordinal) ||
                              Math.Abs(_compactNameMaskFontSize - fontSize) > 0.01 ||
                              Math.Abs(_compactNameMaskSize.X - size.X) > 0.25f ||
                              Math.Abs(_compactNameMaskSize.Y - size.Y) > 0.25f;
        if (_compactNameAlphaMask is null || contentChanged)
        {
            // XAML owns TextBlock.GetAlphaMask() brushes; rebind without calling Dispose().
            _compactNameAlphaMask = CompactNameText.GetAlphaMask();
            _compactNameDropShadow.Mask = _compactNameAlphaMask;
            _compactNameDropShadowAura.Mask = _compactNameAlphaMask;
            _compactNameMaskText = text;
            _compactNameMaskFontSize = fontSize;
            _compactNameMaskSize = size;
        }
        _compactNameDropShadow.Opacity = opacity;
        _compactNameDropShadowAura.Opacity = opacity * 0.95f;
        _compactNameShadowRoot.IsVisible = true;
    }

    private void UpdateExpandedNameShadowVisual(bool highContrast)
    {
        if (_expandedNameShadowRoot is null && WindowRoot.IsLoaded && !_closing)
            EnsureCompactGpuShadowVisuals();

        if (_expandedNameShadowRoot is null || _expandedNameShadowVisual is null || _expandedNameDropShadow is null ||
            _expandedNameShadowAuraVisual is null || _expandedNameDropShadowAura is null)
        {
            return;
        }

        bool visible = ExpandedView.Visibility == Visibility.Visible &&
                       ExpandedNameText.Visibility == Visibility.Visible &&
                       ExpandedNameEditor.Visibility != Visibility.Visible &&
                       !string.IsNullOrEmpty(ExpandedNameText.Text) &&
                       !highContrast;
        if (!visible)
        {
            _expandedNameShadowRoot.IsVisible = false;
            _expandedNameAlphaMaskDirty = true;
            return;
        }

        Thickness margin = ExpandedNameText.Margin;
        double width = ExpandedNameText.ActualWidth > 1
            ? ExpandedNameText.ActualWidth
            : Math.Max(0, ExpandedNameText.DesiredSize.Width - margin.Left - margin.Right);
        double height = ExpandedNameText.ActualHeight > 1
            ? ExpandedNameText.ActualHeight
            : Math.Max(0, ExpandedNameText.DesiredSize.Height - margin.Top - margin.Bottom);
        if (width <= 1 || height <= 1)
        {
            _expandedNameShadowRoot.IsVisible = false;
            QueueExpandedNameShadowRefresh(markMaskDirty: true);
            return;
        }

        Vector2 size = new((float)width, (float)height);
        _expandedNameShadowRoot.Size = size;
        _expandedNameShadowVisual.Size = size;
        _expandedNameShadowAuraVisual.Size = size;

        string text = ExpandedNameText.Text;
        double fontSize = ExpandedNameText.FontSize;
        bool contentChanged = !string.Equals(_expandedNameMaskText, text, StringComparison.Ordinal) ||
                              Math.Abs(_expandedNameMaskFontSize - fontSize) > 0.01 ||
                              Math.Abs(_expandedNameMaskSize.X - size.X) > 0.25f ||
                              Math.Abs(_expandedNameMaskSize.Y - size.Y) > 0.25f;
        if (_expandedNameAlphaMask is null || _expandedNameAlphaMaskDirty || contentChanged)
        {
            // XAML owns TextBlock.GetAlphaMask() brushes; rebind without calling Dispose().
            CompositionBrush alphaMask = ExpandedNameText.GetAlphaMask();
            _expandedNameAlphaMask = alphaMask;
            _expandedNameDropShadow.Mask = alphaMask;
            _expandedNameDropShadowAura.Mask = alphaMask;
            _expandedNameMaskText = text;
            _expandedNameMaskFontSize = fontSize;
            _expandedNameMaskSize = size;
            _expandedNameAlphaMaskDirty = false;
            if (contentChanged)
                QueueExpandedNameShadowRefresh(markMaskDirty: true);
        }

        if (!_animating && !_dwmTransitionRunning)
        {
            if (ExpandedNameShadowHost.Opacity < 0.999)
                ExpandedNameShadowHost.Opacity = 1;
            if (_expandedNameShadowCompositionVisual is not null &&
                _expandedNameShadowCompositionVisual.Opacity < 0.999f)
            {
                _expandedNameShadowCompositionVisual.Opacity = 1f;
            }
        }

        _expandedNameDropShadow.Opacity = 1.0f;
        _expandedNameDropShadowAura.Opacity = 0.92f;
        _expandedNameShadowRoot.IsVisible = true;
    }

    private void UpdateCompactTileShadowVisual(CompactVisualStyle style, bool highContrast)
    {
        if (_compactTileShadowVisual is null || _compactTileDropShadow is null) return;
        // WinUI 3's SystemBackdropElement uses ContentExternalBackdropLink hole-punching across
        // its full rectangular bounds: any XAML DropShadow placed behind CompactSurfaceHost has
        // its 4 rounded corners punched out to raw desktop wallpaper, creating white square boxes.
        // Only enable the rounded GPU DropShadow when CompactSurfaceHost is collapsed (NativeDesktop).
        bool backdropVisible = CompactSurfaceHost.Visibility == Visibility.Visible;
        if (highContrast || backdropVisible)
        {
            _compactTileShadowVisual.IsVisible = false;
            return;
        }

        _compactTileShadowVisual.IsVisible = true;
        _compactTileDropShadow.Color = Color.FromArgb(30, 0, 0, 0);
        _compactTileDropShadow.BlurRadius = _compactPointerHovered ? 12f : 8f;
        _compactTileDropShadow.Offset = new Vector3(0f, _compactPointerHovered ? 3.0f : 2.0f, 0f);
    }

    internal void UpdateCompactSelectionPlateGeometry(double? compactRadiusOverride = null)
    {
        double viewWidth = Math.Max(48, CompactView.Width);
        double viewHeight = Math.Max(48, CompactView.Height);
        double tileW = Math.Max(24, CompactTile.Width);
        double tileH = Math.Max(24, CompactTile.Height);

        double desiredWidth = Math.Min(viewWidth - 2.0, tileW + 8.0);
        double plateWidth = desiredWidth;
        double shiftX = 0.0;

        if (_hwnd != IntPtr.Zero &&
            _definition.PlacementMode == OrganizerPlacementMode.Floating &&
            _compactBounds.Width > 0)
        {
            DisplayInfo display = DisplayPlacementService.ForBounds(_compactBounds);
            double scale = Math.Max(1.0, display.Scale);
            double leftOverhangDip = Math.Max(0, (display.Work.Left - _compactBounds.Left) / scale);
            double rightOverhangDip = Math.Max(0, (_compactBounds.Right - display.Work.Right) / scale);
            if (leftOverhangDip > 0.5 || rightOverhangDip > 0.5)
            {
                double visibleLeft = leftOverhangDip + 1.0;
                double visibleRight = viewWidth - rightOverhangDip - 1.0;
                double visibleSpan = visibleRight - visibleLeft;
                if (visibleSpan > tileW + 1.0)
                {
                    double centerX = viewWidth / 2.0;
                    double maxLeftHalo = Math.Max(0.0, (centerX - tileW / 2.0) - visibleLeft);
                    double maxRightHalo = Math.Max(0.0, visibleRight - (centerX + tileW / 2.0));
                    double symmetricHalo = Math.Min(4.0, Math.Min(maxLeftHalo, maxRightHalo));
                    if (symmetricHalo >= 2.0)
                    {
                        plateWidth = Math.Min(desiredWidth, tileW + symmetricHalo * 2.0);
                    }
                    else
                    {
                        plateWidth = Math.Clamp(tileW + 4.0, tileW + 2.0, Math.Min(desiredWidth, visibleSpan));
                        double naturalLeft = centerX - plateWidth / 2.0;
                        double naturalRight = centerX + plateWidth / 2.0;
                        if (naturalRight > visibleRight)
                            shiftX = visibleRight - naturalRight;
                        else if (naturalLeft < visibleLeft)
                            shiftX = visibleLeft - naturalLeft;
                    }
                }
            }
        }

        bool labelVisible = CompactNameText.Visibility == Visibility.Visible &&
                            !string.IsNullOrEmpty(CompactNameText.Text);
        double rowGap = CompactView.RowDefinitions.Count > 1 && CompactView.RowDefinitions[1].Height.IsAbsolute
            ? CompactView.RowDefinitions[1].Height.Value
            : 4.0;
        double labelHeight = 0.0;
        if (labelVisible)
        {
            double measuredHeight = CompactNameText.ActualHeight > 1
                ? CompactNameText.ActualHeight
                : CompactNameText.DesiredSize.Height;
            labelHeight = measuredHeight > 1 ? measuredHeight : Math.Max(15.0, CompactNameText.LineHeight);
        }

        double targetHeight = labelVisible
            ? tileH + rowGap + labelHeight + 3.0
            : tileH + 4.0;
        double plateHeight = Math.Clamp(targetHeight, tileH + 2.0, Math.Max(tileH + 2.0, viewHeight - 4.0));

        double compactRadius = compactRadiusOverride ?? (CompactCornerRadiusDip * _appliedCompactScale);
        var concentricRadius = new CornerRadius(Math.Max(10.0, compactRadius + 3.5));

        if (Math.Abs(CompactNativeSelectionPlate.Width - plateWidth) > 0.25)
            CompactNativeSelectionPlate.Width = plateWidth;
        if (Math.Abs(CompactNativeSelectionPlate.Height - plateHeight) > 0.25)
            CompactNativeSelectionPlate.Height = plateHeight;
        if (CompactNativeSelectionPlate.CornerRadius != concentricRadius)
            CompactNativeSelectionPlate.CornerRadius = concentricRadius;

        float targetShiftX = (float)Math.Round(shiftX, 1);
        if (Math.Abs(CompactNativeSelectionPlate.Translation.X - targetShiftX) > 0.25f)
            CompactNativeSelectionPlate.Translation = new Vector3(targetShiftX, 0f, 0f);
    }

    private void UpdateCompactSelectionPlateVisual()
    {
        UpdateCompactSelectionPlateGeometry();
        CompactVisualStyle style = CompactVisualStyles.Normalize(_host.State.GlobalSettings.CompactVisualStyle);
        bool nativeBlend = CompactVisualStyles.UsesNativeBlendSurface(style);

        if (_organizerMultiSelected)
        {
            CompactNativeSelectionPlate.Background = CompactMultiSelectBgBrush;
            CompactNativeSelectionPlate.BorderBrush = CompactMultiSelectBorderBrush;
            CompactNativeSelectionPlate.BorderThickness = new Thickness(1.25);
            return;
        }

        if (nativeBlend && _compactPointerHovered)
        {
            CompactNativeSelectionPlate.Background = CompactNativeHoverBgBrush;
            CompactNativeSelectionPlate.BorderBrush = CompactNativeHoverBorderBrush;
            CompactNativeSelectionPlate.BorderThickness = new Thickness(1);
            return;
        }

        CompactNativeSelectionPlate.Background = CompactTransparentPlateBrush;
        CompactNativeSelectionPlate.BorderBrush = CompactTransparentPlateBrush;
        CompactNativeSelectionPlate.BorderThickness = new Thickness(1);
    }

    private void AnimateCompactMultiSelectBadge(bool selected)
    {
        if (_closing || !WindowRoot.IsLoaded) return;
        Visual badgeVisual = ElementCompositionPreview.GetElementVisual(CompactMultiSelectBadge);
        badgeVisual.CenterPoint = new Vector3(8f, 8f, 0f);
        if (!selected)
        {
            badgeVisual.StopAnimation("Scale");
            badgeVisual.Scale = Vector3.One;
            CompactMultiSelectBadge.Visibility = Visibility.Collapsed;
            return;
        }

        CompactMultiSelectBadge.Visibility = Visibility.Visible;
        if (UseCustomAnimations)
        {
            Compositor compositor = badgeVisual.Compositor;
            badgeVisual.StopAnimation("Scale");
            badgeVisual.Scale = new Vector3(0.45f, 0.45f, 1f);
            _multiSelectBadgeSpring ??= compositor.CreateSpringVector3Animation();
            _multiSelectBadgeSpring.InitialValue = new Vector3(0.45f, 0.45f, 1f);
            _multiSelectBadgeSpring.FinalValue = Vector3.One;
            _multiSelectBadgeSpring.DampingRatio = 0.62f;
            _multiSelectBadgeSpring.Period = TimeSpan.FromMilliseconds(38);
            badgeVisual.StartAnimation("Scale", _multiSelectBadgeSpring);
        }
        else
        {
            badgeVisual.StopAnimation("Scale");
            badgeVisual.Scale = Vector3.One;
        }
    }

    private void PulseCompactTileSelectionSpring()
    {
        if (_closing || !WindowRoot.IsLoaded || !UseCustomAnimations) return;
        CompactVisualStyle style = CompactVisualStyles.Normalize(_host.State.GlobalSettings.CompactVisualStyle);
        FrameworkElement springHost = CompactVisualStyles.ShowsFolderSilhouette(style) ? CompactTile : CompactThumbnailHost;
        Visual tileVisual = ElementCompositionPreview.GetElementVisual(springHost);
        double w = springHost.ActualWidth > 1 ? springHost.ActualWidth : springHost.Width;
        double h = springHost.ActualHeight > 1 ? springHost.ActualHeight : springHost.Height;
        tileVisual.CenterPoint = new Vector3((float)(w / 2.0), (float)(h * 0.14f), 0f);
        tileVisual.Scale = new Vector3(0.96f, 0.96f, 1f);
        AnimateCompactTileHoverSpring();
    }

    /// <summary>
    /// Runs native GPU Composition spring animations on the Windows DWM compositor thread
    /// (true 120-240Hz ProMotion smoothness with zero C# per-frame rendering loop or WinRT allocations!).
    /// </summary>
    private void AnimateCompactTileHoverSpring()
    {
        if (_closing || !WindowRoot.IsLoaded) return;
        CompactVisualStyle style = CompactVisualStyles.Normalize(_host.State.GlobalSettings.CompactVisualStyle);
        bool folderStyle = CompactVisualStyles.ShowsFolderSilhouette(style);
        float targetScale = _compactPointerPressed
            ? 0.94f
            : _compactPointerHovered
                ? (style == CompactVisualStyle.GlassTile ? 1.05f : 1.035f)
                : 1.0f;
        float iconScale = _compactPointerPressed
            ? 0.96f
            : _compactPointerHovered
                ? 1.045f
                : 1.0f;

        // FluentFolder animates CompactTile so tab & pocket spring together as one physical object;
        // GlassTile & NativeDesktop animate CompactThumbnailHost directly.
        FrameworkElement springHost = folderStyle ? CompactTile : CompactThumbnailHost;
        FrameworkElement inactiveHost = folderStyle ? CompactThumbnailHost : CompactTile;
        Visual tileVisual = ElementCompositionPreview.GetElementVisual(springHost);
        Visual inactiveVisual = ElementCompositionPreview.GetElementVisual(inactiveHost);
        if (inactiveVisual.Scale != Vector3.One)
        {
            inactiveVisual.StopAnimation("Scale");
            inactiveVisual.Scale = Vector3.One;
        }
        Visual iconVisual = ElementCompositionPreview.GetElementVisual(CompactPreviewGrid);
        double w = springHost.ActualWidth > 1 ? springHost.ActualWidth : springHost.Width;
        double h = springHost.ActualHeight > 1 ? springHost.ActualHeight : springHost.Height;
        // Bias vertical pivot to 14% from top on hover/release so the top rim NEVER clips at y < 0!
        tileVisual.CenterPoint = _compactPointerPressed
            ? new Vector3((float)(w / 2.0), (float)(h / 2.0), 0f)
            : new Vector3((float)(w / 2.0), (float)(h * 0.14f), 0f);
        iconVisual.CenterPoint = new Vector3((float)(CompactPreviewGrid.ActualWidth / 2.0), (float)(CompactPreviewGrid.ActualHeight / 2.0), 0f);

        if (UseCustomAnimations)
        {
            Compositor compositor = tileVisual.Compositor;
            _tileScaleSpring ??= compositor.CreateSpringVector3Animation();
            _tileScaleSpring.FinalValue = new Vector3(targetScale, targetScale, 1f);
            _tileScaleSpring.DampingRatio = _compactPointerPressed ? 0.78f : 0.64f;
            _tileScaleSpring.Period = TimeSpan.FromMilliseconds(_compactPointerPressed ? 34 : 46);
            tileVisual.StartAnimation("Scale", _tileScaleSpring);

            _iconScaleSpring ??= compositor.CreateSpringVector3Animation();
            _iconScaleSpring.FinalValue = new Vector3(iconScale, iconScale, 1f);
            _iconScaleSpring.DampingRatio = 0.68f;
            _iconScaleSpring.Period = TimeSpan.FromMilliseconds(50);
            iconVisual.StartAnimation("Scale", _iconScaleSpring);
        }
        else
        {
            tileVisual.Scale = new Vector3(targetScale, targetScale, 1f);
            iconVisual.Scale = new Vector3(iconScale, iconScale, 1f);
        }

        if (_compactSpecularVisual is not null && _compactSpecularBrush is not null)
        {
            if (style == CompactVisualStyle.GlassTile && (_compactPointerHovered || _compactPointerPressed))
            {
                _compactSpecularBrush.EllipseRadius = _compactPointerPressed
                    ? new Vector2(0.52f, 0.52f)
                    : new Vector2(0.65f, 0.65f);
                _compactSpecularVisual.Opacity = _compactPointerPressed ? 1.0f : 0.85f;
            }
            else
            {
                _compactSpecularVisual.Opacity = 0f;
            }
        }
        UpdateCompactTileShadowVisual(style, DesktopTypographyService.IsHighContrast());
    }

    internal void UpdateCompactSpecularPointer(PointerRoutedEventArgs e)
    {
        if (!_compactPointerHovered || _compactSpecularBrush is null || _compactSpecularVisual is null) return;
        if (_host.State.GlobalSettings.CompactVisualStyle != CompactVisualStyle.GlassTile) return;
        double w = CompactThumbnailHost.ActualWidth;
        double h = CompactThumbnailHost.ActualHeight;
        if (w <= 1 || h <= 1) return;

        Point pos = e.GetCurrentPoint(CompactThumbnailHost).Position;
        float nx = (float)Math.Clamp(pos.X / w, 0.05, 0.95);
        float ny = (float)Math.Clamp(pos.Y / h, 0.05, 0.95);
        _compactSpecularBrush.EllipseCenter = new Vector2(nx, ny);
    }

    internal void SetCompactPressVisualState(bool pressed)
    {
        if (_compactPointerPressed == pressed) return;
        _compactPointerPressed = pressed;
        AnimateCompactTileHoverSpring();
    }

    private void DisposeCompactVisualResources()
    {
        if (_compactNameShadowHooked)
        {
            CompactNameText.SizeChanged -= CompactNameText_SizeChanged;
            _compactNameShadowHooked = false;
        }
        if (_expandedNameShadowHooked)
        {
            ExpandedNameText.SizeChanged -= ExpandedNameText_SizeChanged;
            if (_expandedNameVisibilityCallbackToken != 0)
            {
                ExpandedNameText.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _expandedNameVisibilityCallbackToken);
                _expandedNameVisibilityCallbackToken = 0;
            }
            if (_expandedEditorVisibilityCallbackToken != 0)
            {
                ExpandedNameEditor.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _expandedEditorVisibilityCallbackToken);
                _expandedEditorVisibilityCallbackToken = 0;
            }
            if (_expandedViewVisibilityCallbackToken != 0)
            {
                ExpandedView.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _expandedViewVisibilityCallbackToken);
                _expandedViewVisibilityCallbackToken = 0;
            }
            _expandedNameShadowHooked = false;
        }

        if (_compactNameShadowRoot is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(CompactNameShadowHost, null);
            _compactNameShadowRoot.Children.RemoveAll();
            _compactNameShadowRoot.Dispose();
            _compactNameShadowRoot = null;
        }
        if (_compactNameDropShadow is not null) _compactNameDropShadow.Mask = null;
        if (_compactNameDropShadowAura is not null) _compactNameDropShadowAura.Mask = null;
        // XAML owns TextBlock.GetAlphaMask() brushes; detach from DropShadow without calling Dispose().
        _compactNameAlphaMask = null;
        _compactNameMaskText = null;
        _compactNameMaskFontSize = 0;
        _compactNameMaskSize = Vector2.Zero;
        _compactNameShadowAuraVisual?.Dispose();
        _compactNameShadowAuraVisual = null;
        _compactNameDropShadowAura?.Dispose();
        _compactNameDropShadowAura = null;
        _compactNameShadowVisual?.Dispose();
        _compactNameShadowVisual = null;
        _compactNameDropShadow?.Dispose();
        _compactNameDropShadow = null;

        if (_expandedNameShadowRoot is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(ExpandedNameShadowHost, null);
            _expandedNameShadowRoot.Children.RemoveAll();
            _expandedNameShadowRoot.Dispose();
            _expandedNameShadowRoot = null;
        }
        if (_expandedNameDropShadow is not null) _expandedNameDropShadow.Mask = null;
        if (_expandedNameDropShadowAura is not null) _expandedNameDropShadowAura.Mask = null;
        _expandedNameAlphaMask = null;
        _expandedNameMaskText = null;
        _expandedNameMaskFontSize = 0;
        _expandedNameMaskSize = Vector2.Zero;
        _expandedNameAlphaMaskDirty = false;
        _expandedNameShadowRefreshQueued = false;
        _expandedNameShadowRefreshMarkDirty = false;
        _expandedNameShadowAuraVisual?.Dispose();
        _expandedNameShadowAuraVisual = null;
        _expandedNameDropShadowAura?.Dispose();
        _expandedNameDropShadowAura = null;
        _expandedNameShadowVisual?.Dispose();
        _expandedNameShadowVisual = null;
        _expandedNameDropShadow?.Dispose();
        _expandedNameDropShadow = null;

        if (_compactTileShadowVisual is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(CompactShadowHost, null);
            _compactTileShadowVisual.Dispose();
            _compactTileShadowVisual = null;
        }
        if (_compactTileDropShadow is not null)
        {
            _compactTileDropShadow.ImplicitAnimations = null;
            _compactTileDropShadow.Mask = null;
            _compactTileDropShadow.Dispose();
            _compactTileDropShadow = null;
        }
        _compactTileShadowImplicitAnims?.Dispose();
        _compactTileShadowImplicitAnims = null;
        _compactTileShadowBlurAnim?.Dispose();
        _compactTileShadowBlurAnim = null;
        _compactTileShadowOffsetAnim?.Dispose();
        _compactTileShadowOffsetAnim = null;
        _compactTileShadowMaskBrush?.Dispose();
        _compactTileShadowMaskBrush = null;
        if (_compactTileShadowMaskSurface is not null)
        {
            _compactTileShadowMaskSurface.SourceVisual = null;
            _compactTileShadowMaskSurface.Dispose();
            _compactTileShadowMaskSurface = null;
        }
        if (_compactTileShadowMaskVisual is not null)
        {
            _compactTileShadowMaskVisual.Shapes.Clear();
            _compactTileShadowMaskVisual.Dispose();
            _compactTileShadowMaskVisual = null;
        }
        _compactTileShadowMaskShape?.Dispose();
        _compactTileShadowMaskShape = null;
        _compactTileShadowMaskFill?.Dispose();
        _compactTileShadowMaskFill = null;
        _compactTileShadowMaskGeom?.Dispose();
        _compactTileShadowMaskGeom = null;

        if (_compactSpecularVisual is not null)
        {
            _compactSpecularVisual.ImplicitAnimations = null;
            ElementCompositionPreview.SetElementChildVisual(CompactSpecularOverlay, null);
            _compactSpecularVisual.Dispose();
            _compactSpecularVisual = null;
        }
        _compactSpecularImplicitAnims?.Dispose();
        _compactSpecularImplicitAnims = null;
        _compactSpecularOpacityAnim?.Dispose();
        _compactSpecularOpacityAnim = null;
        _compactSpecularBrush?.Dispose();
        _compactSpecularBrush = null;
        _compactTileShadowClip?.Dispose();
        _compactTileShadowClip = null;
        _compactTileShadowGeom?.Dispose();
        _compactTileShadowGeom = null;

        if (_expandedSweepVisual is not null)
        {
            _expandedSweepVisual.StopAnimation("Opacity");
            _expandedSweepVisual.Clip = null;
            _expandedSweepVisual.Brush = null;
            ElementCompositionPreview.SetElementChildVisual(ExpandedSpecularSweep, null);
            _expandedSweepVisual.Dispose();
            _expandedSweepVisual = null;
        }
        if (_expandedSweepBrush is not null)
        {
            _expandedSweepBrush.StopAnimation("Offset");
            _expandedSweepBrush.Dispose();
            _expandedSweepBrush = null;
        }
        _expandedSweepOpacityExpr?.Dispose();
        _expandedSweepOpacityExpr = null;
        _expandedSweepOffsetExpr?.Dispose();
        _expandedSweepOffsetExpr = null;
        _expandedSweepClip?.Dispose();
        _expandedSweepClip = null;
        _expandedSweepClipGeom?.Dispose();
        _expandedSweepClipGeom = null;

        _tileScaleSpring?.Dispose();
        _tileScaleSpring = null;
        _iconScaleSpring?.Dispose();
        _iconScaleSpring = null;
        _multiSelectBadgeSpring?.Dispose();
        _multiSelectBadgeSpring = null;

        if (_overlayImplicitAnimsAttached)
        {
            ElementCompositionPreview.SetImplicitShowAnimation(ExpandedFilterBar, null);
            ElementCompositionPreview.SetImplicitHideAnimation(ExpandedFilterBar, null);
            ElementCompositionPreview.SetImplicitShowAnimation(SelectionCountBadge, null);
            ElementCompositionPreview.SetImplicitHideAnimation(SelectionCountBadge, null);
            _overlayImplicitAnimsAttached = false;
        }
        _overlayShowAnimGroup?.Dispose();
        _overlayShowAnimGroup = null;
        _overlayHideAnimGroup?.Dispose();
        _overlayHideAnimGroup = null;
        _overlayShowOpacityAnim?.Dispose();
        _overlayShowOpacityAnim = null;
        _overlayShowTranslationAnim?.Dispose();
        _overlayShowTranslationAnim = null;
        _overlayHideOpacityAnim?.Dispose();
        _overlayHideOpacityAnim = null;
        _overlayHideTranslationAnim?.Dispose();
        _overlayHideTranslationAnim = null;
        _overlayEaseOut?.Dispose();
        _overlayEaseOut = null;
    }

    private void CompactTile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _compactPointerHovered = true;
        UpdateCompactSelectionPlateVisual();
        AnimateCompactTileHoverSpring();
        UpdateCompactSpecularPointer(e);
        PreWarmCompactHoverIcons();
    }

    private void CompactTile_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _compactPointerHovered = false;
        _compactPointerPressed = false;
        UpdateCompactSelectionPlateVisual();
        AnimateCompactTileHoverSpring();
    }

    internal void PrepareForGroupPeerDrag(bool topmost)
    {
        if (_closing || _hwnd == IntPtr.Zero) return;
        _desktopCoordinator.PauseForWidgetDrag();
        if (topmost) _desktopLayer?.SetExpanded(true, stayTopmost: true);
    }

    internal void MoveAsGroupDragPeer(NativeMethods.RECT nextBounds)
    {
        if (_closing || _hwnd == IntPtr.Zero || RectsEqual(nextBounds, _compactBounds)) return;
        _compactBounds = nextBounds;
        _ = NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            nextBounds.Left,
            nextBounds.Top,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW);
    }

    internal void ApplyProgrammaticCompactBounds(NativeMethods.RECT newWindowBounds)
    {
        if (_closing || _definition.PlacementMode != OrganizerPlacementMode.Floating || IsContained) return;
        _compactBounds = newWindowBounds;
        _definition.Position = DisplayPlacementService.Capture(_compactBounds, _hwnd);
        if (!_expanded && !_animating)
        {
            ApplyBounds(_compactBounds, show: _runtimeVisible, preserveZOrder: true);
            UpdateSurfaceClips();
        }
    }

    internal void FinishGroupPeerDrag(bool commitPosition)
    {
        if (_closing || _hwnd == IntPtr.Zero) return;
        if (commitPosition)
            _definition.Position = DisplayPlacementService.Capture(_compactBounds, _hwnd);
        _desktopLayer?.SetExpanded(false);
        _desktopLayer?.Reattach();
        RefreshPerformanceSettings();
    }

    private async void AlignToDesktopGridMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            int count = await _host.AlignFloatingToDesktopGridAsync(_definition.Id);
            if (_closing) return;
            if (count > 0)
            {
                string message = AppStrings.Format("DesktopGridAlignDoneFormat", count);
                if (_expanded)
                    message += LocalizeToast("（收起后生效）", " (applies on collapse)", "（収納後に反映）");
                ShowMessage(message, InfoBarSeverity.Success);
            }
            else
            {
                ShowMessage(
                    LocalizeToast("当前已在桌面图标网格位置", "Already aligned to desktop icon grid", "すでにデスクトップグリッドに整列済みです"),
                    InfoBarSeverity.Informational);
            }
        }, "对齐桌面图标网格失败");
    }

    private async void ImportDesktopSelectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            (int moved, int shortcuts, int failed) = await _host.ImportSelectedDesktopIconsAsync(_definition.Id);
            if (moved == 0 && shortcuts == 0 && failed == 0)
            {
                if (!_closing) ShowMessage(AppStrings.Get("ImportDesktopSelectionEmpty"), InfoBarSeverity.Informational);
                return;
            }
            if (!_closing)
            {
                ShowMessage(
                    AppStrings.Format("ImportDesktopSelectionResultFormat", moved, shortcuts, failed),
                    failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            }
        }, "收入桌面已选项目失败");
    }
}
