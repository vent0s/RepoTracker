using System.Diagnostics;
using Xunit.Abstractions;

namespace RepoTracker.Desktop.Tests;

[Collection("Desktop")]
public sealed class NestedRepositoryWindowTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public async Task ManuallyAddChildrenDeduplicateAndCheckIndependentStatuses(string language)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RepoTracker.slnx"))) root = root.Parent;
        Assert.NotNull(root);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var run = Path.Combine(root.FullName, "tests", "artifacts", "nested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        async Task Launch(string scenario)
        {
            var start = new ProcessStartInfo(Path.Combine(root.FullName, "tests", "RepoTracker.Desktop.TestHost", "bin", configuration, "net10.0-windows", "RepoTracker.Desktop.TestHost.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root.FullName };
            start.ArgumentList.Add("--profile"); start.ArgumentList.Add(Path.Combine(run, "profile"));
            start.Environment["REPOTRACKER_TEST_NESTED"] = scenario;
            start.Environment["REPOTRACKER_TEST_SYSTEM_LANGUAGE"] = language;
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            var report = Path.Combine(run, "report.json");
            var details = File.Exists(report) ? await File.ReadAllTextAsync(report) : "No report";
            output.WriteLine(run + "\n" + details);
            Assert.True(process.ExitCode == 0, details);
            File.Copy(report, Path.Combine(run, "nested-" + scenario + "-report.json"));
        }
        await Launch("1");
        Assert.True(File.Exists(Path.Combine(run, "nested-entry.png")));
        Assert.True(File.Exists(Path.Combine(run, "nested-invalid.png")));
        Assert.True(File.Exists(Path.Combine(run, "nested-status.png")));
        Assert.True(File.Exists(Path.Combine(run, "nested-no-upstream.png")));
        Assert.True(File.Exists(Path.Combine(run, "nested-no-upstream-collapsed.png")));
        Assert.True(File.Exists(Path.Combine(run, "nested-expanded-light-150.png")));
        Assert.True(File.Exists(Path.Combine(run, "nested-summary-collapsed.png")));
        Assert.True(File.Exists(Path.Combine(run, "nested-summary-light-150.png")));
        Assert.True(File.Exists(Path.Combine(run, "nested-menu-dark.png")));
        Assert.True(File.Exists(Path.Combine(run, "nested-submenu-light-150.png")));
        await Launch("reload");
        Assert.True(File.Exists(Path.Combine(run, "nested-restart.png")));
    }
}
