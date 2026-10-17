using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace RepoTracker.Desktop.Tests;

[Collection("Desktop")]
public sealed class LanguageWindowTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("zh-CN", "system", "zh-Hans")]
    [InlineData("en-US", "system", "en")]
    [InlineData("de-DE", "system", "en")]
    [InlineData("zh-CN", "en", "en")]
    public async Task RealWindowsFollowSystemAndPersistLanguageAcrossRestart(string system, string preference, string expected)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RepoTracker.slnx"))) root = root.Parent;
        Assert.NotNull(root);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var run = Path.Combine(root.FullName, "tests", "artifacts", "language-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(run, "profile");
        Directory.CreateDirectory(profile);
        await File.WriteAllTextAsync(Path.Combine(profile, "state.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 4, repositories = Array.Empty<object>(),
            groups = new[] { new { id = Guid.NewGuid(), name = "我的 Tools", order = 0, collapsed = false } },
            preferences = new { language = preference }
        }));
        async Task Launch(string language)
        {
            var start = new ProcessStartInfo(Path.Combine(root.FullName, "tests", "RepoTracker.Desktop.TestHost", "bin", configuration, "net10.0-windows", "RepoTracker.Desktop.TestHost.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root.FullName };
            start.ArgumentList.Add("--profile"); start.ArgumentList.Add(profile);
            start.Environment["REPOTRACKER_TEST_SYSTEM_LANGUAGE"] = system;
            start.Environment["REPOTRACKER_TEST_LANGUAGE"] = language;
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            var report = Path.Combine(run, "report.json");
            var details = File.Exists(report) ? await File.ReadAllTextAsync(report) : "No report";
            output.WriteLine(run + "\n" + details);
            Assert.True(process.ExitCode == 0, details);
        }
        await Launch(expected);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(profile, "state.json")))!;
        var switched = expected == "en" ? "zh-Hans" : "en";
        Assert.Equal(switched, saved["preferences"]!["language"]!.GetValue<string>());
        // Keep the same profile and simulated system language: only the persisted override changes the UI.
        await Launch(switched);
    }
}
