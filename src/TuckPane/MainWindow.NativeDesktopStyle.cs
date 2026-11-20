using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;
using Windows.UI;

namespace TuckPane;

public sealed partial class MainWindow
{
    private bool _compactPointerHovered;
    private bool _organizerMultiSelected;

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
        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;

        double backdropOpacity = CompactVisualStyles.BackdropOpacity(style);
        CompactSurfaceHost.Opacity = backdropOpacity;
        CompactEdgeOverlay.Opacity = style == CompactVisualStyle.NativeDesktop ? 0.0 : backdropOpacity;

        bool folderStyle = CompactVisualStyles.ShowsFolderSilhouette(style);
        CompactFolderTab.Visibility = folderStyle ? Visibility.Visible : Visibility.Collapsed;
        CompactFolderPocket.Visibility = folderStyle ? Visibility.Visible : Visibility.Collapsed;
        if (folderStyle)
        {
            double tileWidth = Math.Max(24, CompactTile.Width);
            CompactFolderTab.Width = Math.Max(14, Math.Round(tileWidth * 0.44));
            CompactFolderTab.Height = Math.Max(5, Math.Round(tileWidth * 0.14));
            CompactFolderTab.Margin = new Thickness(Math.Max(2, tileWidth * 0.06), -CompactFolderTab.Height + 1, 0, 0);
        }

        double shadowOpacity = CompactVisualStyles.LabelShadowOpacity(style, highContrast);
        CompactNameShadow1.Opacity = shadowOpacity;
        CompactNameShadow2.Opacity = shadowOpacity * 0.72;

        int maxLines = CompactVisualStyles.LabelMaxLines(style);
        TextWrapping wrap = maxLines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap;
        CompactNameText.MaxLines = maxLines;
        CompactNameText.TextWrapping = wrap;
        CompactNameShadow1.MaxLines = maxLines;
        CompactNameShadow1.TextWrapping = wrap;
        CompactNameShadow2.MaxLines = maxLines;
        CompactNameShadow2.TextWrapping = wrap;

        SyncCompactNameShadow();
        UpdateCompactSelectionPlateVisual();
    }

    internal void SyncCompactNameShadow()
    {
        string text = CompactNameText.Text;
        Visibility visibility = CompactNameText.Visibility;
        double fontSize = CompactNameText.FontSize;
        double maxWidth = CompactNameText.MaxWidth;
        FontFamily font = CompactNameText.FontFamily;

        CompactNameShadow1.Text = text;
        CompactNameShadow1.Visibility = visibility;
        CompactNameShadow1.FontSize = fontSize;
        CompactNameShadow1.MaxWidth = maxWidth;
        CompactNameShadow1.FontFamily = font;

        CompactNameShadow2.Text = text;
        CompactNameShadow2.Visibility = visibility;
        CompactNameShadow2.FontSize = fontSize;
        CompactNameShadow2.MaxWidth = maxWidth;
        CompactNameShadow2.FontFamily = font;
    }

    private void UpdateCompactSelectionPlateVisual()
    {
        CompactVisualStyle style = CompactVisualStyles.Normalize(_host.State.GlobalSettings.CompactVisualStyle);
        bool nativeBlend = CompactVisualStyles.UsesNativeBlendSurface(style);

        if (_organizerMultiSelected)
        {
            CompactNativeSelectionPlate.Background = new SolidColorBrush(Color.FromArgb(72, 0, 120, 212));
            CompactNativeSelectionPlate.BorderBrush = new SolidColorBrush(Color.FromArgb(230, 0, 120, 212));
            CompactNativeSelectionPlate.BorderThickness = new Thickness(1.5);
            return;
        }

        if (nativeBlend && _compactPointerHovered)
        {
            CompactNativeSelectionPlate.Background = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));
            CompactNativeSelectionPlate.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            CompactNativeSelectionPlate.BorderThickness = new Thickness(1);
            return;
        }

        CompactNativeSelectionPlate.Background = new SolidColorBrush(Colors.Transparent);
        CompactNativeSelectionPlate.BorderBrush = new SolidColorBrush(Colors.Transparent);
        CompactNativeSelectionPlate.BorderThickness = new Thickness(1);
    }

    private void CompactTile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _compactPointerHovered = true;
        UpdateCompactSelectionPlateVisual();
    }

    private void CompactTile_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _compactPointerHovered = false;
        UpdateCompactSelectionPlateVisual();
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
