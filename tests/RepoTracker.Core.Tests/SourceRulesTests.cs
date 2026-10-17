using RepoTracker.Core.Sources;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Core.Tests;

public sealed class SourceRulesTests
{
    [Theory]
    [InlineData("https://GitHub.COM/org/repo.git", "github.com")]
    [InlineData("ssh://git@GitLab.com:2222/org/repo.git", "gitlab.com")]
    [InlineData("git@Code.Example.com:team/repo.git", "code.example.com")]
    [InlineData("code.example.com:team/repo.git", "code.example.com")]
    [InlineData("https://user:secret@GitHub.com/repo?token=secret", "github.com")]
    [InlineData("https://github.com.evil.test/repo", "github.com.evil.test")]
    [InlineData("https://GITHUB.com./repo", "github.com")]
    [InlineData("E:\\repo", null)]
    [InlineData("E:repo", null)]
    [InlineData("../repo", null)]
    [InlineData("file:///E:/repo", null)]
    [InlineData("ext::arbitrary command", null)]
    [InlineData("https://github.com\n/repo", null)]
    public void UrlParsingDoesNotExposeCredentials(string url, string? host) => Assert.Equal(host, SourceRules.Host(url));

    [Fact]
    public void PrecedenceIsExactThenLongestLabelSuffix()
    {
        var broad = Rule("通用", "*.example.com"); var narrow = Rule("研发", "*.dev.example.com"); var exact = Rule("主机", "code.dev.example.com");
        SourceMatch Match(string host) => SourceRules.Match(new(true, host), [broad, narrow, exact]);
        Assert.Equal("主机", Match("code.dev.example.com").Name);
        Assert.Equal("研发", Match("other.dev.example.com").Name);
        Assert.Equal("通用", Match("dev.example.com").Name);
        Assert.Equal(RepoTracker.Core.Localization.Text.Get("Source.Unknown"), Match("example.com").Name);
        Assert.Equal(RepoTracker.Core.Localization.Text.Get("Source.Unknown"), Match("evil-example.com").Name);
        Assert.Equal(RepoTracker.Core.Localization.Text.Get("Source.Unknown"), Match("example.com.evil.test").Name);
        Assert.Contains("code.dev.example.com", Match("code.dev.example.com").Explanation);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("example.com/path")]
    [InlineData("example.com:443")]
    [InlineData("*example.com")]
    [InlineData("*.com")]
    public void InvalidPatternsAreRejected(string pattern) => Assert.Throws<TrackerException>(() => SourceRules.Validate(Rule("规则", pattern), []));

    [Fact]
    public void DuplicateRulesAreRejectedAndLocalUnknownPendingStayDistinct()
    {
        var rule = SourceRules.Validate(Rule(" 自建 ", "*.EXAMPLE.COM"), []);
        Assert.Equal("*.example.com", rule.Pattern); Assert.Equal("自建", rule.Name);
        Assert.Throws<TrackerException>(() => SourceRules.Validate(Rule("重复", "*.example.com"), [rule]));
        Assert.Equal(RepoTracker.Core.Localization.Text.Get("Source.Local"), SourceRules.Match(new(false), []).Name);
        Assert.Equal(RepoTracker.Core.Localization.Text.Get("Source.Unknown"), SourceRules.Match(new(true), []).Name);
        Assert.Equal("?", SourceRules.Match(new(true), []).Badge);
        Assert.Equal(RepoTracker.Core.Localization.Text.Get("Source.Pending"), SourceRules.Match(null, []).Name);
        Assert.Equal("GitHub", SourceRules.Match(new(true, "github.com"), SourceRules.Defaults).Name);
        Assert.Equal(RepoTracker.Core.Localization.Text.Get("Source.Unknown"), SourceRules.Match(new(true, "github.com"), []).Name);
    }
    private static SourceRule Rule(string name, string pattern) => new(Guid.NewGuid(), name, pattern, SourceIcon.Server);
}
