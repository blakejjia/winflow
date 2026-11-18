using WinFlow.Core.Models;
using Xunit;

namespace WinFlow.Tests.Core;

public class VoiceMacroTests
{
    [Fact]
    public void VoiceMacroMatcher_ExactMatch_ReturnsPayload()
    {
        var macros = new List<VoiceMacro>
        {
            new() { Command = "my email", Payload = "user@example.com" },
            new() { Command = "home address", Payload = "123 Main St, Springfield" }
        };

        var result = VoiceMacroMatcher.Match("My email.", macros);
        Assert.Equal("user@example.com", result);

        var result2 = VoiceMacroMatcher.Match("home address", macros);
        Assert.Equal("123 Main St, Springfield", result2);
    }

    [Fact]
    public void VoiceMacroMatcher_NoMatch_ReturnsNull()
    {
        var macros = new List<VoiceMacro>
        {
            new() { Command = "my email", Payload = "user@example.com" }
        };

        var result = VoiceMacroMatcher.Match("send my email to Bob", macros);
        Assert.Null(result);
    }
}
