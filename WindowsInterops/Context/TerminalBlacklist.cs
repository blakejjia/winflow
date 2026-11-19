using System.IO;

namespace WinFlow.WindowsInterops.Context;

public static class TerminalBlacklist
{
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowsterminal.exe",
        "windowsterminal",
        "cmd.exe",
        "cmd",
        "powershell.exe",
        "powershell",
        "pwsh.exe",
        "pwsh",
        "conhost.exe",
        "conhost",
        "mintty.exe",
        "mintty",
        "alacritty.exe",
        "alacritty",
        "wezterm-gui.exe",
        "wezterm.exe",
        "wezterm"
    };

    public static bool IsTerminalProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var name = Path.GetFileName(processName);
        return TerminalProcesses.Contains(name);
    }
}
