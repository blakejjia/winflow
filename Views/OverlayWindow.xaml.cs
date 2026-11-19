using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WinFlow.Core.Pipeline;

namespace WinFlow.App.Views;

public partial class OverlayWindow : Window
{
    private readonly AppState _appState;
    private readonly DispatcherTimer _timer;
    private DateTime _recordingStartTime;

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
            TimerText.Text = $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        };

        _appState.PhaseChanged += OnPhaseChanged;
        _appState.AudioLevelChanged += OnAudioLevelChanged;
        _appState.ErrorOccurred += OnErrorOccurred;

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
        Dispatcher.Invoke(() =>
        {
            RecordingPanel.Visibility = Visibility.Collapsed;
            TranscribingPanel.Visibility = Visibility.Collapsed;
            FeedbackPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;

            switch (phase)
            {
                case PipelinePhase.Recording:
                    _recordingStartTime = DateTime.Now;
                    TimerText.Text = "00:00";
                    _timer.Start();
                    RecordingPanel.Visibility = Visibility.Visible;
                    CenterAtTop();
                    Show();
                    break;

                case PipelinePhase.Transcribing:
                    _timer.Stop();
                    TranscribingPanel.Visibility = Visibility.Visible;
                    Show();
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

    private void OnAudioLevelChanged(float level)
    {
        Dispatcher.Invoke(() =>
        {
            Waveform.AudioLevel = level;
        });
    }

    private void OnErrorOccurred(string message)
    {
        Dispatcher.Invoke(() =>
        {
            ErrorText.Text = $"⚠️ {message}";
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _appState.PhaseChanged -= OnPhaseChanged;
        _appState.AudioLevelChanged -= OnAudioLevelChanged;
        _appState.ErrorOccurred -= OnErrorOccurred;
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
