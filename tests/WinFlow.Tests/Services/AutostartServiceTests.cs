using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Services;

public class AutostartServiceTests
{
    // Pure string tests only — never touch the real HKCU Run key.

    [Fact]
    public void BuildRunCommand_QuotesPathWithSpaces_AndPassesMinimized()
    {
        Assert.Equal("\"C:\\a b\\WinFlow.exe\" --minimized",
            AutostartService.BuildRunCommand(@"C:\a b\WinFlow.exe"));
    }

    [Fact]
    public void BuildRunCommand_QuotesEvenWithoutSpaces()
    {
        Assert.Equal("\"C:\\Apps\\WinFlow.exe\" --minimized",
            AutostartService.BuildRunCommand(@"C:\Apps\WinFlow.exe"));
    }
}
