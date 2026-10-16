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

    public AppPreferences Validate()
    {
        if (LocalIntervalSeconds is < 5 or > 300) throw new TrackerException("本地检查间隔应为 5–300 秒。");
        if (RemoteIntervalMinutes is < 1 or > 120) throw new TrackerException("远程检查间隔应为 1–120 分钟。");
        if (LocalConcurrency is < 1 or > 4) throw new TrackerException("本地并发数应为 1–4。");
        if (!Enum.IsDefined(Theme)) throw new TrackerException("请选择深色或浅色主题。");
        if (ScalePercent is not (100 or 125 or 150)) throw new TrackerException("界面缩放支持 100%、125% 和 150%。");
        return this;
    }
}

public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}
