using System.Runtime.InteropServices;

namespace WinFlow.WindowsInterops.Context;

/// <summary>
/// Detects fullscreen apps/games so dictation stays out of the way.
/// Standard approach (what Discord/OBS-style overlays use):
///   1. SHQueryUserNotificationState — Windows' own "do not disturb" signal,
///      which covers exclusive-fullscreen D3D games and presentation mode.
///   2. Foreground window covering its whole monitor — catches borderless
///      fullscreen (Unity/Unreal default), which the notification state misses.
///      Desktop/shell windows are excluded so a plain desktop never suppresses.
/// Results are cached briefly: this is polled on every key gesture.
/// </summary>
public static class FullscreenDetector
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMilliseconds(500);
    private static readonly object Gate = new();
    private static bool _cached;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>True when a fullscreen game/app owns the screen right now.</summary>
    public static bool IsFullscreenAppActive()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _cachedAt < CacheDuration)
                return _cached;

            _cached = Detect();
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    private static bool Detect()
    {
        // 1. Windows' own signal: running a D3D fullscreen app or presentation mode.
        if (SHQueryUserNotificationState(out var state) == 0)
        {
            if (state is QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                or QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY
                or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
            {
                return true;
            }
        }

        // 2. Borderless fullscreen: foreground window covers its entire monitor.
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        if (IsShellWindow(hwnd)) return false;

        if (!GetWindowRect(hwnd, out var rect)) return false;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var m = info.rcMonitor;
        bool covers = rect.Left <= m.Left && rect.Top <= m.Top
            && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
        if (!covers) return false;

        // A maximized normal app also covers the monitor; require the window to have
        // no caption/border chrome, which is what borderless game windows look like.
        int style = GetWindowLong(hwnd, GWL_STYLE);
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        return !hasCaption;
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        // The desktop and taskbar cover the monitor too; never treat them as a game.
        return hwnd == GetShellWindow() || hwnd == FindWindow("Shell_TrayWnd", null);
    }

    private enum QUERY_USER_NOTIFICATION_STATE
    {
        QUNS_NOT_PRESENT = 1,
        QUNS_BUSY = 2,
        QUNS_RUNNING_D3D_FULL_SCREEN = 3,
        QUNS_PRESENTATION_MODE = 4,
        QUNS_ACCEPTS_NOTIFICATIONS = 5,
        QUNS_QUIET_TIME = 6,
        QUNS_APP = 7
    }

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
