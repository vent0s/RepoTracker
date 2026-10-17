using RepoTracker.Core.Localization;
namespace RepoTracker.Core.Tracking;

public enum AppTheme { Dark, Light }
public sealed record AppPreferences
{
    public int LocalIntervalSeconds { get; init; } = 15;
    public int RemoteIntervalMinutes { get; init; } = 10;
    public int LocalConcurrency { get; init; } = 2;
    public bool KeepExpanded { get; init; }
    public AppTheme Theme { get; init; } = AppTheme.Dark;
    public int ScalePercent { get; init; } = 100;
    public string Language { get; init; } = Text.SystemLanguage;

    public AppPreferences Validate()
    {
        if (!Text.IsSupportedPreference(Language)) throw new TrackerException(Text.Get("Language.Invalid"));
        if (LocalIntervalSeconds is < 5 or > 300) throw new TrackerException(Text.Get("Settings.LocalRange"));
        if (RemoteIntervalMinutes is < 1 or > 120) throw new TrackerException(Text.Get("Settings.RemoteRange"));
        if (LocalConcurrency is < 1 or > 4) throw new TrackerException(Text.Get("Settings.ConcurrencyRange"));
        if (!Enum.IsDefined(Theme)) throw new TrackerException(Text.Get("Settings.ThemeInvalid"));
        if (ScalePercent is not (100 or 125 or 150)) throw new TrackerException(Text.Get("Settings.ScaleInvalid"));
        return this;
    }
}

public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}
