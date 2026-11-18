using WinFlow.WindowsInterops.Context;
using Xunit;

namespace WinFlow.Tests.Interops;

public class TerminalBlacklistTests
{
    [Theory]
    [InlineData("windowsterminal.exe", true)]
    [InlineData("WindowsTerminal", true)]
    [InlineData("cmd.exe", true)]
    [InlineData("powershell.exe", true)]
    [InlineData("pwsh.exe", true)]
    [InlineData("conhost.exe", true)]
    [InlineData("notepad.exe", false)]
    [InlineData("code.exe", false)]
    [InlineData("chrome.exe", false)]
    public void IsTerminalProcess_IdentifiesTerminalsAccurately(string processName, bool expected)
    {
        var isTerminal = TerminalBlacklist.IsTerminalProcess(processName);
        Assert.Equal(expected, isTerminal);
    }
}
