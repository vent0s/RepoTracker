using RepoTracker.Core.Tracking;

namespace RepoTracker.Core.Tests;

public sealed class AppPreferencesTests
{
    [Theory]
    [InlineData(5, 1, 1, 100, AppTheme.Dark)]
    [InlineData(300, 120, 4, 150, AppTheme.Light)]
    [InlineData(15, 10, 2, 125, AppTheme.Dark)]
    public void AllowedBoundaries(int local, int remote, int concurrency, int scale, AppTheme theme)
    { new AppPreferences { LocalIntervalSeconds = local, RemoteIntervalMinutes = remote, LocalConcurrency = concurrency, ScalePercent = scale, Theme = theme }.Validate(); }
    [Theory]
    [InlineData(4, 10, 2, 100, 0)]
    [InlineData(301, 10, 2, 100, 0)]
    [InlineData(15, 0, 2, 100, 0)]
    [InlineData(15, 121, 2, 100, 0)]
    [InlineData(15, 10, 0, 100, 0)]
    [InlineData(15, 10, 5, 100, 0)]
    [InlineData(15, 10, 2, 101, 0)]
    [InlineData(15, 10, 2, 100, 2)]
    public void InvalidPreferencesAreRejected(int local, int remote, int concurrency, int scale, int theme)
    { Assert.Throws<TrackerException>(() => new AppPreferences { LocalIntervalSeconds = local, RemoteIntervalMinutes = remote, LocalConcurrency = concurrency, ScalePercent = scale, Theme = (AppTheme)theme }.Validate()); }
}
