using RepoTracker.Core.Localization;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Core.Sources;

public enum SourceIcon { Git, GitHub, GitLab, Server }
public sealed record SourceRule(Guid Id, string Name, string Pattern, SourceIcon Icon);
// Only the host crosses the process boundary into UI/state; never retain credentials or repository URLs there.
public sealed record RemoteIdentity(bool HasRemote, string? Host = null, string? RemoteName = null, bool ReadFailed = false);
public sealed record SourceMatch(string Name, SourceIcon Icon, string Explanation, string? Marker = null)
{
    public string Badge => Marker ?? (Icon switch { SourceIcon.GitHub => "GH", SourceIcon.GitLab => "GL", SourceIcon.Server => Text.Get("Source.Server"), _ => "Git" });
}

public static partial class SourceRules
{
    public static ImmutableArray<SourceRule> Defaults => [
        new(new("439d93d4-9501-432c-802f-ed5a696fb844"), "GitHub", "github.com", SourceIcon.GitHub),
        new(new("cb04d038-319a-462d-9d1f-ddd9c8f70568"), "GitLab", "gitlab.com", SourceIcon.GitLab)];

    public static string? Host(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsControl)) return null;
        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "ssh" or "git")) return null;
            return NormalizeHost(uri.IdnHost);
        }
        if (Regex.IsMatch(url, @"^[a-zA-Z]:", RegexOptions.CultureInvariant)) return null;
        var match = Scp().Match(url);
        return match.Success ? NormalizeHost(match.Groups[1].Value.Trim('[', ']')) : null;
    }

    private static string? NormalizeHost(string host)
    {
        try
        {
            var normalized = new IdnMapping().GetAscii(host.TrimEnd('.')).ToLowerInvariant();
            return Uri.CheckHostName(normalized) == UriHostNameType.Unknown ? null : normalized;
        }
        catch (ArgumentException) { return null; }
    }

    public static SourceRule Validate(SourceRule rule, IEnumerable<SourceRule> existing)
    {
        if (rule.Id == Guid.Empty || rule.Name is null || rule.Pattern is null || !Enum.IsDefined(rule.Icon)) throw new TrackerException(Text.Get("Source.Invalid"));
        var name = rule.Name.Trim();
        if (name.Length is < 1 or > 30 || name.Any(char.IsControl)) throw new TrackerException(Text.Get("Source.NameLength"));
        var pattern = rule.Pattern.Trim();
        var wildcard = pattern.StartsWith("*.", StringComparison.Ordinal);
        var host = NormalizeHost(wildcard ? pattern[2..] : pattern);
        if (host is null || (wildcard && (Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.'))))
            throw new TrackerException(Text.Get("Source.InvalidHost"));
        pattern = (wildcard ? "*." : "") + host;
        if (existing.Any(item => item.Id != rule.Id && string.Equals(item.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
            throw new TrackerException(Text.Get("Source.Duplicate"));
        return rule with { Name = name, Pattern = pattern };
    }

    public static SourceMatch Match(RemoteIdentity? identity, IEnumerable<SourceRule> rules)
    {
        if (identity is null || identity.ReadFailed) return new(Text.Get("Source.Pending"), SourceIcon.Git, Text.Get("Source.NotRead"), "…");
        if (!identity.HasRemote) return new(Text.Get("Source.Local"), SourceIcon.Git, Text.Get("Remote.None"));
        if (identity.Host is not { } host) return new(Text.Get("Source.Unknown"), SourceIcon.Git, Text.Get("Source.Unsupported"), "?");
        var rule = rules.Where(rule => rule.Pattern == host || rule.Pattern.StartsWith("*.", StringComparison.Ordinal) &&
                host.EndsWith(rule.Pattern[1..], StringComparison.Ordinal) && host.Length > rule.Pattern.Length - 1)
            .OrderBy(rule => rule.Pattern.StartsWith("*.", StringComparison.Ordinal))
            .ThenByDescending(rule => rule.Pattern.Length).ThenBy(rule => rule.Id).FirstOrDefault();
        return rule is null ? new(Text.Get("Source.Unknown"), SourceIcon.Git, Text.Format("Source.NoMatch", host), "?")
            : new(rule.Name, rule.Icon, Text.Format("Source.Match", host, rule.Pattern));
    }

    [GeneratedRegex(@"^(?:[^@/\s:]+@)?(\[[^\]]+\]|[^/:\s]+):[^\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Scp();
}
