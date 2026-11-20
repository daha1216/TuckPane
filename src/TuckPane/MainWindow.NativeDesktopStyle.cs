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
    private static readonly SolidColorBrush CompactMultiSelectBgBrush = new(Color.FromArgb(62, 0, 120, 212));
    private static readonly SolidColorBrush CompactMultiSelectBorderBrush = new(Color.FromArgb(220, 0, 120, 212));
    private static readonly SolidColorBrush CompactNativeHoverBgBrush = new(Color.FromArgb(30, 0, 120, 212));
    private static readonly SolidColorBrush CompactNativeHoverBorderBrush = new(Color.FromArgb(98, 0, 120, 212));
    private static readonly SolidColorBrush CompactTransparentPlateBrush = new(Colors.Transparent);

    private bool _compactPointerHovered;
    private bool _compactPointerPressed;
    private bool _organizerMultiSelected;

    // GPU-composited shadow & iOS 26 Liquid Glass live specular visuals (zero per-frame XAML churn).
    private ContainerVisual? _compactNameShadowRoot;
    private SpriteVisual? _compactNameShadowAuraVisual;
    private DropShadow? _compactNameDropShadowAura;
    private SpriteVisual? _compactNameShadowVisual;
    private DropShadow? _compactNameDropShadow;
    private bool _compactNameShadowHooked;

    private SpriteVisual? _compactTileShadowVisual;
    private DropShadow? _compactTileDropShadow;
    private CompositionRoundedRectangleGeometry? _compactTileShadowGeom;
    private CompositionGeometricClip? _compactTileShadowClip;

    private SpriteVisual? _compactSpecularVisual;
    private CompositionRadialGradientBrush? _compactSpecularBrush;

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

        CompactLiquidSheen.Visibility = glassStyle ? Visibility.Visible : Visibility.Collapsed;
        CompactNativeBadge.Visibility = nativeStyle ? Visibility.Visible : Visibility.Collapsed;
        CompactEdgeOverlay.Visibility = nativeStyle ? Visibility.Collapsed : Visibility.Visible;
        CompactEdgeOverlay.Opacity = nativeStyle ? 0.0 : backdropOpacity;

        CompactFolderTab.Visibility = folderStyle ? Visibility.Visible : Visibility.Collapsed;
        CompactFolderPocket.Visibility = folderStyle ? Visibility.Visible : Visibility.Collapsed;
        if (folderStyle)
        {
            double tileWidth = Math.Max(24, CompactTile.Width);
            CompactFolderTab.Width = Math.Max(16, Math.Round(tileWidth * 0.44));
            CompactFolderTab.Height = Math.Max(6, Math.Round(tileWidth * 0.14));
            CompactFolderTab.Margin = new Thickness(Math.Max(3, tileWidth * 0.08), -CompactFolderTab.Height + 2, 0, 0);
        }

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

        // Keep expanded liquid glass sheen disabled in high-contrast accessibility mode.
        ExpandedLiquidSheen.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;

        EnsureCompactGpuShadowVisuals();
        UpdateCompactNameShadowVisual(style, highContrast);
        UpdateCompactTileShadowVisual(style, highContrast);
        UpdateCompactSelectionPlateVisual();
    }

    /// <summary>
    /// Computes the symmetric on-screen label width so tiles anchored flush against the left or
    /// right monitor edge wrap/ellipsis gracefully inside the visible work area and NEVER slice
    /// characters off at the monitor bezel.
    /// </summary>
    internal void UpdateCompactLabelSafeWidth()
    {
        double fullWidth = Math.Max(48, CompactView.Width - 6);
        double minSafeWidth = Math.Max(48, CompactTile.Width - 2);
        double safeWidth = fullWidth;

        if (_hwnd != IntPtr.Zero &&
            _definition.PlacementMode == OrganizerPlacementMode.Floating &&
            _compactBounds.Width > 0)
        {
            DisplayInfo display = DisplayPlacementService.ForBounds(_compactBounds);
            double scale = Math.Max(1.0, display.Scale);
            double leftOverhangDip = Math.Max(0, (display.Work.Left - _compactBounds.Left) / scale);
            double rightOverhangDip = Math.Max(0, (_compactBounds.Right - display.Work.Right) / scale);
            double maxOverhangDip = Math.Max(leftOverhangDip, rightOverhangDip);
            if (maxOverhangDip > 0.5)
            {
                double visibleSymmetricWidth = CompactView.Width - 2.0 * maxOverhangDip - 4.0;
                safeWidth = Math.Clamp(visibleSymmetricWidth, minSafeWidth, fullWidth);
            }
        }

        if (Math.Abs(CompactNameText.MaxWidth - safeWidth) > 0.5)
            CompactNameText.MaxWidth = safeWidth;
    }

    internal void SyncCompactNameShadow()
    {
        CompactVisualStyle style = CompactVisualStyles.Normalize(_host.State.GlobalSettings.CompactVisualStyle);
        UpdateCompactNameShadowVisual(style, DesktopTypographyService.IsHighContrast());
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

        if (_compactTileShadowVisual is not null && w > 1 && h > 1)
        {
            _compactTileShadowVisual.Size = new Vector2((float)w, (float)h);
        }
        if (_compactSpecularVisual is not null && w > 1 && h > 1)
        {
            _compactSpecularVisual.Size = new Vector2((float)w, (float)h);
            if (_compactTileShadowGeom is not null)
            {
                _compactTileShadowGeom.Size = new Vector2((float)w, (float)h);
                _compactTileShadowGeom.CornerRadius = new Vector2((float)compactRadius, (float)compactRadius);
            }
        }
    }

    internal void UpdateExpandedLiquidSheenGeometry(double expandedRadius)
    {
        double clamped = Math.Max(0, expandedRadius);
        ExpandedLiquidSheen.CornerRadius = new CornerRadius(clamped);
        ExpandedLiquidInnerRim.CornerRadius = new CornerRadius(Math.Max(0, clamped - 1));
    }

    private void EnsureCompactGpuShadowVisuals()
    {
        if (!WindowRoot.IsLoaded) return;

        // 1. Dual-Layer Hardware Gaussian DropShadow on CompactNameText using GetAlphaMask():
        //    - Wide ambient aura (BlurRadius 5.6) + tight Windows Explorer DrawShadowText core (BlurRadius 2.1)
        //    Delivers razor-sharp legibility on pure white wallpaper, black wallpaper, and busy line-art alike!
        if (_compactNameShadowRoot is null)
        {
            Visual hostVisual = ElementCompositionPreview.GetElementVisual(CompactNameShadowHost);
            Compositor compositor = hostVisual.Compositor;
            CompositionBrush alphaMask = CompactNameText.GetAlphaMask();

            _compactNameDropShadowAura = compositor.CreateDropShadow();
            _compactNameDropShadowAura.Color = Color.FromArgb(184, 0, 0, 0);
            _compactNameDropShadowAura.BlurRadius = 5.6f;
            _compactNameDropShadowAura.Offset = new Vector3(0f, 1.3f, 0f);
            _compactNameDropShadowAura.Mask = alphaMask;

            _compactNameShadowAuraVisual = compositor.CreateSpriteVisual();
            _compactNameShadowAuraVisual.Shadow = _compactNameDropShadowAura;

            _compactNameDropShadow = compositor.CreateDropShadow();
            _compactNameDropShadow.Color = Color.FromArgb(255, 0, 0, 0);
            _compactNameDropShadow.BlurRadius = 2.1f;
            _compactNameDropShadow.Offset = new Vector3(0f, 0.75f, 0f);
            _compactNameDropShadow.Mask = alphaMask;

            _compactNameShadowVisual = compositor.CreateSpriteVisual();
            _compactNameShadowVisual.Shadow = _compactNameDropShadow;

            _compactNameShadowRoot = compositor.CreateContainerVisual();
            _compactNameShadowRoot.Children.InsertAtBottom(_compactNameShadowAuraVisual);
            _compactNameShadowRoot.Children.InsertAtTop(_compactNameShadowVisual);
            ElementCompositionPreview.SetElementChildVisual(CompactNameShadowHost, _compactNameShadowRoot);
        }
        if (!_compactNameShadowHooked)
        {
            _compactNameShadowHooked = true;
            CompactNameText.SizeChanged += (_, _) => SyncCompactNameShadow();
        }

        // 2. Sculptural floating ambient DropShadow beneath CompactThumbnailHost (makes Liquid Glass float on pure white wallpaper!).
        if (_compactTileShadowVisual is null)
        {
            Visual shadowHostVisual = ElementCompositionPreview.GetElementVisual(CompactShadowHost);
            Compositor compositor = shadowHostVisual.Compositor;

            _compactTileDropShadow = compositor.CreateDropShadow();
            _compactTileDropShadow.Color = Color.FromArgb(54, 0, 0, 0);
            _compactTileDropShadow.BlurRadius = 16f;
            _compactTileDropShadow.Offset = new Vector3(0f, 4.2f, 0f);

            _compactTileShadowVisual = compositor.CreateSpriteVisual();
            _compactTileShadowVisual.Shadow = _compactTileDropShadow;
            ElementCompositionPreview.SetElementChildVisual(CompactShadowHost, _compactTileShadowVisual);
        }

        // 3. iOS 26 Liquid Glass live cursor-tracking specular catchlight (pure GPU CompositionRadialGradientBrush!).
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

            _compactSpecularVisual = compositor.CreateSpriteVisual();
            _compactSpecularVisual.Brush = _compactSpecularBrush;
            _compactSpecularVisual.Clip = _compactTileShadowClip;
            _compactSpecularVisual.Opacity = 0f;
            ElementCompositionPreview.SetElementChildVisual(CompactSpecularOverlay, _compactSpecularVisual);
        }
    }

    private void UpdateCompactNameShadowVisual(CompactVisualStyle style, bool highContrast)
    {
        if (_compactNameShadowRoot is null || _compactNameShadowVisual is null || _compactNameDropShadow is null ||
            _compactNameShadowAuraVisual is null || _compactNameDropShadowAura is null)
        {
            return;
        }

        bool visible = CompactNameText.Visibility == Visibility.Visible &&
                       !string.IsNullOrEmpty(CompactNameText.Text) &&
                       !highContrast;
        if (!visible)
        {
            _compactNameShadowRoot.IsVisible = false;
            return;
        }

        double width = CompactNameText.ActualWidth > 0 ? CompactNameText.ActualWidth : CompactNameText.DesiredSize.Width;
        double height = CompactNameText.ActualHeight > 0 ? CompactNameText.ActualHeight : CompactNameText.DesiredSize.Height;
        Vector2 size = new((float)Math.Max(1, width), (float)Math.Max(1, height));
        CompactNameShadowHost.Width = size.X;
        CompactNameShadowHost.Height = size.Y;
        _compactNameShadowRoot.Size = size;
        _compactNameShadowVisual.Size = size;
        _compactNameShadowAuraVisual.Size = size;

        float opacity = (float)Math.Clamp(CompactVisualStyles.LabelShadowOpacity(style, highContrast), 0.0, 1.0);
        CompositionBrush mask = CompactNameText.GetAlphaMask();
        _compactNameDropShadow.Opacity = opacity;
        _compactNameDropShadow.Mask = mask;
        _compactNameDropShadowAura.Opacity = opacity * 0.92f;
        _compactNameDropShadowAura.Mask = mask;
        _compactNameShadowRoot.IsVisible = true;
    }

    private void UpdateCompactTileShadowVisual(CompactVisualStyle style, bool highContrast)
    {
        if (_compactTileShadowVisual is null || _compactTileDropShadow is null) return;
        if (highContrast)
        {
            _compactTileShadowVisual.IsVisible = false;
            return;
        }

        _compactTileShadowVisual.IsVisible = true;
        switch (style)
        {
            case CompactVisualStyle.GlassTile:
                _compactTileDropShadow.Color = Color.FromArgb(56, 0, 0, 0);
                _compactTileDropShadow.BlurRadius = _compactPointerHovered ? 22f : 16f;
                _compactTileDropShadow.Offset = new Vector3(0f, _compactPointerHovered ? 5.5f : 4.0f, 0f);
                break;
            case CompactVisualStyle.FluentFolder:
                _compactTileDropShadow.Color = Color.FromArgb(48, 0, 0, 0);
                _compactTileDropShadow.BlurRadius = _compactPointerHovered ? 16f : 12f;
                _compactTileDropShadow.Offset = new Vector3(0f, _compactPointerHovered ? 4.0f : 3.0f, 0f);
                break;
            default: // NativeDesktop
                _compactTileDropShadow.Color = Color.FromArgb(34, 0, 0, 0);
                _compactTileDropShadow.BlurRadius = _compactPointerHovered ? 12f : 8f;
                _compactTileDropShadow.Offset = new Vector3(0f, 2.2f, 0f);
                break;
        }
    }

    private void UpdateCompactSelectionPlateVisual()
    {
        CompactVisualStyle style = CompactVisualStyles.Normalize(_host.State.GlobalSettings.CompactVisualStyle);
        bool nativeBlend = CompactVisualStyles.UsesNativeBlendSurface(style);

        if (_organizerMultiSelected)
        {
            CompactNativeSelectionPlate.Background = CompactMultiSelectBgBrush;
            CompactNativeSelectionPlate.BorderBrush = CompactMultiSelectBorderBrush;
            CompactNativeSelectionPlate.BorderThickness = new Thickness(1.5);
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

    /// <summary>
    /// Runs native GPU Composition spring animations on the Windows DWM compositor thread
    /// (true 120-240Hz ProMotion smoothness with zero C# per-frame rendering loop!).
    /// </summary>
    private void AnimateCompactTileHoverSpring()
    {
        if (_closing || !WindowRoot.IsLoaded) return;
        CompactVisualStyle style = CompactVisualStyles.Normalize(_host.State.GlobalSettings.CompactVisualStyle);
        float targetScale = _compactPointerPressed
            ? 0.94f
            : _compactPointerHovered
                ? (style == CompactVisualStyle.GlassTile ? 1.06f : 1.035f)
                : 1.0f;
        float iconScale = _compactPointerPressed
            ? 0.96f
            : _compactPointerHovered
                ? 1.045f
                : 1.0f;

        Visual tileVisual = ElementCompositionPreview.GetElementVisual(CompactThumbnailHost);
        Visual iconVisual = ElementCompositionPreview.GetElementVisual(CompactPreviewGrid);
        double w = CompactThumbnailHost.ActualWidth > 1 ? CompactThumbnailHost.ActualWidth : CompactThumbnailHost.Width;
        double h = CompactThumbnailHost.ActualHeight > 1 ? CompactThumbnailHost.ActualHeight : CompactThumbnailHost.Height;
        tileVisual.CenterPoint = new Vector3((float)(w / 2.0), (float)(h / 2.0), 0f);
        iconVisual.CenterPoint = new Vector3((float)(CompactPreviewGrid.ActualWidth / 2.0), (float)(CompactPreviewGrid.ActualHeight / 2.0), 0f);

        if (UseCustomAnimations)
        {
            Compositor compositor = tileVisual.Compositor;
            SpringVector3NaturalMotionAnimation tileSpring = compositor.CreateSpringVector3Animation();
            tileSpring.FinalValue = new Vector3(targetScale, targetScale, 1f);
            tileSpring.DampingRatio = _compactPointerPressed ? 0.78f : 0.64f;
            tileSpring.Period = TimeSpan.FromMilliseconds(_compactPointerPressed ? 34 : 46);
            tileVisual.StartAnimation("Scale", tileSpring);

            SpringVector3NaturalMotionAnimation iconSpring = compositor.CreateSpringVector3Animation();
            iconSpring.FinalValue = new Vector3(iconScale, iconScale, 1f);
            iconSpring.DampingRatio = 0.68f;
            iconSpring.Period = TimeSpan.FromMilliseconds(50);
            iconVisual.StartAnimation("Scale", iconSpring);
        }
        else
        {
            tileVisual.Scale = new Vector3(targetScale, targetScale, 1f);
            iconVisual.Scale = new Vector3(iconScale, iconScale, 1f);
        }

        if (_compactSpecularVisual is not null)
        {
            _compactSpecularVisual.Opacity = _compactPointerHovered && style == CompactVisualStyle.GlassTile ? 0.85f : 0f;
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
        if (_compactNameShadowRoot is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(CompactNameShadowHost, null);
            _compactNameShadowRoot.Children.RemoveAll();
            _compactNameShadowRoot.Dispose();
            _compactNameShadowRoot = null;
        }
        _compactNameShadowAuraVisual?.Dispose();
        _compactNameShadowAuraVisual = null;
        _compactNameDropShadowAura?.Dispose();
        _compactNameDropShadowAura = null;
        _compactNameShadowVisual?.Dispose();
        _compactNameShadowVisual = null;
        _compactNameDropShadow?.Dispose();
        _compactNameDropShadow = null;

        if (_compactTileShadowVisual is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(CompactShadowHost, null);
            _compactTileShadowVisual.Dispose();
            _compactTileShadowVisual = null;
        }
        _compactTileDropShadow?.Dispose();
        _compactTileDropShadow = null;

        if (_compactSpecularVisual is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(CompactSpecularOverlay, null);
            _compactSpecularVisual.Dispose();
            _compactSpecularVisual = null;
        }
        _compactSpecularBrush?.Dispose();
        _compactSpecularBrush = null;
        _compactTileShadowClip?.Dispose();
        _compactTileShadowClip = null;
        _compactTileShadowGeom?.Dispose();
        _compactTileShadowGeom = null;
    }

    private void CompactTile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _compactPointerHovered = true;
        UpdateCompactSelectionPlateVisual();
        AnimateCompactTileHoverSpring();
        UpdateCompactSpecularPointer(e);
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
            if (count > 0 && !_closing)
                ShowMessage(AppStrings.Format("DesktopGridAlignDoneFormat", count), InfoBarSeverity.Success);
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
