using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;

namespace TuckPane.Controls;

public sealed class ConsoleSlider : Slider
{
    private static readonly Vector3 HoverScale = new(1.12f, 1.12f, 1f);
    private static readonly Vector3 PressedScale = new(0.94f, 0.94f, 1f);
    private static readonly Vector3 ThumbCenter = new(8f, 8f, 0f);

    private Thumb? _thumb;
    private readonly SolidColorBrush _normalBrush = new(Windows.UI.Color.FromArgb(255, 250, 249, 246));
    private readonly SolidColorBrush _borderBrush = new(Windows.UI.Color.FromArgb(255, 184, 183, 179));

    private bool _adjusting;
    private bool _thumbHovered;
    public event EventHandler? AdjustmentCompleted;

    public ConsoleSlider()
    {
        IsEnabledChanged += (_, _) => UpdateThumbOpacity();
        AddHandler(PointerPressedEvent, new PointerEventHandler(Slider_PointerPressed), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(Slider_PointerEnded), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(Slider_PointerEnded), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Slider_PointerEnded), true);
        LostFocus += (_, _) => CompleteAdjustment();
        Unloaded += (_, _) =>
        {
            _thumbHovered = false;
            CompleteAdjustment();
        };
    }

    public void SetModelValue(double value)
    {
        if (!_adjusting && Math.Abs(Value - value) > .000001) Value = value;
    }

    private void Slider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) _adjusting = true;
    }

    private void Slider_PointerEnded(object sender, PointerRoutedEventArgs e) => CompleteAdjustment();

    private void CompleteAdjustment()
    {
        if (!_adjusting) return;
        _adjusting = false;
        UpdateThumbVisualState();
        AdjustmentCompleted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnApplyTemplate()
    {
        if (_thumb is not null)
        {
            _thumb.PointerEntered -= Thumb_PointerEntered;
            _thumb.PointerExited -= Thumb_PointerExited;
            _thumb.PointerPressed -= Thumb_PointerPressed;
            _thumb.PointerReleased -= Thumb_PointerReleased;
            _thumb.PointerCanceled -= Thumb_PointerReleased;
        }
        _thumbHovered = false;
        base.OnApplyTemplate();
        _thumb = GetTemplateChild("HorizontalThumb") as Thumb;
        if (_thumb is null) return;
        _thumb.Width = 16;
        _thumb.Height = 16;
        _thumb.CenterPoint = ThumbCenter;
        _thumb.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(110) };
        _thumb.Template = (ControlTemplate)Application.Current.Resources["ConsoleSliderThumbTemplate"];
        _thumb.Background = _normalBrush;
        _thumb.BorderBrush = _borderBrush;
        _thumb.BorderThickness = new Thickness(1);
        _thumb.PointerEntered += Thumb_PointerEntered;
        _thumb.PointerExited += Thumb_PointerExited;
        _thumb.PointerPressed += Thumb_PointerPressed;
        _thumb.PointerReleased += Thumb_PointerReleased;
        _thumb.PointerCanceled += Thumb_PointerReleased;
        UpdateThumbVisualState();
    }

    private void Thumb_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _thumbHovered = true;
        if (_thumb is not null && IsEnabled)
            _thumb.Scale = _adjusting ? PressedScale : HoverScale;
    }

    private void Thumb_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _thumbHovered = false;
        if (_thumb is not null)
        {
            _thumb.Scale = _adjusting ? PressedScale : Vector3.One;
            UpdateThumbOpacity();
        }
    }

    private void Thumb_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_thumb is not null && IsEnabled)
        {
            _thumb.Opacity = .92;
            _thumb.Scale = PressedScale;
        }
    }

    private void Thumb_PointerReleased(object sender, PointerRoutedEventArgs e) => UpdateThumbVisualState();

    public void SetThumbPalette(Windows.UI.Color color, Windows.UI.Color borderColor)
    {
        if (_normalBrush.Color != color) _normalBrush.Color = color;
        if (_borderBrush.Color != borderColor) _borderBrush.Color = borderColor;
        if (_thumb is null) return;
        if (!ReferenceEquals(_thumb.Background, _normalBrush)) _thumb.Background = _normalBrush;
        if (!ReferenceEquals(_thumb.BorderBrush, _borderBrush)) _thumb.BorderBrush = _borderBrush;
        _thumb.BorderThickness = new Thickness(1);
    }

    private void UpdateThumbVisualState()
    {
        if (_thumb is null) return;
        _thumb.Scale = IsEnabled && _thumbHovered ? HoverScale : Vector3.One;
        UpdateThumbOpacity();
    }

    private void UpdateThumbOpacity()
    {
        if (_thumb is not null) _thumb.Opacity = IsEnabled ? 1 : .45;
    }
}
