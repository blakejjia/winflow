using WinFlow.WindowsInterops.TextInsertion;
using Xunit;

namespace WinFlow.Tests.Interops;

public class TextInjectorTests
{
    [Fact]
    public async Task InjectTextAsync_EmptyOrWhitespace_ReturnsFalseWithoutThrowing()
    {
        var injector = new WindowsTextInjector();
        var result1 = await injector.InjectTextAsync("");
        var result2 = await injector.InjectTextAsync("   ");

        Assert.False(result1);
        Assert.False(result2);
    }
}
