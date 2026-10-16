using RepoTracker.Core.Sources;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Infrastructure.Git;

// Raw configuration exists only during inspection/fetch and is never logged, persisted, or sent to the UI.
public sealed record RemoteConfiguration(RemoteIdentity Identity, string? Url, IReadOnlyList<string> Refspecs, string? SkipReason);

public sealed class GitRemoteReader(IGitProcess git)
{
    public async Task<RemoteConfiguration> ReadAsync(TrackedRepository repo, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(["--no-optional-locks", "--git-dir=" + repo.GitDir, "--work-tree=" + repo.WorktreeRoot,
            "-C", repo.WorktreeRoot, "config", "--null", "--get-regexp", @"^(remote\..*\.(url|fetch|mirror|uploadpack)|url\..*\.insteadof)$"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1) return new(new(false), null, [], "没有配置 remote。");
        if (result.ExitCode != 0) return new(new(false, ReadFailed: true), null, [], "无法读取 remote 配置。");
        var values = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(item =>
        {
            var separator = item.IndexOf('\n');
            if (separator < 1) throw new FormatException("Invalid Git config output.");
            return new KeyValuePair<string, string>(item[..separator], item[(separator + 1)..]);
        }).ToArray();
        var names = values.Where(pair => pair.Key.StartsWith("remote.", StringComparison.Ordinal) && pair.Key.EndsWith(".url", StringComparison.Ordinal))
            .Select(pair => pair.Key[7..^4]).Distinct(StringComparer.Ordinal).OrderBy(name => name != "origin").ThenBy(name => name, StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return new(new(false), null, [], "没有配置 remote。");
        var name = names[0];
        string[] Values(string key) => values.Where(pair => pair.Key == key).Select(pair => pair.Value).ToArray();
        var urls = Values($"remote.{name}.url");
        var url = urls[0];
        var identity = new RemoteIdentity(true, SourceRules.Host(url), name);
        var refs = Values($"remote.{name}.fetch");
        string? skip = null;
        if (urls.Length != 1) skip = "存在多个 remote URL，已跳过自动 fetch。";
        else if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]*$")) skip = "remote 名称不受支持。";
        else if (Values($"remote.{name}.mirror").Any(value => value is not ("false" or "no" or "off" or "0"))) skip = "镜像 remote 不支持自动检查。";
        else if (Values($"remote.{name}.uploadpack").Length > 0) skip = "自定义 upload-pack 不支持自动检查。";
        else if (values.Any(pair => pair.Key.StartsWith("url.", StringComparison.Ordinal) && url.StartsWith(pair.Value, StringComparison.Ordinal))) skip = "URL 重写配置需要手动检查，已跳过 fetch。";
        else if (!SafeUrl(url)) skip = "仅支持不含密码或查询参数的 HTTPS / SSH remote；本地路径和其他协议已跳过。";
        else if (refs.Length != 1 || !SafeRefspec(name, refs[0])) skip = "fetch 映射不受支持；仅接受单一 heads → 对应 remote-tracking 映射。";
        return new(identity, url, refs, skip);
    }

    public static bool SafeUrl(string url)
    {
        if (SourceRules.Host(url) is null || url.Any(char.IsWhiteSpace)) return false;
        if (url.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh" &&
                uri.Query.Length == 0 && uri.Fragment.Length == 0 && !uri.UserInfo.Contains(':') &&
                (uri.Scheme != "https" || uri.UserInfo.Length == 0);
        return !url.StartsWith('-') && !url.Contains("::", StringComparison.Ordinal);
    }

    public static bool SafeRefspec(string remote, string refspec)
    {
        var parts = refspec.TrimStart('+').Split(':');
        if (parts.Length != 2 || !parts[0].StartsWith("refs/heads/", StringComparison.Ordinal) || !parts[1].StartsWith($"refs/remotes/{remote}/", StringComparison.Ordinal)) return false;
        var source = parts[0][11..]; var destination = parts[1][($"refs/remotes/{remote}/").Length..];
        if (source != destination) return false;
        if (source == "*") return true;
        return source.Length > 0 && source != "HEAD" && source.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') && !part.EndsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal)) &&
            !source.Contains("..", StringComparison.Ordinal) && !source.Contains("@{", StringComparison.Ordinal) && !source.Any(c => char.IsControl(c) || " ~^:?*[\\".Contains(c));
    }
}
