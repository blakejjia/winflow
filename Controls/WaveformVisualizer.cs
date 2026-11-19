using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace WinFlow.Controls;

public class WaveformVisualizer : StackPanel
{
    private readonly WpfRectangle[] _bars = new WpfRectangle[5];
    private static readonly double[] Multipliers = { 0.7, 1.0, 1.25, 0.95, 0.75 };

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
        Height = 28;

        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new WpfRectangle
            {
                Width = 3,
                Height = 4,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(2, 0, 2, 0),
                Fill = System.Windows.Media.Brushes.White,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            Children.Add(_bars[i]);
        }
    }

    private static void OnAudioLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WaveformVisualizer visualizer && e.NewValue is float level)
        {
            visualizer.UpdateBars(level);
        }
    }

    /// <summary>Lets the HUD tint the bars to match its palette.</summary>
    public void SetBarBrush(System.Windows.Media.Brush brush)
    {
        foreach (var bar in _bars)
        {
            bar.Fill = brush;
        }
    }

    private void UpdateBars(float level)
    {
        // level is already perceptually shaped (see AudioLevelCalculator.ShapeForDisplay).
        double baseHeight = 4.0;
        double maxHeight = 24.0;

        for (int i = 0; i < _bars.Length; i++)
        {
            double target = baseHeight + (maxHeight - baseHeight) * Math.Clamp(level * Multipliers[i], 0.0, 1.0);
            AnimateBar(i, target);
        }
    }

    private void AnimateBar(int index, double target)
    {
        // Snapshot the bar's CURRENT animated height and replace any running animation,
        // so rapid buffer updates blend smoothly instead of stacking animations.
        double current = _bars[index].Height;
        _bars[index].BeginAnimation(HeightProperty, null);
        _bars[index].Height = current;

        var animation = new DoubleAnimation
        {
            From = current,
            To = target,
            Duration = TimeSpan.FromMilliseconds(110),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        _bars[index].BeginAnimation(HeightProperty, animation);
    }
}
