using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using WinFlow.Core.Pipeline;

namespace WinFlow.Views;

/// <summary>
/// Two-state HUD: breathing white bars while listening, a small spinner while
/// transcribing. The black pill morphs between capsule and circle in ~120ms —
/// never a hard cut. Success returns straight to hidden; errors/advisories are
/// the only states that show words.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double CapsuleWidth = 96;
    private const double CapsuleHeight = 38;
    private const double CircleSize = 38;
    private const double ErrorWidth = 240;
    private const double ErrorHeight = 52;
    private static readonly Duration MorphDuration = new(TimeSpan.FromMilliseconds(120));

    private readonly AppState _appState;
    private Storyboard? _spinnerStoryboard;

    public OverlayWindow(AppState appState)
    {
        InitializeComponent();
        _appState = appState;

        _appState.PhaseChanged += OnPhaseChanged;
        _appState.AudioLevelChanged += OnAudioLevelChanged;
        _appState.ErrorOccurred += OnErrorOccurred;
        _appState.NoticeRaised += OnNoticeRaised;

        Loaded += (_, _) => CenterAtTop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

        // WS_EX_NOACTIVATE = never takes focus; WS_EX_TRANSPARENT = click-through;
        // WS_EX_TOOLWINDOW = hidden from Alt-Tab.
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
    }

    public void CenterAtTop()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = (screenWidth - Width) / 2;
        Top = 24;
    }

    private void OnPhaseChanged(PipelinePhase phase)
    {
        // Async marshal: pipeline events can originate on input/audio threads.
        Dispatcher.BeginInvoke(() =>
        {
            switch (phase)
            {
                case PipelinePhase.Recording:
                    Waveform.AudioLevel = 0;
                    SetContent(waveform: true);
                    MorphTo(CapsuleWidth, CapsuleHeight, radius: CapsuleHeight / 2);
                    Show();
                    break;

                case PipelinePhase.Transcribing:
                case PipelinePhase.Inserting:
                    SetContent(spinner: true);
                    MorphTo(CircleSize, CircleSize, radius: CircleSize / 2);
                    StartSpinner();
                    Show();
                    break;

                case PipelinePhase.Error:
                    // Error text is set by OnErrorOccurred; give it room.
                    SetContent(advisory: true);
                    MorphTo(ErrorWidth, ErrorHeight, radius: 14);
                    Show();
                    break;

                case PipelinePhase.Feedback:
                case PipelinePhase.Idle:
                default:
                    // No success state: shrink away quickly, then hide.
                    StopSpinner();
                    MorphTo(CircleSize, CircleSize, radius: CircleSize / 2, onCompleted: Hide);
                    break;
            }
        });
    }

    private void SetContent(bool waveform = false, bool spinner = false, bool advisory = false)
    {
        Waveform.Visibility = waveform ? Visibility.Visible : Visibility.Collapsed;
        TranscribingPanel.Visibility = spinner ? Visibility.Visible : Visibility.Collapsed;
        AdvisoryPanel.Visibility = advisory ? Visibility.Visible : Visibility.Collapsed;
        if (!advisory)
        {
            ElevatedBadge.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
        }
        if (!spinner) StopSpinner();
    }

    private System.Windows.Threading.DispatcherTimer? _morphTimer;
    private System.Diagnostics.Stopwatch? _morphClock;
    private (double W, double H, double R) _morphFrom, _morphTo;
    private Action? _morphCompleted;

    /// <summary>Animates width, height and corner radius together — the capsule
    /// visibly rounds into a circle (and back) instead of swapping images.
    /// WPF has no CornerRadius animation, so all three are driven per frame.</summary>
    private void MorphTo(double width, double height, double radius, Action? onCompleted = null)
    {
        _morphFrom = (PillBorder.Width, PillBorder.Height, PillBorder.CornerRadius.TopLeft);
        _morphTo = (width, height, radius);
        _morphCompleted = onCompleted;
        _morphClock = System.Diagnostics.Stopwatch.StartNew();

        if (_morphTimer == null)
        {
            _morphTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            _morphTimer.Tick += OnMorphTick;
        }
        _morphTimer.Start();
    }

    private void OnMorphTick(object? sender, EventArgs e)
    {
        double t = Math.Clamp(_morphClock!.Elapsed.TotalMilliseconds / MorphDuration.TimeSpan.TotalMilliseconds, 0, 1);
        // Cubic ease-in-out, matching the rest of the app's motion.
        double k = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

        PillBorder.Width = _morphFrom.W + (_morphTo.W - _morphFrom.W) * k;
        PillBorder.Height = _morphFrom.H + (_morphTo.H - _morphFrom.H) * k;
        double r = _morphFrom.R + (_morphTo.R - _morphFrom.R) * k;
        PillBorder.CornerRadius = new CornerRadius(r);

        if (t >= 1)
        {
            _morphTimer!.Stop();
            var done = _morphCompleted;
            _morphCompleted = null;
            done?.Invoke();
        }
    }

    private void StartSpinner()
    {
        if (_spinnerStoryboard != null) return;
        var rotate = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromMilliseconds(900),
            RepeatBehavior = RepeatBehavior.Forever
        };
        _spinnerStoryboard = new Storyboard();
        _spinnerStoryboard.Children.Add(rotate);
        Storyboard.SetTarget(rotate, SpinnerArc);
        Storyboard.SetTargetProperty(rotate, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        _spinnerStoryboard.Begin();
    }

    private void StopSpinner()
    {
        _spinnerStoryboard?.Stop();
        _spinnerStoryboard = null;
    }

    private void OnAudioLevelChanged(float level)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Waveform.AudioLevel = level;
        });
    }

    private void OnNoticeRaised(PipelineNotice notice)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (notice == PipelineNotice.TargetElevated)
            {
                ElevatedBadge.Visibility = Visibility.Visible;
                SetContent(advisory: true);
                ElevatedBadge.Visibility = Visibility.Visible;
                MorphTo(ErrorWidth, ErrorHeight, radius: 14);
                Show();
            }
            else
            {
                ElevatedBadge.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void OnErrorOccurred(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ErrorText.Text = message;
            ErrorText.ToolTip = message;
            ErrorText.Visibility = Visibility.Visible;
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        StopSpinner();
        _morphTimer?.Stop();
        _appState.PhaseChanged -= OnPhaseChanged;
        _appState.AudioLevelChanged -= OnAudioLevelChanged;
        _appState.ErrorOccurred -= OnErrorOccurred;
        _appState.NoticeRaised -= OnNoticeRaised;
        base.OnClosed(e);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
