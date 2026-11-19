using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinFlow.Core.Pipeline;

namespace WinFlow.Views;

/// <summary>
/// Minimal black-pill HUD: white waveform, white text, no glow or shadow.
/// Everything outside the pill is fully transparent and click-through.
/// The look is intentionally fixed (black/white) regardless of app theme.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly AppState _appState;
    private readonly DispatcherTimer _timer;
    private DateTime _recordingStartTime;
    private Storyboard? _spinnerStoryboard;

    public OverlayWindow(AppState appState)
    {
        InitializeComponent();
        _appState = appState;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (s, e) =>
        {
            var elapsed = DateTime.Now - _recordingStartTime;
            TimerText.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
        };

        _appState.PhaseChanged += OnPhaseChanged;
        _appState.AudioLevelChanged += OnAudioLevelChanged;
        _appState.ErrorOccurred += OnErrorOccurred;
        _appState.NoticeRaised += OnNoticeRaised;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CenterAtTop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

        // WS_EX_NOACTIVATE = 0x08000000 (never takes focus)
        // WS_EX_TRANSPARENT = 0x00000020 (mouse clicks pass through)
        // WS_EX_TOOLWINDOW = 0x00000080 (hidden from Alt-Tab)
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
    }

    public void CenterAtTop()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = (screenWidth - Width) / 2;
        Top = 28;
    }

    private void OnPhaseChanged(PipelinePhase phase)
    {
        // Async marshal: pipeline events can originate on input/audio threads and must
        // never block waiting on the UI thread.
        Dispatcher.BeginInvoke(() =>
        {
            if (phase == PipelinePhase.Recording || phase == PipelinePhase.Idle)
            {
                ElevatedBadge.Visibility = Visibility.Collapsed;
            }
            RecordingPanel.Visibility = Visibility.Collapsed;
            TranscribingPanel.Visibility = Visibility.Collapsed;
            FeedbackPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;

            StopAnimations();

            switch (phase)
            {
                case PipelinePhase.Recording:
                    _recordingStartTime = DateTime.Now;
                    TimerText.Text = "0:00";
                    _timer.Start();
                    RecordingPanel.Visibility = Visibility.Visible;
                    CenterAtTop();
                    ShowWithSlide();
                    break;

                case PipelinePhase.Transcribing:
                    _timer.Stop();
                    TranscribingPanel.Visibility = Visibility.Visible;
                    Show();
                    StartSpinner();
                    break;

                case PipelinePhase.Feedback:
                    _timer.Stop();
                    FeedbackPanel.Visibility = Visibility.Visible;
                    Show();
                    break;

                case PipelinePhase.Error:
                    _timer.Stop();
                    ErrorPanel.Visibility = Visibility.Visible;
                    Show();
                    break;

                case PipelinePhase.Idle:
                default:
                    _timer.Stop();
                    Hide();
                    break;
            }
        });
    }

    private void ShowWithSlide()
    {
        Show();
        var slide = new DoubleAnimation
        {
            From = -10,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(160)
        };
        PillTransform.BeginAnimation(TranslateTransform.YProperty, slide);
        PillBorder.BeginAnimation(OpacityProperty, fade);
    }

    private void StartSpinner()
    {
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

    private void StopAnimations()
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
            ElevatedBadge.Visibility = notice == PipelineNotice.TargetElevated
                ? Visibility.Visible
                : Visibility.Collapsed;
        });
    }

    private void OnErrorOccurred(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ErrorText.Text = message;
            ErrorText.ToolTip = message;
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        StopAnimations();
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
