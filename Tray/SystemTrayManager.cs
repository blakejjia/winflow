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

        var menu = new ContextMenuStrip();

        var titleItem = new ToolStripMenuItem("WinFlow — hold CapsLock to speak") { Enabled = false };
        menu.Items.Add(titleItem);
        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings", null, (s, e) => OpenSettingsRequested?.Invoke());
        menu.Items.Add(settingsItem);

        var runLogItem = new ToolStripMenuItem("Run log", null, (s, e) => OpenRunLogRequested?.Invoke());
        menu.Items.Add(runLogItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Quit WinFlow", null, (s, e) => ExitRequested?.Invoke());
        menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (s, e) => OpenSettingsRequested?.Invoke();

        _appState.PhaseChanged += OnPhaseChanged;
    }

    private static (Icon Icon, IntPtr HIcon) CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(0, 120, 212));
        g.FillEllipse(brush, 2, 2, 28, 28);
        using var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString("W", font, textBrush, new RectangleF(0, 0, 32, 32), format);

        // Icon.FromHandle does NOT own the HICON; keep it and DestroyIcon on dispose.
        var hIcon = bitmap.GetHicon();
        return (Icon.FromHandle(hIcon), hIcon);
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
        if (_uiContext != null)
        {
            _uiContext.Post(_ => UpdateTrayText(phase), null);
        }
        else
        {
            UpdateTrayText(phase);
        }
    }

    private void UpdateTrayText(PipelinePhase phase)
    {
        _notifyIcon.Text = phase switch
        {
            PipelinePhase.Recording => "WinFlow — listening…",
            PipelinePhase.Transcribing => "WinFlow — writing it down…",
            _ => "WinFlow — hold CapsLock to speak"
        };
    }

    public void Dispose()
    {
        _appState.PhaseChanged -= OnPhaseChanged;
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
