using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinFlow.Core.Pipeline;
using Wpf.Ui.Appearance;

namespace WinFlow.Views;

public partial class OverlayWindow : Window
{
    private readonly AppState _appState;
    private readonly DispatcherTimer _timer;
    private DateTime _recordingStartTime;
    private Storyboard? _haloStoryboard;
    private Storyboard? _spinnerStoryboard;

    public OverlayWindow(AppState appState)
    {
        InitializeComponent();
        _appState = appState;

        ApplyHudTheme(ApplicationThemeManager.GetAppTheme());
        ApplicationThemeManager.Changed += OnAppThemeChanged;

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

    private void OnAppThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        Dispatcher.BeginInvoke(() => ApplyHudTheme(theme));
    }

    /// <summary>
    /// The HUD is a transparent layered window, so it paints its own Fluent-style
    /// palette instead of inheriting window brushes that assume an opaque backdrop.
    /// </summary>
    private void ApplyHudTheme(ApplicationTheme theme)
    {
        bool dark = theme != ApplicationTheme.Light;

        void SetBrush(string key, string hex) =>
            Resources[key] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

        SetBrush("HudBackgroundBrush", dark ? "#F21C1C1E" : "#F2FFFFFF");
        SetBrush("HudBorderBrush", dark ? "#28FFFFFF" : "#14000000");
        SetBrush("HudTextBrush", dark ? "#F5FFFFFF" : "#E4000000");
        SetBrush("HudTextSecondaryBrush", dark ? "#99FFFFFF" : "#8A000000");
        SetBrush("HudRecordingBrush", "#FFFF453A");
        SetBrush("HudAccentBrush", dark ? "#FF60CDFF" : "#FF0067C0");
        SetBrush("HudSuccessBrush", dark ? "#FF6CCB5F" : "#FF0F7B0F");
        SetBrush("HudErrorBrush", dark ? "#FFFF99A4" : "#FFC42B1C");
        SetBrush("HudCautionBrush", dark ? "#FFFCE100" : "#FF9D5D00");

        Waveform?.SetBarBrush((System.Windows.Media.Brush)Resources["HudTextBrush"]);
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
                    StartHaloBreathing();
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

    private void StartHaloBreathing()
    {
        var breathe = new DoubleAnimation
        {
            From = 0.2,
            To = 0.55,
            Duration = TimeSpan.FromMilliseconds(900),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        _haloStoryboard = new Storyboard();
        _haloStoryboard.Children.Add(breathe);
        Storyboard.SetTarget(breathe, RecordingHalo);
        Storyboard.SetTargetProperty(breathe, new PropertyPath(OpacityProperty));
        _haloStoryboard.Begin();
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
        _haloStoryboard?.Stop();
        _haloStoryboard = null;
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
        ApplicationThemeManager.Changed -= OnAppThemeChanged;
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
