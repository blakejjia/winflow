using WinFlow.Core.Models;

namespace WinFlow.Core.Services;

/// <summary>
/// Single source of truth for the live settings instance. Services read through the
/// provider on every request so saving settings takes effect immediately without restart.
/// </summary>
public interface ISettingsProvider
{
    AppSettings Current { get; }
}

public sealed class SettingsProvider : ISettingsProvider
{
    public SettingsProvider(AppSettings? initial = null)
    {
        Current = initial ?? new AppSettings();
    }

    public AppSettings Current { get; set; }
}

/// <summary>
/// Adapts a fixed <see cref="AppSettings"/> instance to <see cref="ISettingsProvider"/>.
/// The instance is mutated in place when the user saves, so services always see fresh values.
/// </summary>
public sealed class FixedSettingsProvider : ISettingsProvider
{
    public FixedSettingsProvider(AppSettings settings)
    {
        Current = settings;
    }

    public AppSettings Current { get; }
}
