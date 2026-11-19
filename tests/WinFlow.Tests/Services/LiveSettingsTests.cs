using System.Net;
using System.Net.Http;
using System.Text;
using WinFlow.Core.Models;
using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Services;

public class LiveSettingsTests
{
    private class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\": \"ok\"}", Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task TranscriptionService_ReadsUpdatedApiKey_WithoutRestart()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var provider = new SettingsProvider(new AppSettings { GroqApiKey = "old-key" });
        var service = new TranscriptionService(http, provider);

        await service.TranscribeAsync(new byte[100]);
        Assert.Equal("old-key", handler.LastRequest!.Headers.Authorization?.Parameter);

        // Simulate the user saving a new key: the provider swaps the live instance.
        provider.Current = new AppSettings { GroqApiKey = "new-key" };

        await service.TranscribeAsync(new byte[100]);
        Assert.Equal("new-key", handler.LastRequest!.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task TranscriptionService_EmptyKeyThenSavedKey_StartsWorking()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var provider = new SettingsProvider(new AppSettings()); // no key at startup
        var service = new TranscriptionService(http, provider);

        var before = await service.TranscribeAsync(new byte[100]);
        Assert.Equal(string.Empty, before);
        Assert.Null(handler.LastRequest); // no request sent without a key

        provider.Current = new AppSettings { GroqApiKey = "entered-later" };
        var after = await service.TranscribeAsync(new byte[100]);
        Assert.Equal("ok", after);
        Assert.Equal("entered-later", handler.LastRequest!.Headers.Authorization?.Parameter);
    }
}
