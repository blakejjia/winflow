using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinFlow.Core.Models;

namespace WinFlow.Core.Services;

public interface ISettingsService
{
    Task<AppSettings> LoadSettingsAsync();
    Task SaveSettingsAsync(AppSettings settings);
}

public class LocalSettingsService : ISettingsService
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public LocalSettingsService(string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "WinFlow");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "settings.json");
        }
        else
        {
            _filePath = filePath;
        }
    }

    public async Task<AppSettings> LoadSettingsAsync()
    {
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

            if (!string.IsNullOrEmpty(settings.GroqApiKey))
            {
                settings.GroqApiKey = UnprotectSafe(settings.GroqApiKey);
            }
            if (!string.IsNullOrEmpty(settings.TranscriptionApiKey))
            {
                settings.TranscriptionApiKey = UnprotectSafe(settings.TranscriptionApiKey);
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        var clone = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        if (!string.IsNullOrEmpty(clone.GroqApiKey))
        {
            clone.GroqApiKey = ProtectSafe(clone.GroqApiKey);
        }
        if (!string.IsNullOrEmpty(clone.TranscriptionApiKey))
        {
            clone.TranscriptionApiKey = ProtectSafe(clone.TranscriptionApiKey);
        }

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(clone, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json);
    }

    private static string ProtectSafe(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var bytes = Encoding.UTF8.GetBytes(plainText);
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                return "dpapi:" + Convert.ToBase64String(encrypted);
            }
        }
        catch
        {
            // fallback
        }
        return plainText;
    }

    private static string UnprotectSafe(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;
        if (cipherText.StartsWith("dpapi:") && OperatingSystem.IsWindows())
        {
            try
            {
                var base64 = cipherText["dpapi:".Length..];
                var bytes = Convert.FromBase64String(base64);
                var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                return string.Empty;
            }
        }
        return cipherText;
    }
}
