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

    private void UpdateBars(float level)
    {
        // level is 0.0 to 1.0
        double baseHeight = 5.0;
        double maxHeight = 24.0;

        for (int i = 0; i < _bars.Length; i++)
        {
            double target = baseHeight + (maxHeight - baseHeight) * Math.Clamp(level * Multipliers[i] * 2.5, 0.0, 1.0);
            _bars[i].Height = target;
        }
    }
}
