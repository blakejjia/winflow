using System.Net.Http;
using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Services;

public class TranscriptionRetryTests
{
    public TranscriptionRetryTests()
    {
        // Keep tests fast; production defaults are 500ms / 2s.
        TranscriptionRetry.RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero };
    }

    [Fact]
    public async Task SucceedsFirstTry_NoRetry()
    {
        int calls = 0;
        var result = await TranscriptionRetry.RunAsync(_ =>
        {
            calls++;
            return Task.FromResult("ok");
        });
        Assert.Equal("ok", result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailsTwice_SucceedsOnThirdAttempt()
    {
        int calls = 0;
        var result = await TranscriptionRetry.RunAsync(_ =>
        {
            calls++;
            if (calls < 3) throw new HttpRequestException("server 500");
            return Task.FromResult("recovered");
        });
        Assert.Equal("recovered", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task FailsThreeTimes_ThrowsAfterExactlyThreeAttempts()
    {
        int calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => TranscriptionRetry.RunAsync(_ =>
        {
            calls++;
            throw new HttpRequestException("server 500");
        }));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Cancellation_StopsImmediatelyWithoutRetrying()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TranscriptionRetry.RunAsync(ct =>
        {
            calls++;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("never");
        }, cts.Token));
        Assert.Equal(1, calls);
    }
}
