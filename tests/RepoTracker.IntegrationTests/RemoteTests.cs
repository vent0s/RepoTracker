using System.Text;
using System.Text.Json;
using RepoTracker.Core.Sources;
using RepoTracker.Core.Status;
using RepoTracker.Core.Tracking;
using RepoTracker.Infrastructure.Configuration;
using RepoTracker.Infrastructure.Git;
using RepoTracker.Tests.Shared;

namespace RepoTracker.IntegrationTests;

public sealed class RemoteTests
{
    private static async Task<TrackedRepository> Fixture(string extra = "")
    {
        var root = await new TestWorkspace().CreateDetachedRepositoryAsync("remote fixture");
        await File.AppendAllTextAsync(Path.Combine(root, ".git", "config"), "[remote \"origin\"]\nurl = https://github.com/test/repo.git\nfetch = +refs/heads/*:refs/remotes/origin/*\n" + extra);
        return new(Guid.NewGuid(), root, Path.Combine(root, ".git"), Path.Combine(root, ".git"), "fixture", null, DateTimeOffset.UnixEpoch);
    }
    [Fact]
    public async Task ReaderUsesOriginThenOrdinalFallbackWithoutChangingConfig()
    {
        var repo = await Fixture("[remote \"aaa\"]\nurl = git@gitlab.com:test/aaa.git\n");
        var before = await File.ReadAllBytesAsync(Path.Combine(repo.GitDir, "config"));
        var config = await new GitRemoteReader(new GitProcess()).ReadAsync(repo, default);
        Assert.Equal("origin", config.Identity.RemoteName); Assert.Equal("github.com", config.Identity.Host); Assert.Null(config.SkipReason);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(repo.GitDir, "config")));
        await TestWorkspace.GitAsync(repo.WorktreeRoot, "config", "--remove-section", "remote.origin");
        config = await new GitRemoteReader(new GitProcess()).ReadAsync(repo, default);
        Assert.Equal("aaa", config.Identity.RemoteName); Assert.Equal("gitlab.com", config.Identity.Host);
    }
    [Theory]
    [InlineData("+refs/heads/*:refs/heads/*")]
    [InlineData("+refs/*:refs/*")]
    [InlineData("+refs/tags/*:refs/tags/*")]
    [InlineData("+refs/heads/*:refs/remotes/other/*")]
    [InlineData("^refs/heads/private")]
    [InlineData("+refs/heads/main:refs/remotes/origin/HEAD")]
    [InlineData("+refs/heads/../bad:refs/remotes/origin/../bad")]
    public async Task UnsupportedRefspecNeverStartsFetch(string refspec)
    {
        var repo = await Fixture(); await TestWorkspace.GitAsync(repo.WorktreeRoot, "config", "remote.origin.fetch", refspec);
        var process = new RecordingProcess(); var result = await new GitRemoteChecker(process, new(new GitProcess())).CheckAsync(repo, default);
        Assert.Equal(RemoteOutcome.Skipped, result.Outcome); Assert.Empty(process.Calls);
        Assert.Empty(await TestWorkspace.GitAsync(repo.WorktreeRoot, "for-each-ref", "--format=%(refname)", "refs/heads"));
    }
    [Theory]
    [InlineData("[remote \"origin\"]\nmirror = true\n")]
    [InlineData("[remote \"origin\"]\nurl = https://other.example/repo\n")]
    [InlineData("[remote \"origin\"]\nuploadpack = unsafe-command\n")]
    [InlineData("[url \"https://other.example/\"]\ninsteadOf = https://github.com/\n")]
    public async Task UnusualRemoteConfigurationIsSkipped(string extra)
    {
        var repo = await Fixture(extra); var process = new RecordingProcess();
        var result = await new GitRemoteChecker(process, new(new GitProcess())).CheckAsync(repo, default);
        Assert.Equal(RemoteOutcome.Skipped, result.Outcome); Assert.Empty(process.Calls);
    }
    [Theory]
    [InlineData("https://user:secret@example.com/repo")]
    [InlineData("https://example.com/repo?token=secret")]
    [InlineData("http://example.com/repo")]
    [InlineData("file:///E:/repo")]
    [InlineData("ext::command")]
    public void UnsafeTransportOrEmbeddedSecretsAreRejected(string url) => Assert.False(GitRemoteReader.SafeUrl(url));

    [Fact]
    public async Task FetchUsesExplicitDestinationsAndDoesNotLeakErrors()
    {
        var repo = await Fixture(); var process = new RecordingProcess();
        var checker = new GitRemoteChecker(process, new(new GitProcess()));
        Assert.Equal(RemoteOutcome.Success, (await checker.CheckAsync(repo, default)).Outcome);
        var args = process.Calls.Single(call => call.Contains("fetch"));
        foreach (var arg in new[] { "--refmap=", "--no-tags", "--no-prune", "--no-prune-tags", "--no-recurse-submodules", "--no-write-fetch-head", "--no-auto-maintenance", "--no-write-commit-graph", "core.hooksPath=/dev/null", "credential.helper=", "credential.helper=manager", "protocol.allow=never", "+refs/heads/*:refs/remotes/origin/*", "^refs/heads/HEAD" }) Assert.Contains(arg, args);
        process.Result = new(1, "", "fatal: Authentication failed: https://user:TOP_SECRET@example.com/repo?token=SECRET");
        var failure = await checker.CheckAsync(repo, default);
        Assert.Equal(RemoteOutcome.Authentication, failure.Outcome); Assert.DoesNotContain("SECRET", failure.Message); Assert.DoesNotContain("https://", failure.Message);
        process.Result = new(1, "", "Could not resolve host: example.invalid"); Assert.Equal(RemoteOutcome.Offline, (await checker.CheckAsync(repo, default)).Outcome);
        process.Timeout = true; Assert.Equal(RemoteOutcome.Timeout, (await checker.CheckAsync(repo, default)).Outcome);
        Assert.Empty(await TestWorkspace.GitAsync(repo.WorktreeRoot, "for-each-ref", "--format=%(refname)", "refs/heads"));
    }
    [Fact]
    public async Task SymbolicDestinationCannotRedirectFetchToLocalBranch()
    {
        var repo = await Fixture(); var process = new RecordingProcess { References = "refs/remotes/origin/main\trefs/heads/main\n" };
        var result = await new GitRemoteChecker(process, new(new GitProcess())).CheckAsync(repo, default);
        Assert.Equal(RemoteOutcome.Skipped, result.Outcome); Assert.DoesNotContain(process.Calls, call => call.Contains("fetch"));
    }
    [Fact]
    public async Task RemoteProcessHasNoninteractiveEnvironment()
    {
        var script = "$env:GIT_TERMINAL_PROMPT; $env:GCM_INTERACTIVE; $env:GIT_SSH_COMMAND; $env:SSH_ASKPASS_REQUIRE; $env:GCM_GUI_PROMPT; $env:GIT_ASKPASS";
        var result = await new GitProcess("powershell.exe", remote: true).RunAsync(["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
        Assert.Equal(0, result.ExitCode); Assert.Contains("0", result.Output); Assert.Contains("Never", result.Output);
        Assert.Contains("BatchMode=yes", result.Output); Assert.Contains("StrictHostKeyChecking=yes", result.Output); Assert.Contains("never", result.Output); Assert.Contains("false", result.Output); Assert.DoesNotContain("echo", result.Output);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OldProfilesCannotOptIntoRemoteAndRuleDeletionPersists(int version)
    {
        var area = new TestWorkspace(); var path = Path.Combine(area.Root, "state.json");
        await File.WriteAllTextAsync(path, $"{{\"schemaVersion\":{version},\"repositories\":[],\"remoteEnabled\":true,\"sources\":[]}}");
        var store = new JsonConfigurationStore(area.Root); var loaded = await store.LoadAsync();
        Assert.False(loaded.State.RemoteEnabled); Assert.Equal(2, loaded.State.Sources.Length); Assert.Equal(4, loaded.State.SchemaVersion);
        await store.SaveAsync(loaded.State with { RemoteEnabled = true, Sources = [] }, loaded.Revision);
        var roundtrip = await new JsonConfigurationStore(area.Root).LoadAsync(); Assert.True(roundtrip.State.RemoteEnabled); Assert.Empty(roundtrip.State.Sources);
    }
    [Fact]
    public async Task RealGitFetchToOwnedLoopbackEndpointTimesOutWithoutRefsOrCredentialPrompts()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var repo = await Fixture();
        await TestWorkspace.GitAsync(repo.WorktreeRoot, "config", "remote.origin.url", $"ssh://git@127.0.0.1:{port}/fixture.git");
        var before = await File.ReadAllBytesAsync(Path.Combine(repo.GitDir, "config"));
        var checker = new GitRemoteChecker(new GitProcess(timeout: TimeSpan.FromSeconds(3), remote: true), new(new GitProcess()));
        var operation = checker.CheckAsync(repo, default);
        using var client = await accept.WaitAsync(TimeSpan.FromSeconds(8)); // Own server intentionally never supplies an SSH greeting.
        var result = await operation;
        Assert.Equal(RemoteOutcome.Timeout, result.Outcome);
        Assert.False(File.Exists(Path.Combine(repo.GitDir, "FETCH_HEAD")));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(repo.GitDir, "config")));
        Assert.Empty(await TestWorkspace.GitAsync(repo.WorktreeRoot, "for-each-ref", "--format=%(refname)"));
    }
    private sealed class RecordingProcess : IGitProcess
    {
        public List<IReadOnlyList<string>> Calls = []; public GitResult Result = new(0, "", ""); public string References = ""; public bool Timeout;
        public Task<GitResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
        {
            Calls.Add(args.ToArray()); cancellationToken.ThrowIfCancellationRequested();
            if (args.Contains("for-each-ref")) return Task.FromResult(new GitResult(0, References, ""));
            if (Timeout) throw new GitExecutionException(StatusError.Timeout, "timeout");
            return Task.FromResult(Result);
        }
    }
}
