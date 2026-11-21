using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
    private const double CircleSize = 30;
    private const double ErrorWidth = 240;
    private const double ErrorHeight = 52;
    private static readonly TimeSpan MorphTime = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan FadeInTime = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan FadeOutTime = TimeSpan.FromMilliseconds(360);

    private readonly AppState _appState;

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

    public OverlayWindow(AppState appState)
    {
        InitializeComponent();
        _appState = appState;

        _appState.PhaseChanged += OnPhaseChanged;
        _appState.AudioLevelChanged += OnAudioLevelChanged;
        _appState.ErrorOccurred += OnErrorOccurred;
        _appState.NoticeRaised += OnNoticeRaised;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnsureLayeredWindow();
    }

    private bool _layeredInitialized;
    private static ushort _hudClassAtom;
    private static WndProcDelegate? _hudWndProc; // keep alive for the process lifetime

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void EnsureLayeredWindow()
    {
        if (_layeredInitialized) return;
        _layeredInitialized = true;

        // A dedicated native window, NOT the WPF HwndSource: WPF's composition
        // target redirects its HWND and every UpdateLayeredWindow on it fails
        // with ERROR_INVALID_PARAMETER. A plain registered class has no such
        // redirection, so per-pixel alpha presentation works.
        if (_hudClassAtom == 0)
        {
            _hudWndProc = DefWindowProc;
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_hudWndProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = "WinFlowHudLayeredClass"
            };
            _hudClassAtom = RegisterClass(ref wc);
                if (_hudClassAtom == 0) return;
        }

        _dpiScale = GetDpiForSystem() / 96.0;
        if (_dpiScale <= 0) _dpiScale = 1.0;

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            "WinFlowHudLayeredClass", "WinFlow HUD",
            WS_POPUP,
            0, 0, (int)Math.Round(Width * _dpiScale), (int)Math.Round(Height * _dpiScale),
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;

        CenterAtTop();
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        PushPixels();
    }

    public void CenterAtTop()
    {
        if (_hwnd == IntPtr.Zero) return;
        // Work in PHYSICAL pixels against the monitor under the WPF window position
        // (GetDpiForSystem can disagree with the actual monitor on mixed-DPI setups).
        uint dpi = GetDpiForWindow(_hwnd);
        _dpiScale = dpi > 0 ? dpi / 96.0 : _dpiScale;

        var monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTOPRIMARY);
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

        SetWindowPos(_hwnd, IntPtr.Zero, _leftPhys, _topPhys, wPhys, hPhys,
            SWP_NOZORDER | SWP_NOACTIVATE);
        // Keep WPF DIP state roughly in sync for anything that reads Left/Top.
        Left = _leftPhys / _dpiScale;
        Top = _topPhys / _dpiScale;
    }

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
                var done = _fadeCompleted;
                _fadeCompleted = null;
                done?.Invoke();
            }
        }

        if (TranscribingPanel.Visibility == Visibility.Visible)
        {
            _spinnerAngle = (_spinnerAngle + 6.4) % 360;
            SpinnerRotation.Angle = _spinnerAngle;
        }
        if (Waveform.Visibility == Visibility.Visible)
        {
            Waveform.Tick();
        }

        if (_alpha > 0.5 || animating)
        {
            PushPixels();
        }
        else if (_frameTimer != null)
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
        // The frame loop can start before WPF creates the HWND (preview path,
        // or a phase change racing startup). Initialize once, lazily.
        if (_hwnd == IntPtr.Zero)
        {
            EnsureLayeredWindow();
            if (_hwnd == IntPtr.Zero) return;
        }

        // Layout in DIPs, raster at the monitor's DPI, present in physical pixels.
        double wDip = Width, hDip = Height;
        int w = (int)Math.Round(wDip * _dpiScale);
        int h = (int)Math.Round(hDip * _dpiScale);
        if (w <= 0 || h <= 0) return;

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

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return;
        }

        IntPtr oldBitmap = SelectObject(memDc, hBitmap);
        try
        {
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
                if (_pushCount < 3)
                {
                    System.Diagnostics.Debug.WriteLine($"[WinFlow HUD] UpdateLayeredWindow failed: {Marshal.GetLastWin32Error()}");
                }
            }
            _pushCount++;
        }
        finally
        {
            SelectObject(memDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
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
                    _hugTop = true;
                    SetContent(waveform: true);
                    MorphTo(CapsuleWidth, CapsuleHeight, CapsuleHeight / 2);
                    FadeTo(255, FadeInTime);
                    break;

                case PipelinePhase.Transcribing:
                case PipelinePhase.Inserting:
                    _hugTop = true;
                    SetContent(spinner: true);
                    MorphTo(CircleSize, CircleSize, CircleSize / 2);
                    // Dim to 50% while working; the final fade-out then runs 50% -> 0.
                    FadeTo(128, FadeInTime);
                    break;

                case PipelinePhase.Error:
                    _hugTop = false;
                    SetContent(advisory: true);
                    MorphTo(ErrorWidth, ErrorHeight, 14);
                    FadeTo(255, FadeInTime);
                    break;

                case PipelinePhase.Feedback:
                case PipelinePhase.Idle:
                default:
                    MorphTo(CircleSize, CircleSize, CircleSize / 2);
                    FadeTo(0, FadeOutTime, onCompleted: () => PushPixels());
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
        });
    }

    private void OnNoticeRaised(PipelineNotice notice)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (notice == PipelineNotice.TargetElevated)
            {
                _hugTop = false;
                SetContent(advisory: true);
                ElevatedBadge.Visibility = Visibility.Visible;
                MorphTo(ErrorWidth, ErrorHeight, 14);
                FadeTo(255, FadeInTime);
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
        _frameTimer?.Stop();
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _appState.PhaseChanged -= OnPhaseChanged;
        _appState.AudioLevelChanged -= OnAudioLevelChanged;
        _appState.ErrorOccurred -= OnErrorOccurred;
        _appState.NoticeRaised -= OnNoticeRaised;
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
