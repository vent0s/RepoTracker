using System.Diagnostics;
using System.IO;

namespace RepoTracker.Tests.Shared;

public sealed class TestWorkspace
{
    public string Root { get; }
    public TestWorkspace(string? directory = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RepoTracker.slnx"))) root = root.Parent;
        Root = directory ?? Path.Combine(root?.FullName ?? throw new DirectoryNotFoundException("Solution not found"), "tests", "artifacts", "data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public async Task<string> CreateDetachedRepositoryAsync(string name)
    {
        var repository = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.Combine(repository, ".git", "objects"));
        Directory.CreateDirectory(Path.Combine(repository, ".git", "refs"));
        await File.WriteAllTextAsync(Path.Combine(repository, ".git", "config"), "[core]\nrepositoryformatversion = 0\nbare = false\n");
        // A detached HEAD and objects only: no git init, named branches, upstream or real repository is used.
        await File.WriteAllTextAsync(Path.Combine(repository, ".git", "HEAD"), new string('0', 40) + "\n");
        var tree = (await GitAsync(repository, "hash-object", "-t", "tree", "-w", "--stdin")).Trim();
        var commit = (await GitAsync(repository, "-c", "user.name=RepoTracker test", "-c", "user.email=test@example.invalid",
            "commit-tree", tree, "-m", "isolated fixture")).Trim();
        await File.WriteAllTextAsync(Path.Combine(repository, ".git", "HEAD"), commit + "\n");
        await File.WriteAllTextAsync(Path.Combine(repository, "keep.txt"), "This fixture file must not be changed.\n");
        return repository;
    }

    public async Task<string> CreateLinkedWorktreeAsync(string repository, string name)
    {
        var linked = Path.Combine(Root, name);
        // Build the documented linked-worktree fixture directly. No branch or tracking is created.
        var administration = Path.Combine(repository, ".git", "worktrees", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(administration);
        Directory.CreateDirectory(linked);
        await File.WriteAllTextAsync(Path.Combine(linked, ".git"), "gitdir: " + administration + "\n");
        await File.WriteAllTextAsync(Path.Combine(administration, "gitdir"), Path.Combine(linked, ".git") + "\n");
        await File.WriteAllTextAsync(Path.Combine(administration, "commondir"), "../..\n");
        await File.WriteAllTextAsync(Path.Combine(administration, "HEAD"), await File.ReadAllTextAsync(Path.Combine(repository, ".git", "HEAD")));
        if (!string.IsNullOrWhiteSpace(await GitAsync(repository, "for-each-ref", "--format=%(refname)", "refs/heads")))
            throw new InvalidOperationException("Fixture unexpectedly created a named branch.");
        return linked;
    }

    public static Task<string> GitAsync(string repository, params string[] arguments) => GitInputAsync(repository, "", arguments);

    public static async Task CommitFixtureAsync(string repository)
    {
        // Test-only detached commit; never creates a branch and never runs commit hooks.
        await GitAsync(repository, "add", "--", "keep.txt");
        var tree = (await GitAsync(repository, "write-tree")).Trim();
        var commit = (await GitAsync(repository, "-c", "user.name=RepoTracker test", "-c", "user.email=test@example.invalid", "commit-tree", tree, "-m", "tracked fixture")).Trim();
        await File.WriteAllTextAsync(Path.Combine(repository, ".git", "HEAD"), commit + "\n");
    }

    public static async Task<string> GitInputAsync(string repository, string input, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(repository);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException(await stderr);
        return await stdout;
    }
}
