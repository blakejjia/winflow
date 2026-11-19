using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using WpfEllipse = System.Windows.Shapes.Ellipse;

namespace WinFlow.Controls;

/// <summary>
/// A row of white dots that swell with the microphone level — the WhisperFlow look.
/// At silence the dots rest small; speech makes them bloom toward the center dot.
/// </summary>
public class WaveformVisualizer : StackPanel
{
    private const int DotCount = 7;
    private const double MinSize = 4.0;
    private const double MaxSize = 16.0;

    private readonly WpfEllipse[] _dots = new WpfEllipse[DotCount];

    // Center-weighted: the middle dots bloom most, edges least.
    private static readonly double[] Multipliers = { 0.45, 0.7, 0.9, 1.0, 0.9, 0.7, 0.45 };

    public static readonly DependencyProperty AudioLevelProperty =
        DependencyProperty.Register(
            nameof(AudioLevel),
            typeof(float),
            typeof(WaveformVisualizer),
            new PropertyMetadata(0.0f, OnAudioLevelChanged));

    public float AudioLevel
    {
        get => (float)GetValue(AudioLevelProperty);
        set => SetValue(AudioLevelProperty, value);
    }

    public WaveformVisualizer()
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal;
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        VerticalAlignment = System.Windows.VerticalAlignment.Center;
        Height = MaxSize + 2;

        for (int i = 0; i < DotCount; i++)
        {
            // Fixed-size slot so growing dots don't shift their neighbors.
            var slot = new Grid
            {
                Width = MaxSize,
                Height = MaxSize,
                Margin = new Thickness(1, 0, 1, 0)
            };
            _dots[i] = new WpfEllipse
            {
                Width = MinSize,
                Height = MinSize,
                Fill = System.Windows.Media.Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            slot.Children.Add(_dots[i]);
            Children.Add(slot);
        }
    }

    private static void OnAudioLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WaveformVisualizer visualizer && e.NewValue is float level)
        {
            visualizer.UpdateDots(level);
        }
    }

    private void UpdateDots(float level)
    {
        // level is already perceptually shaped (AudioLevelCalculator.ShapeForDisplay).
        for (int i = 0; i < DotCount; i++)
        {
            double target = MinSize + (MaxSize - MinSize) * Math.Clamp(level * Multipliers[i], 0.0, 1.0);
            AnimateDot(_dots[i], target);
        }
    }

    private static void AnimateDot(WpfEllipse dot, double target)
    {
        // Snapshot the current animated size and replace the running animation so
        // rapid buffer updates blend smoothly instead of stacking.
        double current = dot.Width;
        dot.BeginAnimation(WidthProperty, null);
        dot.BeginAnimation(HeightProperty, null);
        dot.Width = current;
        dot.Height = current;

        var duration = TimeSpan.FromMilliseconds(110);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        dot.BeginAnimation(WidthProperty, new DoubleAnimation { From = current, To = target, Duration = duration, EasingFunction = easing });
        dot.BeginAnimation(HeightProperty, new DoubleAnimation { From = current, To = target, Duration = duration, EasingFunction = easing });
    }
}
