using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using WinFlow.Core.Pipeline;

namespace WinFlow.Views;

/// <summary>
/// Two-state HUD: breathing white bars while listening, a spinner while transcribing.
/// Uses plain WPF AllowsTransparency (the UpdateLayeredWindow experiment never produced
/// visible pixels on this machine and is gone). The window hugs the pill closely and
/// fades/scales via standard WPF animations, which also drive the waveform per frame.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double CapsuleWidth = 96;
    private const double CapsuleHeight = 38;
    private const double CircleSize = 38;
    private const double ErrorWidth = 240;
    private const double ErrorHeight = 52;
    private static readonly Duration MorphDuration = new(TimeSpan.FromMilliseconds(140));
    private static readonly Duration FadeInDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly Duration FadeOutDuration = new(TimeSpan.FromMilliseconds(200));

    private readonly AppState _appState;
    private System.Windows.Threading.DispatcherTimer? _tickTimer;
    private double _spinnerAngle;
    private bool _shown;

    public OverlayWindow(AppState appState)
    {
        InitializeComponent();
        _appState = appState;
        Opacity = 0;

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
        // NOACTIVATE: never steals focus. TRANSPARENT: clicks pass through.
        // TOOLWINDOW: hidden from Alt-Tab.
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
    }

    public void CenterAtTop()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = (screenWidth - Width) / 2;
        Top = 24;
    }

    public void CloseForShutdown() => Close();

    // ---------- per-frame tick: waveform + spinner ----------

    private void EnsureTick()
    {
        if (_tickTimer == null)
        {
            _tickTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _tickTimer.Tick += (_, _) =>
            {
                if (Waveform.Visibility == Visibility.Visible)
                {
                    Waveform.Tick();
                }
                if (TranscribingPanel.Visibility == Visibility.Visible)
                {
                    _spinnerAngle = (_spinnerAngle + 6.4) % 360;
                    SpinnerRotation.Angle = _spinnerAngle;
                }
            };
        }
        if (!_tickTimer.IsEnabled) _tickTimer.Start();
    }

    private void StopTickIfIdle()
    {
        if (!_shown)
        {
            _tickTimer?.Stop();
        }
    }

    // ---------- state machine ----------

    private void OnPhaseChanged(PipelinePhase phase)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (phase)
            {
                case PipelinePhase.Recording:
                    Waveform.AudioLevel = 0;
                    SetContent(waveform: true);
                    MorphTo(CapsuleWidth, CapsuleHeight, CapsuleHeight / 2);
                    ShowAnimated();
                    break;

                case PipelinePhase.Transcribing:
                case PipelinePhase.Inserting:
                    SetContent(spinner: true);
                    MorphTo(CircleSize, CircleSize, CircleSize / 2);
                    ShowAnimated();
                    break;

                case PipelinePhase.Error:
                    SetContent(advisory: true);
                    MorphTo(ErrorWidth, ErrorHeight, 14);
                    ShowAnimated();
                    break;

                case PipelinePhase.Feedback:
                case PipelinePhase.Idle:
                default:
                    HideAnimated();
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
    }

    private void ShowAnimated()
    {
        CenterAtTop();
        if (!_shown)
        {
            _shown = true;
            Show();
            var fadeIn = new DoubleAnimation(0, 1, FadeInDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(OpacityProperty, fadeIn);

            PillScale.ScaleX = 0.85;
            PillScale.ScaleY = 0.85;
            var grow = new DoubleAnimation(0.85, 1, FadeInDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            PillScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
            PillScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
        }
        EnsureTick();
    }

    private void HideAnimated()
    {
        if (!_shown) return;
        _shown = false;

        var fadeOut = new DoubleAnimation(Opacity, 0, FadeOutDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            Hide();
            StopTickIfIdle();
        };
        BeginAnimation(OpacityProperty, fadeOut);

        var shrink = new DoubleAnimation(1, 0.9, FadeOutDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        PillScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, shrink);
        PillScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, shrink);
    }

    /// <summary>Animates width, height and corner radius together (capsule <-> circle).</summary>
    private void MorphTo(double width, double height, double radius)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        PillBorder.BeginAnimation(WidthProperty, new DoubleAnimation { To = width, Duration = MorphDuration, EasingFunction = ease });
        PillBorder.BeginAnimation(HeightProperty, new DoubleAnimation { To = height, Duration = MorphDuration, EasingFunction = ease });

        // WPF has no CornerRadius animation; snap it at the morph midpoint where the
        // shape change is fastest and the corner jump is least visible.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = MorphDuration.TimeSpan / 2 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            PillBorder.CornerRadius = new CornerRadius(radius);
        };
        timer.Start();
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
                SetContent(advisory: true);
                ElevatedBadge.Visibility = Visibility.Visible;
                MorphTo(ErrorWidth, ErrorHeight, 14);
                ShowAnimated();
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
        _tickTimer?.Stop();
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
