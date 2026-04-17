using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CLCKTY.UI.Components;

public partial class TonePadControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ToneXProperty = DependencyProperty.Register(
        nameof(ToneX),
        typeof(double),
        typeof(TonePadControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnToneChanged));

    public static readonly DependencyProperty ToneYProperty = DependencyProperty.Register(
        nameof(ToneY),
        typeof(double),
        typeof(TonePadControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnToneChanged));

    private bool _isDragging;

    public TonePadControl()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateThumbPosition();
    }

    public double ToneX
    {
        get => (double)GetValue(ToneXProperty);
        set => SetValue(ToneXProperty, value);
    }

    public double ToneY
    {
        get => (double)GetValue(ToneYProperty);
        set => SetValue(ToneYProperty, value);
    }

    private static void OnToneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TonePadControl control)
        {
            control.UpdateThumbPosition();
        }
    }

    private void InputSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        InputSurface.CaptureMouse();
        UpdateFromPointer(e.GetPosition(InputSurface));
    }

    private void InputSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        InputSurface.ReleaseMouseCapture();
    }

    private void InputSurface_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        UpdateFromPointer(e.GetPosition(InputSurface));
    }

    private void InputSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateThumbPosition();
    }

    private void UpdateFromPointer(System.Windows.Point point)
    {
        var width = Math.Max(InputSurface.ActualWidth, 1);
        var height = Math.Max(InputSurface.ActualHeight, 1);

        var clampedX = Math.Clamp(point.X, 0, width);
        var clampedY = Math.Clamp(point.Y, 0, height);

        ToneX = ((clampedX / width) * 2.0) - 1.0;
        ToneY = 1.0 - ((clampedY / height) * 2.0);

        UpdateThumbPosition();
    }

    private void UpdateThumbPosition()
    {
        var width = InputSurface.ActualWidth;
        var height = InputSurface.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var x = ((Math.Clamp(ToneX, -1d, 1d) + 1d) * 0.5d) * width;
        var y = ((1d - Math.Clamp(ToneY, -1d, 1d)) * 0.5d) * height;

        Canvas.SetLeft(Thumb, x - (Thumb.Width / 2));
        Canvas.SetTop(Thumb, y - (Thumb.Height / 2));
    }
}
