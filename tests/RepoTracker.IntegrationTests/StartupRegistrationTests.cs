using RepoTracker.Core.Tracking;
using RepoTracker.Infrastructure.Runtime;

namespace RepoTracker.IntegrationTests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void LoginCommandQuotesPathsAndRegistrationRemainsScopedToProfile()
    {
        var values = new Store(); var first = new StartupRegistration(values, @"E:\应用 程序\RepoTracker.exe", @"E:\用户 配置\");
        var second = new StartupRegistration(values, @"E:\应用 程序\RepoTracker.exe", @"E:\另一配置");
        Assert.False(first.IsEnabled); first.SetEnabled(true); Assert.True(first.IsEnabled); Assert.False(second.IsEnabled);
        Assert.Equal("\"E:\\应用 程序\\RepoTracker.exe\" --profile \"E:\\用户 配置\" --minimized", Assert.Single(values.Values).Value);
        second.SetEnabled(true); first.SetEnabled(false); Assert.False(first.IsEnabled); Assert.True(second.IsEnabled);
        Assert.Single(values.Values);
        Assert.Contains("\"E:\\\\\"", StartupRegistration.Command(@"E:\App.exe", @"E:\"));
    }
    [Fact]
    public void DisableRefusesForeignCommandAndWriteErrorsRemainVisible()
    {
        var values = new Store(); var registration = new StartupRegistration(values, @"E:\App.exe", @"E:\profile");
        registration.SetEnabled(true); var key = Assert.Single(values.Values).Key;
        values.Values[key] = "another application";
        Assert.False(registration.IsEnabled); Assert.Throws<TrackerException>(() => registration.SetEnabled(false));
        Assert.Equal("another application", values.Values[key]);
        values.Fail = true; Assert.Throws<UnauthorizedAccessException>(() => registration.SetEnabled(true));
        Assert.False(registration.IsEnabled);
    }
    [Theory]
    [InlineData("relative.exe", "E:\\profile")]
    [InlineData("E:\\App.dll", "E:\\profile")]
    [InlineData("E:\\App.exe", "relative")]
    [InlineData("E:\\App.exe", "E:\\bad\"profile")]
    public void MalformedCommandsCannotReachStore(string exe, string profile)
    { Assert.Throws<TrackerException>(() => StartupRegistration.Command(exe, profile)); }
    [Fact]
    public void TooLongRunCommandIsRejectedBeforeWrite()
    {
        var values = new Store(); var registration = new StartupRegistration(values, @"E:\App.exe", "E:\\" + new string('a', 250));
        Assert.Throws<TrackerException>(() => registration.SetEnabled(true)); Assert.Empty(values.Values);
    }
    private sealed class Store : IStartupValueStore
    {
        public Dictionary<string, string> Values { get; } = []; public bool Fail;
        public string? Read(string name) => Values.GetValueOrDefault(name);
        public void Write(string name, string? command)
        { if (Fail) throw new UnauthorizedAccessException(); if (command is null) Values.Remove(name); else Values[name] = command; }
    }
}
