using Microsoft.Win32;

namespace WinFlow.Core.Services;

/// <summary>
/// Per-user "start with Windows" via the HKCU Run key. The registry value is the
/// single source of truth shared by the installer task and the in-app toggle:
/// installed-with-task and portable-enabled look identical here, and Task Manager's
/// Startup apps view always agrees with what the settings toggle shows.
/// </summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinFlow";

    /// <summary>True if a WinFlow Run value exists (name match only — the path may be stale).</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes the current exe path (single-file safe) or deletes the value. Throws on failure.</summary>
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            // Environment.ProcessPath is the real single-file exe;
            // AppContext.BaseDirectory would be the bundle extraction dir — wrong.
            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine the WinFlow executable path.");
            key.SetValue(ValueName, BuildRunCommand(exePath));
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    internal static string BuildRunCommand(string exePath) => $"\"{exePath}\" --minimized";
}
