using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace WinFlow.Controls;

/// <summary>
/// A row of slim white bars that breathe. Every bar sways gently on its own slow
/// sine even in silence; the microphone level lifts them all together. The owner
/// calls <see cref="Tick"/> once per frame (the HUD's layered-window loop), so
/// motion is continuous and organic rather than stepped.
/// </summary>
public class WaveformVisualizer : StackPanel
{
    private const int BarCount = 5;
    private const double BarMinHeight = 4.0;
    private const double BarMaxHeight = 22.0;
    private const double SlotHeight = BarMaxHeight + 4;

    private readonly WpfRectangle[] _bars = new WpfRectangle[BarCount];

    // Per-bar sway: distinct periods and phases so they never move in lockstep.
    private static readonly double[] SwayPeriodSeconds = { 1.7, 2.3, 1.9, 2.1, 1.5 };
    private static readonly double[] SwayPhase = { 0.0, 1.3, 2.6, 0.7, 1.9 };
    private static readonly double[] LevelWeight = { 0.75, 0.95, 1.0, 0.9, 0.7 };

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _displayLevel;   // smoothed toward _targetLevel each frame
    private double _targetLevel;

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
        Height = SlotHeight;

        for (int i = 0; i < BarCount; i++)
        {
            var slot = new Grid { Width = 8, Height = SlotHeight };
            _bars[i] = new WpfRectangle
            {
                Width = 3.5,
                Height = BarMinHeight,
                RadiusX = 1.75,
                RadiusY = 1.75,
                Fill = System.Windows.Media.Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            slot.Children.Add(_bars[i]);
            Children.Add(slot);
        }

    }

    private static void OnAudioLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WaveformVisualizer v && e.NewValue is float level)
        {
            v._targetLevel = Math.Clamp(level, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Advances the animation one frame. The HUD drives this from its layered-window
    /// frame loop (CompositionTarget.Rendering does not run for UpdateLayeredWindow
    /// surfaces, so the owner ticks us explicitly).
    /// </summary>
    public void Tick()
    {
        double t = _clock.Elapsed.TotalSeconds;

        // Attack fast, release slow — like a real VU meter's ballistics.
        double rate = _targetLevel > _displayLevel ? 0.35 : 0.08;
        _displayLevel += (_targetLevel - _displayLevel) * rate;

        for (int i = 0; i < BarCount; i++)
        {
            // Idle breathing: each bar sways ±~12% of range on its own slow sine.
            double sway = 0.5 + 0.5 * Math.Sin(2 * Math.PI * t / SwayPeriodSeconds[i] + SwayPhase[i]);
            double idle = 0.10 + 0.12 * sway;

            double energy = Math.Clamp(idle + _displayLevel * LevelWeight[i], 0.0, 1.0);
            _bars[i].Height = BarMinHeight + (BarMaxHeight - BarMinHeight) * energy;
        }
    }
}
