using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace WinFlow.Tray;

/// <summary>
/// A Fluent-styled tray context menu. WinForms ContextMenuStrip always renders the
/// legacy Win32 look, so the menu is a small non-activating WPF popup instead,
/// positioned above the tray and dismissed on deactivation or Escape.
/// </summary>
public partial class TrayMenuWindow : Window
{
    public event Action? SettingsRequested;
    public event Action? RunLogRequested;
    public event Action? QuitRequested;

    private bool _closing;

    public TrayMenuWindow()
    {
        InitializeComponent();
        ApplyTheme(ApplicationThemeManager.GetAppTheme());
        ApplicationThemeManager.Changed += OnThemeChanged;
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                Dismiss();
                e.Handled = true;
            }
        };
    }

    public void SetStatus(string text)
    {
        StatusHeader.Text = text;
    }

    /// <summary>Shows the menu with its bottom-right corner near the tray click point.</summary>
    public void ShowAtCursor()
    {
        // Measure off-screen first so placement uses the final size.
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();

        GetCursorPos(out var pt);
        var workArea = SystemParameters.WorkArea;

        double left = pt.X - ActualWidth + 8;
        double top = pt.Y - ActualHeight - 8;

        left = Math.Max(workArea.Left + 8, Math.Min(left, workArea.Right - ActualWidth - 8));
        top = Math.Max(workArea.Top + 8, Math.Min(top, workArea.Bottom - ActualHeight - 8));

        Left = left;
        Top = top;
        Activate(); // needed so Deactivated fires when the user clicks elsewhere
        // Tray menus must claim foreground or the first outside click won't dismiss them.
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Hide();
        _closing = false;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        SettingsRequested?.Invoke();
    }

    private void RunLog_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        RunLogRequested?.Invoke();
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
        QuitRequested?.Invoke();
    }

    private void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        Dispatcher.BeginInvoke(() => ApplyTheme(theme));
    }

    private void ApplyTheme(ApplicationTheme theme)
    {
        bool dark = theme != ApplicationTheme.Light;

        void SetBrush(string key, string hex) =>
            Resources[key] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

        SetBrush("TrayMenuBackgroundBrush", dark ? "#F52C2C2C" : "#F5FFFFFF");
        SetBrush("TrayMenuBorderBrush", dark ? "#28FFFFFF" : "#14000000");
        SetBrush("TrayMenuTextBrush", dark ? "#F5FFFFFF" : "#E4000000");
        SetBrush("TrayMenuSecondaryBrush", dark ? "#99FFFFFF" : "#8A000000");
        SetBrush("TrayMenuDividerBrush", dark ? "#1EFFFFFF" : "#12000000");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        // WS_EX_TOOLWINDOW: keep it out of Alt-Tab. Not NOACTIVATE: the menu must take
        // focus briefly so clicks work and Deactivated dismisses it.
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
    }

    protected override void OnClosed(EventArgs e)
    {
        ApplicationThemeManager.Changed -= OnThemeChanged;
        base.OnClosed(e);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
