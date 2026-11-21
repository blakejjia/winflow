using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WinFlow.Core.Pipeline;

namespace WinFlow.Tray;

public class SystemTrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly AppState _appState;
    private readonly Icon _icon;
    private readonly IntPtr _hIcon;
    private readonly SynchronizationContext? _uiContext;
    private TrayMenuWindow? _menuWindow;
    private string _statusText = "WinFlow — hold CapsLock to speak";

    public event Action? OpenSettingsRequested;
    public event Action? OpenRunLogRequested;
    public event Action? ExitRequested;

    public SystemTrayManager(AppState appState)
    {
        _appState = appState;
        _uiContext = SynchronizationContext.Current;
        (_icon, _hIcon) = CreateAppIcon();

        _notifyIcon = new NotifyIcon
        {
            Text = "WinFlow — hold CapsLock to speak",
            Icon = _icon,
            Visible = true
        };

        // A WinForms ContextMenuStrip always renders the legacy Win32 look; use a
        // Fluent WPF popup instead, created lazily on the UI thread.
        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                PostToUi(ShowFluentMenu);
            }
        };
        _notifyIcon.DoubleClick += (s, e) => OpenSettingsRequested?.Invoke();

        _appState.PhaseChanged += OnPhaseChanged;
    }

    private static (Icon Icon, IntPtr HIcon) CreateAppIcon()
    {
        try
        {
            var asm = typeof(SystemTrayManager).Assembly;
            using var stream = asm.GetManifestResourceStream("WinFlow.Assets.app.ico")
                ?? asm.GetManifestResourceStream("Assets.app.ico");
            if (stream != null)
            {
                var icon = new Icon(stream, new Size(32, 32));
                return (icon, IntPtr.Zero);
            }

            var localIco = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(localIco))
            {
                var icon = new Icon(localIco, new Size(32, 32));
                return (icon, IntPtr.Zero);
            }
        }
        catch
        {
            // Fall through to procedural fallback
        }

        // Procedural Fluent squircle fallback with 7 HUD soundwave bars
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Base dark plate
        using var plateBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
            new PointF(0, 0), new PointF(32, 32),
            Color.FromArgb(44, 51, 64), Color.FromArgb(18, 22, 29));
        using var platePath = new System.Drawing.Drawing2D.GraphicsPath();
        platePath.AddArc(2, 2, 8, 8, 180, 90);
        platePath.AddArc(22, 2, 8, 8, 270, 90);
        platePath.AddArc(22, 22, 8, 8, 0, 90);
        platePath.AddArc(2, 22, 8, 8, 90, 90);
        platePath.CloseFigure();
        g.FillPath(plateBrush, platePath);

        // Sound bars: 7 symmetric vertical bars
        float[] heights = [3, 6, 9, 13, 9, 6, 3];
        float[] xs = [7, 10, 13, 16, 19, 22, 25];
        using var barBrush = new SolidBrush(Color.White);
        for (int i = 0; i < 7; i++)
        {
            float h = heights[i];
            float y = 16 - (h / 2f);
            g.FillRectangle(barBrush, xs[i], y, 2f, h);
        }

        // Icon.FromHandle does NOT own the HICON; keep it and DestroyIcon on dispose.
        var hIcon = bitmap.GetHicon();
        return (Icon.FromHandle(hIcon), hIcon);
    }

    private void PostToUi(Action action)
    {
        if (_uiContext != null)
        {
            _uiContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    private void ShowFluentMenu()
    {
        if (_menuWindow == null)
        {
            _menuWindow = new TrayMenuWindow();
            _menuWindow.SettingsRequested += () => OpenSettingsRequested?.Invoke();
            _menuWindow.RunLogRequested += () => OpenRunLogRequested?.Invoke();
            _menuWindow.QuitRequested += () => ExitRequested?.Invoke();
        }

        if (_menuWindow.IsVisible)
        {
            _menuWindow.Dismiss();
            return;
        }

        _menuWindow.SetStatus(_statusText);
        _menuWindow.ShowAtCursor();
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
        }
        catch
        {
            // Ignore if notification fails
        }
    }

    private void OnPhaseChanged(PipelinePhase phase)
    {
        // Phase events arrive on pipeline threads; marshal tray updates to the UI thread.
        PostToUi(() => UpdateTrayText(phase));
    }

    private void UpdateTrayText(PipelinePhase phase)
    {
        _statusText = phase switch
        {
            PipelinePhase.Recording => "WinFlow — listening…",
            PipelinePhase.Transcribing => "WinFlow — writing it down…",
            _ => "WinFlow — hold CapsLock to speak"
        };
        _notifyIcon.Text = _statusText;
        _menuWindow?.SetStatus(_statusText);
    }

    public void Dispose()
    {
        _appState.PhaseChanged -= OnPhaseChanged;
        _menuWindow?.Close();
        _menuWindow = null;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
