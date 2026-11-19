using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using WinFlow.Core.Pipeline;

namespace WinFlow.Views;

/// <summary>
/// WhisperFlow-style HUD: a bare black capsule that hugs its content, showing only
/// white waveform dots while recording and a small spinner while transcribing.
/// No labels, no timer, no glow — everything outside the capsule is fully transparent.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly AppState _appState;

    public OverlayWindow(AppState appState)
    {
        InitializeComponent();
        _appState = appState;

        _appState.PhaseChanged += OnPhaseChanged;
        _appState.AudioLevelChanged += OnAudioLevelChanged;
        _appState.ErrorOccurred += OnErrorOccurred;
        _appState.NoticeRaised += OnNoticeRaised;

        Loaded += OnLoaded;
        SizeChanged += (_, _) => CenterAtTop();
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
        Left = (screenWidth - ActualWidth) / 2;
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
            Waveform.Visibility = Visibility.Collapsed;
            TranscribingPanel.Visibility = Visibility.Collapsed;
            FeedbackPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            StopSpinner();

            switch (phase)
            {
                case PipelinePhase.Recording:
                    Waveform.AudioLevel = 0;
                    Waveform.Visibility = Visibility.Visible;
                    ShowWithSlide();
                    break;

                case PipelinePhase.Transcribing:
                    TranscribingPanel.Visibility = Visibility.Visible;
                    Show();
                    StartSpinner();
                    break;

                case PipelinePhase.Feedback:
                    FeedbackPanel.Visibility = Visibility.Visible;
                    Show();
                    break;

                case PipelinePhase.Error:
                    ErrorPanel.Visibility = Visibility.Visible;
                    Show();
                    break;

                case PipelinePhase.Idle:
                default:
                    Hide();
                    break;
            }
        });
    }

    private void ShowWithSlide()
    {
        Show();
        CenterAtTop();
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
        PillTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
        PillBorder.BeginAnimation(OpacityProperty, fade);
    }

    private System.Windows.Media.Animation.Storyboard? _spinnerStoryboard;

    private void StartSpinner()
    {
        var rotate = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromMilliseconds(900),
            RepeatBehavior = RepeatBehavior.Forever
        };
        _spinnerStoryboard = new System.Windows.Media.Animation.Storyboard();
        _spinnerStoryboard.Children.Add(rotate);
        System.Windows.Media.Animation.Storyboard.SetTarget(rotate, SpinnerArc);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(rotate, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
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
        StopSpinner();
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
