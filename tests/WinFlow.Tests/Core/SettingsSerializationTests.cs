using System.IO;
using WinFlow.Core.Models;
using WinFlow.Core.Services;
using Xunit;

namespace WinFlow.Tests.Core;

public class SettingsSerializationTests
{
    [Fact]
    public void AppSettings_DefaultValues_MatchSpec()
    {
        var settings = new AppSettings();

        Assert.Equal("https://api.groq.com/openai/v1", settings.ApiBaseUrl);
        Assert.Equal("whisper-large-v3", settings.TranscriptionModel);
        Assert.Equal("openai/gpt-oss-20b", settings.PostProcessingModel);
        Assert.Equal("qwen/qwen3.8-27b", settings.ContextModel);
        Assert.Equal("CapsLock", settings.TriggerKey);
        Assert.True(settings.DoubleTapLockEnabled);
        Assert.True(settings.PreserveClipboard);
    }

    [Fact]
    public async Task LocalSettingsService_SaveAndLoad_PreservesValues()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"winflow_settings_test_{Guid.NewGuid()}.json");
        try
        {
            var service = new LocalSettingsService(tempFile);
            var initial = new AppSettings
            {
                GroqApiKey = "gsk_test12345",
                TriggerKey = "RightControl",
                CustomVocabulary = "GraphQL, WinFlow, Groq"
            };

            await service.SaveSettingsAsync(initial);

            var loaded = await service.LoadSettingsAsync();
            Assert.Equal("gsk_test12345", loaded.GroqApiKey);
            Assert.Equal("RightControl", loaded.TriggerKey);
            Assert.Equal("GraphQL, WinFlow, Groq", loaded.CustomVocabulary);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
