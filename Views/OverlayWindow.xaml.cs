using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using WinFlow.Audio;
using WinFlow.Core.Pipeline;

namespace WinFlow.Views;

/// <summary>
/// Two-state HUD on a true per-pixel-alpha layered window — only the black pill
/// exists on screen, no halo rectangle. The HWND is a dedicated native window
/// (registered class, WS_EX_LAYERED), NOT the WPF HwndSource: WPF's composition
/// target redirects its own HWND and every UpdateLayeredWindow on it fails with
/// ERROR_INVALID_PARAMETER — the root cause of the earlier "never renders" attempts.
/// The content Canvas is laid out manually, rendered with RenderTargetBitmap at the
/// monitor's DPI, and presented in physical pixels; fades run on layered alpha.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double CapsuleWidth = 76;
    private const double CapsuleHeight = 30;
    private const double MeetingWidth = 116;
    private const double CircleSize = 30;
    // Idle presence: a slim horizontal bar parallel to the screen's top edge,
    // at ~25% opacity so the user always knows WinFlow is running.
    // All state changes morph/fade from wherever we are.
    private const double IdleWidth = 34;
    private const double IdleHeight = 8;
    private const double IdleAlpha = 64;
    private const double ErrorWidth = 240;
    private const double ErrorHeight = 52;
    private static readonly TimeSpan MorphTime = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan FadeInTime = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan FadeOutTime = TimeSpan.FromMilliseconds(360);

    private readonly AppState _appState;
    private readonly IMeetingRecorder? _meetingRecorder;

    // Meeting recording indicator: persistent pill while a meeting records.
    private bool _meetingRecording;
    private PipelinePhase _lastPhase = PipelinePhase.Idle;
    private System.Windows.Threading.DispatcherTimer? _meetingClock;

    private IntPtr _hwnd;
    private System.Windows.Threading.DispatcherTimer? _frameTimer;
    private double _spinnerAngle;

    // Morph state
    private (double W, double H, double R) _morphFrom, _morphTo;
    private System.Diagnostics.Stopwatch? _morphClock;
    private Action? _morphCompleted;

    // Fade state (layered alpha 0..255)
    private double _alpha;
    private double _alphaFrom, _alphaTo;
    private System.Diagnostics.Stopwatch? _fadeClock;
    private TimeSpan _fadeTime = FadeInTime;
    private Action? _fadeCompleted;

    private int _pushCount;
    private double _dpiScale = 1.0;
    private int _leftPhys, _topPhys;
    private bool _hugTop = true;
    private bool _isClosed;
    private bool _inRecoveryRecreate;
    private System.Windows.Threading.DispatcherTimer? _recoveryTimer;
    private int _recoveryStep;
    private bool _recoveryNeedsRecreate;
    private static readonly TimeSpan[] RecoveryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1500)
    ];

    public OverlayWindow(AppState appState, IMeetingRecorder? meetingRecorder = null)
    {
        InitializeComponent();
        _appState = appState;
        _meetingRecorder = meetingRecorder;

        _appState.PhaseChanged += OnPhaseChanged;
        _appState.AudioLevelChanged += OnAudioLevelChanged;
        _appState.ErrorOccurred += OnErrorOccurred;
        _appState.NoticeRaised += OnNoticeRaised;
        if (_meetingRecorder != null)
        {
            _meetingRecorder.RecordingChanged += OnMeetingRecordingChanged;
        }

        // The idle presence bar must exist from launch, not from the first
        // CapsLock press. WPF defers HWND creation for a never-Shown window, so
        // OnSourceInitialized (and the old lazy PushPixels path) never ran until
        // a dictation phase. Create the layered surface explicitly here.
        EnsureLayeredWindow();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Autostart (--minimized) can race Explorer/DWM during login; verify again
        // once the desktop settles.
        ScheduleRecovery(recreate: false, immediate: false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnsureLayeredWindow();
    }

    private bool _layeredInitialized;
    private static ushort _hudClassAtom;
    private static WndProcDelegate? _hudWndProc; // keep alive for the process lifetime
    private static uint _taskbarCreatedMsg;
    private static readonly Dictionary<IntPtr, OverlayWindow> _windowsByHwnd = new();

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void EnsureLayeredWindow()
    {
        if (_layeredInitialized) return;

        CreateLayeredWindowHandle();
        if (_hwnd == IntPtr.Zero) return;

        CenterAtTop();
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);

        // First paint: snap straight to the fixed Idle definition, no animation.
        SnapTo(IdleDef);
        PushPixels();
    }

    private void CreateLayeredWindowHandle()
    {
        // A dedicated native window, NOT the WPF HwndSource: WPF's composition
        // target redirects its HWND and every UpdateLayeredWindow on it fails
        // with ERROR_INVALID_PARAMETER. A plain registered class has no such
        // redirection, so per-pixel alpha presentation works.
        if (_hudClassAtom == 0)
        {
            _hudWndProc = HudWndProc;
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_hudWndProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = "WinFlowHudLayeredClass"
            };
            _hudClassAtom = RegisterClass(ref wc);
            if (_hudClassAtom == 0) return;
        }

        uint sysDpi = GetDpiForSystem();
        _dpiScale = sysDpi > 0 ? sysDpi / 96.0 : 1.0;

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            "WinFlowHudLayeredClass", "WinFlow HUD",
            WS_POPUP,
            0, 0, (int)Math.Round(Width * _dpiScale), (int)Math.Round(Height * _dpiScale),
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;

        _windowsByHwnd[_hwnd] = this;
        _layeredInitialized = true;
    }

    private void RecreateLayeredWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            _windowsByHwnd.Remove(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _layeredInitialized = false;
        CreateLayeredWindowHandle();
    }

    private static IntPtr HudWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_NCDESTROY)
            {
                _windowsByHwnd.Remove(hWnd);
            }
            else if (ShouldRecoverOnWindowMessage(msg, wParam, _taskbarCreatedMsg)
                && _windowsByHwnd.TryGetValue(hWnd, out var owner))
            {
                owner.ScheduleRecovery(recreate: ShouldRecreateOnWindowMessage(msg, _taskbarCreatedMsg));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WinFlow HUD] WndProc error: {ex.Message}");
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>Applies a state definition instantly (first paint only).</summary>
    private void SnapTo(HudStateDef def)
    {
        _hugTop = def.HugTop;
        SetContent(waveform: def.Waveform, spinner: def.Spinner, advisory: def.Advisory, meeting: def.Meeting);
        PillBorder.Width = def.W;
        PillBorder.Height = def.H;
        // Radius = half the SHORT side, or WPF clamps it into an ellipse.
        PillBorder.CornerRadius = new CornerRadius(def.R);
        System.Windows.Controls.Canvas.SetLeft(PillBorder, (RootCanvas.Width - def.W) / 2);
        System.Windows.Controls.Canvas.SetTop(PillBorder, def.HugTop ? 0 : (RootCanvas.Height - def.H) / 2);
        _alpha = def.Alpha;
    }

    public void CenterAtTop()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        // Resolve the current primary monitor first so a previously unplugged or
        // rearranged display never leaves the HUD off-screen.
        var monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        if (monitor == IntPtr.Zero)
        {
            monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTOPRIMARY);
        }

        uint dpi = GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = GetDpiForSystem();
        _dpiScale = dpi > 0 ? dpi / 96.0 : 1.0;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        Rect workPhys;
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref mi))
        {
            workPhys = new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top,
                mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);
        }
        else
        {
            workPhys = new Rect(0, 0,
                SystemParameters.PrimaryScreenWidth * _dpiScale,
                SystemParameters.PrimaryScreenHeight * _dpiScale);
        }

        int wPhys = (int)Math.Round(Width * _dpiScale);
        int hPhys = (int)Math.Round(Height * _dpiScale);
        _leftPhys = (int)(workPhys.Left + (workPhys.Width - wPhys) / 2);
        _topPhys = (int)(workPhys.Top + 5 * _dpiScale);

        // Re-assert HWND_TOPMOST without stealing focus; sleep/lock/Explorer restarts
        // frequently demote WS_EX_TOPMOST tool windows.
        SetWindowPos(_hwnd, HWND_TOPMOST, _leftPhys, _topPhys, wPhys, hPhys,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // Moving onto the primary monitor can change the window's DPI on mixed-DPI setups.
        uint newDpi = GetDpiForWindow(_hwnd);
        if (newDpi > 0 && Math.Abs(newDpi / 96.0 - _dpiScale) > 0.001)
        {
            _dpiScale = newDpi / 96.0;
            wPhys = (int)Math.Round(Width * _dpiScale);
            hPhys = (int)Math.Round(Height * _dpiScale);
            _leftPhys = (int)(workPhys.Left + (workPhys.Width - wPhys) / 2);
            _topPhys = (int)(workPhys.Top + 5 * _dpiScale);
            SetWindowPos(_hwnd, HWND_TOPMOST, _leftPhys, _topPhys, wPhys, hPhys,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        if (!IsWindowVisible(_hwnd))
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        }

        // Keep WPF DIP state roughly in sync for anything that reads Left/Top.
        Left = _leftPhys / _dpiScale;
        Top = _topPhys / _dpiScale;
    }

    /// <summary>
    /// Ensures the native layered HUD window exists, sits topmost on the primary
    /// monitor, and is freshly presented. Safe to call from any thread.
    /// </summary>
    public void RecoverOverlay(bool recreate = false)
    {
        if (_isClosed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => RecoverOverlay(recreate));
            return;
        }

        if (recreate || _hwnd == IntPtr.Zero || !IsWindow(_hwnd))
        {
            RecreateLayeredWindow();
            if (_hwnd == IntPtr.Zero) return;
        }

        CenterAtTop();
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        PushPixels();
    }

    internal void ScheduleRecovery(bool recreate = false, bool immediate = true)
    {
        if (_isClosed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ScheduleRecovery(recreate, immediate));
            return;
        }

        _recoveryNeedsRecreate |= recreate;
        if (immediate)
        {
            RecoverOverlay(recreate: recreate);
        }

        if (_recoveryTimer == null)
        {
            _recoveryTimer = new System.Windows.Threading.DispatcherTimer();
            _recoveryTimer.Tick += OnRecoveryTimerTick;
        }

        _recoveryTimer.Stop();
        _recoveryStep = 0;
        _recoveryTimer.Interval = RecoveryDelays[0];
        _recoveryTimer.Start();
    }

    private void OnRecoveryTimerTick(object? sender, EventArgs e)
    {
        _recoveryTimer?.Stop();
        if (_isClosed) return;

        if (_recoveryStep == 0)
        {
            bool doRecreate = _recoveryNeedsRecreate;
            _recoveryNeedsRecreate = false;
            RecoverOverlay(recreate: doRecreate);
        }
        else
        {
            RecoverOverlay(recreate: false);
        }

        _recoveryStep++;
        if (_recoveryStep < RecoveryDelays.Length && _recoveryTimer != null)
        {
            _recoveryTimer.Interval = RecoveryDelays[_recoveryStep] - RecoveryDelays[_recoveryStep - 1];
            _recoveryTimer.Start();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (ShouldRecoverOnPowerMode(e.Mode))
        {
            ScheduleRecovery(recreate: true);
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (ShouldRecoverOnSessionSwitch(e.Reason))
        {
            ScheduleRecovery(recreate: true);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        ScheduleRecovery(recreate: false);
    }

    internal static bool ShouldKeepFrameLoopRunning(
        bool morphActive,
        bool fadeActive,
        bool waveformVisible,
        bool spinnerVisible,
        bool meetingVisible) =>
        morphActive || fadeActive || waveformVisible || spinnerVisible || meetingVisible;

    internal static bool ShouldRecoverOnPowerMode(PowerModes mode) =>
        mode == PowerModes.Resume;

    internal static bool ShouldRecoverOnSessionSwitch(SessionSwitchReason reason) =>
        reason is SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.SessionLogon
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect;

    internal static bool ShouldRecoverOnWindowMessage(uint msg, IntPtr wParam, uint taskbarCreatedMsg)
    {
        if (taskbarCreatedMsg != 0 && msg == taskbarCreatedMsg) return true;
        if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED or WM_DWMCOMPOSITIONCHANGED) return true;
        if (msg == WM_POWERBROADCAST)
        {
            long pbt = wParam.ToInt64();
            return pbt is PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC;
        }
        return false;
    }

    internal static bool ShouldRecreateOnWindowMessage(uint msg, uint taskbarCreatedMsg) =>
        (taskbarCreatedMsg != 0 && msg == taskbarCreatedMsg)
        || msg is WM_DWMCOMPOSITIONCHANGED or WM_POWERBROADCAST;

    /// <summary>
    /// Test/preview hook: presents the HUD exactly as a real phase change would,
    /// without touching the audio pipeline.
    /// </summary>
    internal void PreviewPhase(PipelinePhase phase) => OnPhaseChanged(phase);

    public void CloseForShutdown() => Close();

    // ---------- frame loop: morph + fade + pixel push ----------

    private void EnsureFrameLoop()
    {
        if (_frameTimer == null)
        {
            _frameTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _frameTimer.Tick += OnFrame;
        }
        if (!_frameTimer.IsEnabled)
        {
            _frameTimer.Start();
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        bool animating = false;
        bool completedThisFrame = false;

        if (_morphClock != null)
        {
            double t = Math.Clamp(_morphClock.Elapsed / MorphTime, 0, 1);
            double k = EaseInOut(t);
            PillBorder.Width = Lerp(_morphFrom.W, _morphTo.W, k);
            PillBorder.Height = Lerp(_morphFrom.H, _morphTo.H, k);
            PillBorder.CornerRadius = new CornerRadius(Lerp(_morphFrom.R, _morphTo.R, k));
            // Keep the pill centered inside the fixed canvas as it morphs.
            System.Windows.Controls.Canvas.SetLeft(PillBorder, (RootCanvas.Width - PillBorder.Width) / 2);
            // Listening hugs the screen top; spinner/advisory center in the canvas.
            double top = _hugTop ? 0 : (RootCanvas.Height - PillBorder.Height) / 2;
            System.Windows.Controls.Canvas.SetTop(PillBorder, top);
            animating = true;
            if (t >= 1)
            {
                _morphClock = null;
                completedThisFrame = true;
                var done = _morphCompleted;
                _morphCompleted = null;
                done?.Invoke();
            }
        }

        if (_fadeClock != null)
        {
            double t = Math.Clamp(_fadeClock.Elapsed / _fadeTime, 0, 1);
            _alpha = Lerp(_alphaFrom, _alphaTo, EaseInOut(t));
            animating = true;
            if (t >= 1)
            {
                _fadeClock = null;
                completedThisFrame = true;
                var done = _fadeCompleted;
                _fadeCompleted = null;
                done?.Invoke();
            }
        }

        if (TranscribingPanel.Visibility == Visibility.Visible)
        {
            _spinnerAngle = (_spinnerAngle + 6.4) % 360;
            SpinnerRotation.Angle = _spinnerAngle;
            animating = true;
        }
        if (Waveform.Visibility == Visibility.Visible)
        {
            Waveform.Tick();
            animating = true;
        }
        if (MeetingPanel.Visibility == Visibility.Visible)
        {
            MeetingWaveform.Tick();
            animating = true;
        }

        bool keepRunning = ShouldKeepFrameLoopRunning(
            _morphClock != null,
            _fadeClock != null,
            Waveform.Visibility == Visibility.Visible,
            TranscribingPanel.Visibility == Visibility.Visible,
            MeetingPanel.Visibility == Visibility.Visible);

        if (animating || completedThisFrame)
        {
            PushPixels();
        }

        if (!keepRunning && _frameTimer != null)
        {
            _frameTimer.Stop();
        }
    }

    private static double Lerp(double a, double b, double k) => a + (b - a) * k;

    private static double EaseInOut(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>
    /// Renders the content canvas (NOT the Window visual) into a DIB and presents it
    /// via UpdateLayeredWindow. The canvas is laid out manually at the window size.
    /// </summary>
    private void PushPixels()
    {
        if (_isClosed) return;

        // The frame loop can start before WPF creates the HWND (preview path,
        // or a phase change racing startup), or the HWND may have been invalidated.
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
        {
            RecreateLayeredWindow();
            if (_hwnd == IntPtr.Zero) return;
            CenterAtTop();
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        }

        // Layout in DIPs, raster at the monitor's DPI, present in physical pixels.
        double wDip = Width, hDip = Height;
        int w = (int)Math.Round(wDip * _dpiScale);
        int h = (int)Math.Round(hDip * _dpiScale);
        if (w <= 0 || h <= 0) return;

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        bool updateFailed = false;

        try
        {
            var size = new System.Windows.Size(wDip, hDip);
            RootCanvas.Measure(size);
            RootCanvas.Arrange(new Rect(size));
            RootCanvas.UpdateLayout();

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, 96 * _dpiScale, 96 * _dpiScale, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(RootCanvas);

            var bmi = new BITMAPINFO
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            };

            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return;
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return;
            hBitmap = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero) return;

            oldBitmap = SelectObject(memDc, hBitmap);
            rtb.CopyPixels(new Int32Rect(0, 0, w, h), bits, w * h * 4, w * 4);

            var topPos = new POINT { X = _leftPhys, Y = _topPhys };
            var sizeNative = new SIZE { cx = w, cy = h };
            var srcLoc = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = (byte)Math.Clamp(_alpha, 0, 255),
                AlphaFormat = AC_SRC_ALPHA
            };

            bool ok = UpdateLayeredWindow(_hwnd, screenDc, ref topPos, ref sizeNative, memDc, ref srcLoc, 0, ref blend, ULW_ALPHA);
            if (!ok)
            {
                updateFailed = true;
                System.Diagnostics.Debug.WriteLine($"[WinFlow HUD] UpdateLayeredWindow failed: {Marshal.GetLastWin32Error()}");
            }
            _pushCount++;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WinFlow HUD] PushPixels failed: {ex.Message}");
        }
        finally
        {
            if (memDc != IntPtr.Zero && oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }

        if (updateFailed && !_inRecoveryRecreate)
        {
            _inRecoveryRecreate = true;
            try
            {
                RecreateLayeredWindow();
                if (_hwnd != IntPtr.Zero)
                {
                    CenterAtTop();
                    ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                    PushPixels();
                }
            }
            finally
            {
                _inRecoveryRecreate = false;
            }
        }
    }

    // ---------- state machine ----------
    //
    // Every visual state is a FIXED definition (size, radius, opacity, content).
    // Transitions only animate from the current pixels to the target definition —
    // no state is ever derived from the previous one.

    private enum HudState { Idle, Listening, Working, Advisory, Meeting }

    private sealed record HudStateDef(
        double W, double H, double R, double Alpha, bool HugTop,
        bool Waveform = false, bool Spinner = false, bool Advisory = false, bool Meeting = false);

    private static readonly HudStateDef IdleDef = new(IdleWidth, IdleHeight, IdleHeight / 2, IdleAlpha, HugTop: true);
    private static readonly HudStateDef ListeningDef = new(CapsuleWidth, CapsuleHeight, CapsuleHeight / 2, 255, HugTop: true, Waveform: true);
    private static readonly HudStateDef WorkingDef = new(CircleSize, CircleSize, CircleSize / 2, 128, HugTop: true, Spinner: true);
    private static readonly HudStateDef AdvisoryDef = new(ErrorWidth, ErrorHeight, 14, 255, HugTop: false, Advisory: true);
    private static readonly HudStateDef MeetingDef = new(MeetingWidth, CapsuleHeight, CapsuleHeight / 2, 255, HugTop: true, Meeting: true);

    private HudState _state = HudState.Idle;

    /// <summary>Animates the HUD to a fixed state definition.</summary>
    private void TransitionTo(HudState state, TimeSpan? fadeTime = null)
    {
        var def = state switch
        {
            HudState.Listening => ListeningDef,
            HudState.Working => WorkingDef,
            HudState.Advisory => AdvisoryDef,
            HudState.Meeting => MeetingDef,
            _ => IdleDef
        };
        _state = state;
        _hugTop = def.HugTop;
        if (state == HudState.Listening) Waveform.AudioLevel = 0;
        if (state == HudState.Meeting) MeetingWaveform.AudioLevel = 0;
        SetContent(waveform: def.Waveform, spinner: def.Spinner, advisory: def.Advisory, meeting: def.Meeting);
        MorphTo(def.W, def.H, def.R);
        FadeTo(def.Alpha, fadeTime ?? (def.Alpha >= _alpha ? FadeInTime : FadeOutTime));
    }

    /// <summary>Which fixed state should own the HUD right now.</summary>
    private HudState ResolveState()
    {
        if (_lastPhase == PipelinePhase.Error) return HudState.Advisory;
        if (_lastPhase is PipelinePhase.Recording) return HudState.Listening;
        if (_lastPhase is PipelinePhase.Transcribing or PipelinePhase.Inserting) return HudState.Working;
        if (_meetingRecording) return HudState.Meeting;
        return HudState.Idle;
    }

    private void OnPhaseChanged(PipelinePhase phase)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _lastPhase = phase;
            TransitionTo(ResolveState());
        });
    }

    // ---------- meeting recording indicator ----------

    private void OnMeetingRecordingChanged(bool recording)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _meetingRecording = recording;
            if (recording)
            {
                UpdateMeetingTime();
                if (_meetingClock == null)
                {
                    _meetingClock = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(1)
                    };
                    _meetingClock.Tick += (_, _) => UpdateMeetingTime();
                }
                _meetingClock.Start();
            }
            else
            {
                _meetingClock?.Stop();
            }
            TransitionTo(ResolveState());
        });
    }

    private void UpdateMeetingTime()
    {
        var e = _meetingRecorder?.Elapsed ?? TimeSpan.Zero;
        MeetingTimeText.Text = $"{(int)e.TotalMinutes}:{e.Seconds:D2}";
    }

    /// <summary>Test/preview hook for the meeting pill, without any audio capture.</summary>
    internal void PreviewMeeting(bool recording, TimeSpan elapsed = default)
    {
        OnMeetingRecordingChanged(recording);
        Dispatcher.BeginInvoke(() =>
        {
            MeetingTimeText.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
        });
    }

    private void SetContent(bool waveform = false, bool spinner = false, bool advisory = false, bool meeting = false)
    {
        Waveform.Visibility = waveform ? Visibility.Visible : Visibility.Collapsed;
        TranscribingPanel.Visibility = spinner ? Visibility.Visible : Visibility.Collapsed;
        AdvisoryPanel.Visibility = advisory ? Visibility.Visible : Visibility.Collapsed;
        MeetingPanel.Visibility = meeting ? Visibility.Visible : Visibility.Collapsed;
        if (!advisory)
        {
            ElevatedBadge.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private void MorphTo(double width, double height, double radius, Action? onCompleted = null)
    {
        _morphFrom = (PillBorder.Width, PillBorder.Height, PillBorder.CornerRadius.TopLeft);
        _morphTo = (width, height, radius);
        _morphCompleted = onCompleted;
        _morphClock = System.Diagnostics.Stopwatch.StartNew();
        EnsureFrameLoop();
    }

    private void FadeTo(double alpha, TimeSpan duration, Action? onCompleted = null)
    {
        _alphaFrom = _alpha;
        _alphaTo = alpha;
        _fadeTime = duration;
        _fadeCompleted = onCompleted;
        _fadeClock = System.Diagnostics.Stopwatch.StartNew();
        if (alpha > 0)
        {
            CenterAtTop();
        }
        EnsureFrameLoop();
    }

    private void OnAudioLevelChanged(float level)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Waveform.AudioLevel = level;
            // During a meeting the mic is live too — feed the same level so the
            // meeting pill's bars dance exactly like the dictation capsule.
            MeetingWaveform.AudioLevel = level;
        });
    }

    private void OnNoticeRaised(PipelineNotice notice)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (notice == PipelineNotice.TargetElevated)
            {
                TransitionTo(HudState.Advisory);
                ElevatedBadge.Visibility = Visibility.Visible;
            }
            else
            {
                ElevatedBadge.Visibility = Visibility.Collapsed;
            }

            if (_frameTimer?.IsEnabled != true)
            {
                PushPixels();
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

            if (_frameTimer?.IsEnabled != true)
            {
                PushPixels();
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _frameTimer?.Stop();
        _meetingClock?.Stop();
        _recoveryTimer?.Stop();

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        if (_hwnd != IntPtr.Zero)
        {
            _windowsByHwnd.Remove(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _appState.PhaseChanged -= OnPhaseChanged;
        _appState.AudioLevelChanged -= OnAudioLevelChanged;
        _appState.ErrorOccurred -= OnErrorOccurred;
        _appState.NoticeRaised -= OnNoticeRaised;
        if (_meetingRecorder != null)
        {
            _meetingRecorder.RecordingChanged -= OnMeetingRecordingChanged;
        }
        base.OnClosed(e);
    }

    // ---------- Win32 layered-window interop ----------

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint ULW_ALPHA = 0x2;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_NCDESTROY = 0x0082;
    private const uint WM_POWERBROADCAST = 0x0218;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;
    private const long PBT_APMRESUMESUSPEND = 0x0007;
    private const long PBT_APMRESUMEAUTOMATIC = 0x0012;

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private const int SW_SHOWNOACTIVATE = 4;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int X, int Y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage,
        out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }
}
