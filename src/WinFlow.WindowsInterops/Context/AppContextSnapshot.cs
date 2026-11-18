namespace WinFlow.WindowsInterops.Context;

public class AppContextSnapshot
{
    public IntPtr Hwnd { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public string? SelectedText { get; set; }
    public bool IsElevated { get; set; }
    public string? ScreenshotBase64Jpeg { get; set; }

    public bool HasSelectedText => !string.IsNullOrWhiteSpace(SelectedText);
}
