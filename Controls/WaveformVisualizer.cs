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
    private const int BarCount = 11;
    private const double BarMinHeight = 3.0;
    private const double BarMaxHeight = 17.0;
    private const double SlotHeight = BarMaxHeight + 4;
    // 11 slots keep the same total width as the old 7×6 layout (42 DIPs):
    // thinner bars, tighter gaps.
    private const double SlotWidth = 42.0 / BarCount;
    private const double BarWidth = 2.0;

    private readonly WpfRectangle[] _bars = new WpfRectangle[BarCount];

    // Two incommensurate periods per bar: their sum never repeats visibly, so the
    // row shimmers organically instead of sweeping left-to-right like one wave.
    private static readonly double[] PeriodA = { 1.71, 2.33, 1.94, 2.12, 1.53, 2.47, 1.82, 2.05, 1.66, 2.29, 1.88 };
    private static readonly double[] PeriodB = { 0.83, 1.09, 0.71, 1.31, 0.97, 0.67, 1.17, 0.89, 1.23, 0.77, 1.05 };
    private static readonly double[] PhaseA  = { 0.0, 1.3, 2.6, 0.7, 1.9, 4.1, 5.3, 2.2, 3.6, 0.4, 4.8 };
    private static readonly double[] PhaseB  = { 2.1, 0.4, 5.0, 3.3, 1.1, 2.8, 0.9, 4.4, 1.7, 5.6, 3.0 };
    private static readonly double[] LevelWeight = { 0.55, 0.7, 0.82, 0.92, 0.98, 1.0, 0.97, 0.9, 0.78, 0.66, 0.5 };

    private readonly double[] _drift = new double[BarCount];
    private readonly double[] _driftTarget = new double[BarCount];
    private readonly Random _rng = new();
    private double _nextDriftPick;

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
            var slot = new Grid { Width = SlotWidth, Height = SlotHeight };
            _bars[i] = new WpfRectangle
            {
                Width = BarWidth,
                Height = BarMinHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
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

        // Every ~0.4s each bar picks a new random drift target and eases toward
        // it — a bounded random walk on top of the sines breaks any residual pattern.
        if (t >= _nextDriftPick)
        {
            _nextDriftPick = t + 0.4;
            for (int i = 0; i < BarCount; i++)
            {
                _driftTarget[i] = (_rng.NextDouble() - 0.5) * 0.16;
            }
        }

        for (int i = 0; i < BarCount; i++)
        {
            _drift[i] += (_driftTarget[i] - _drift[i]) * 0.06;

            double a = Math.Sin(2 * Math.PI * t / PeriodA[i] + PhaseA[i]);
            double b = Math.Sin(2 * Math.PI * t / PeriodB[i] + PhaseB[i]);
            double idle = 0.14 + 0.07 * a + 0.05 * b + _drift[i];

            double energy = Math.Clamp(idle + _displayLevel * LevelWeight[i], 0.0, 1.0);
            _bars[i].Height = BarMinHeight + (BarMaxHeight - BarMinHeight) * energy;
        }
    }
}
