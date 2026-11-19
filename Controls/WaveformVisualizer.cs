using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace WinFlow.Controls;

public class WaveformVisualizer : StackPanel
{
    private readonly WpfRectangle[] _bars = new WpfRectangle[5];
    private static readonly double[] Multipliers = { 0.6, 1.1, 1.4, 0.9, 0.7 };

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
                Width = 4,
                Height = 6,
                RadiusX = 2,
                RadiusY = 2,
                Margin = new Thickness(2, 0, 2, 0),
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(240, 255, 255, 255)),
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

    /// <summary>Lets the HUD tint the bars to match its light/dark palette.</summary>
    public void SetBarBrush(System.Windows.Media.Brush brush)
    {
        foreach (var bar in _bars)
        {
            bar.Fill = brush;
        }
    }

    private void UpdateBars(float level)
    {
        // level is 0.0 to 1.0. Ease each bar toward its target so the meter feels
        // like a living thing rather than a jumping equalizer.
        double baseHeight = 5.0;
        double maxHeight = 24.0;

        for (int i = 0; i < _bars.Length; i++)
        {
            double target = baseHeight + (maxHeight - baseHeight) * Math.Clamp(level * Multipliers[i] * 2.5, 0.0, 1.0);

            // Animate from wherever the previous animation currently is, so successive
            // buffer updates blend instead of snapping.
            var animation = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            };
            _bars[i].BeginAnimation(HeightProperty, animation, System.Windows.Media.Animation.HandoffBehavior.Compose);
        }
    }
}
