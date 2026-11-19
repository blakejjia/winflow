using System.Drawing;
using System.Windows.Forms;
using WinFlow.Core.Pipeline;

namespace WinFlow.Tray;

public class SystemTrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly AppState _appState;

    public event Action? OpenSettingsRequested;
    public event Action? OpenRunLogRequested;
    public event Action? ExitRequested;

    public SystemTrayManager(AppState appState)
    {
        _appState = appState;

        _notifyIcon = new NotifyIcon
        {
            Text = "WinFlow - Context-Aware Voice Dictation",
            Icon = SystemIcons.Application,
            Visible = true
        };

        var menu = new ContextMenuStrip();

        var titleItem = new ToolStripMenuItem("WinFlow (CapsLock to dict)") { Enabled = false };
        menu.Items.Add(titleItem);
        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("⚙ Settings...", null, (s, e) => OpenSettingsRequested?.Invoke());
        menu.Items.Add(settingsItem);

        var runLogItem = new ToolStripMenuItem("📋 Pipeline Run Log...", null, (s, e) => OpenRunLogRequested?.Invoke());
        menu.Items.Add(runLogItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit WinFlow", null, (s, e) => ExitRequested?.Invoke());
        menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (s, e) => OpenSettingsRequested?.Invoke();

        _appState.PhaseChanged += OnPhaseChanged;
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
        switch (phase)
        {
            case PipelinePhase.Recording:
                _notifyIcon.Text = "WinFlow - Recording...";
                break;
            case PipelinePhase.Transcribing:
                _notifyIcon.Text = "WinFlow - Transcribing...";
                break;
            default:
                _notifyIcon.Text = "WinFlow - Idle (Hold CapsLock to speak)";
                break;
        }
    }

    public void Dispose()
    {
        _appState.PhaseChanged -= OnPhaseChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
