using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace WinFlow.WindowsInterops.Context;

public class WindowsContextService : IContextService
{
    public Task<AppContextSnapshot> CaptureContextSnapshotAsync(bool captureScreenshot = true, int maxDimension = 1024)
    {
        return Task.Run(() =>
        {
            var snapshot = new AppContextSnapshot();
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return snapshot;
            }

            snapshot.Hwnd = hwnd;
            snapshot.WindowTitle = GetWindowTitle(hwnd);

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    snapshot.ProcessName = proc.ProcessName;
                    snapshot.IsElevated = IsProcessElevated(proc.Handle);
                }
                catch
                {
                    // Access denied or process exited
                }
            }

            // Extract selected text using UI Automation
            snapshot.SelectedText = TryGetSelectedTextWithUia();

            // Capture screenshot if enabled
            if (captureScreenshot)
            {
                try
                {
                    using var bmp = CaptureWindowBitmap(hwnd);
                    if (bmp != null)
                    {
                        snapshot.ScreenshotBase64Jpeg = ImageUtilities.DownscaleAndEncodeJpeg(bmp, maxDimension);
                    }
                }
                catch
                {
                    // Screenshot capture error
                }
            }

            return snapshot;
        });
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;

        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? TryGetSelectedTextWithUia()
    {
        try
        {
            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement == null) return null;

            if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) &&
                patternObj is TextPattern textPattern)
            {
                var selectionRanges = textPattern.GetSelection();
                if (selectionRanges != null && selectionRanges.Length > 0)
                {
                    var selected = selectionRanges[0].GetText(-1);
                    if (!string.IsNullOrWhiteSpace(selected))
                    {
                        return selected.Trim();
                    }
                }
            }
        }
        catch
        {
            // UIA exception
        }

        return null;
    }

    private static Bitmap? CaptureWindowBitmap(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return null;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0) return null;

        var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try
            {
                // PW_RENDERFULLCONTENT = 2
                bool success = PrintWindow(hwnd, hdc, 2);
                if (!success)
                {
                    // Fallback to PW_CLIENTONLY = 1 or standard 0
                    PrintWindow(hwnd, hdc, 0);
                }
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        return bmp;
    }

    private static bool IsProcessElevated(IntPtr hProcess)
    {
        if (!OpenProcessToken(hProcess, TOKEN_QUERY, out var hToken))
            return false;

        try
        {
            var elevation = new TOKEN_ELEVATION();
            int size = Marshal.SizeOf<TOKEN_ELEVATION>();
            var pElevation = Marshal.AllocHGlobal(size);
            try
            {
                if (GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenElevation, pElevation, size, out _))
                {
                    elevation = Marshal.PtrToStructure<TOKEN_ELEVATION>(pElevation);
                    return elevation.TokenIsElevated != 0;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pElevation);
            }
        }
        finally
        {
            CloseHandle(hToken);
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hDC, uint nFlags);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr TokenHandle,
        TOKEN_INFORMATION_CLASS TokenInformationClass,
        IntPtr TokenInformation,
        int TokenInformationLength,
        out int ReturnLength);

    private const uint TOKEN_QUERY = 0x0008;

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenElevation = 20
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
