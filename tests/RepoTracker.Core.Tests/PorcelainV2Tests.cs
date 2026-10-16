using RepoTracker.Core.Status;

namespace RepoTracker.Core.Tests;

public sealed class PorcelainV2Tests
{
    private const string Hash = "1234567890123456789012345678901234567890";
    private static string Header(string head = "main", string oid = Hash) => $"# branch.oid {oid}\0# branch.head {head}\0";
    private static string Ordinary(string xy, string path = "文件.txt", string sub = "N...") => $"1 {xy} {sub} 100644 100644 100644 {Hash} {Hash} {path}\0";

    [Fact]
    public void CleanWithoutUpstreamDoesNotInventCounts()
    {
        var status = PorcelainV2Parser.Parse(Header());
        Assert.False(status.IsDirty); Assert.Equal("main", status.Branch);
        Assert.Null(status.Upstream); Assert.Null(status.Ahead); Assert.Null(status.Behind);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(2, 0)] [InlineData(0, 5)] [InlineData(2, 5)]
    public void KnownUpstreamAndDirtyStateAreIndependent(int ahead, int behind)
    {
        var status = PorcelainV2Parser.Parse(Header() + $"# branch.upstream origin/main\0# branch.ab +{ahead} -{behind}\0" + Ordinary("MM"));
        Assert.Equal(ahead, status.Ahead); Assert.Equal(behind, status.Behind);
        Assert.True(status.IsDirty); Assert.Equal(1, status.Staged); Assert.Equal(1, status.Unstaged);
    }

    [Fact]
    public void UpstreamWithoutBranchAbRemainsUnknown()
    {
        var status = PorcelainV2Parser.Parse(Header() + "# branch.upstream origin/main\0# future whatever\0");
        Assert.Equal("origin/main", status.Upstream); Assert.Null(status.Ahead); Assert.Null(status.Behind);
    }

    [Fact]
    public void RenamesConsumeExactlyTwoNulPathsRegardlessOfTheirContents()
    {
        var status = PorcelainV2Parser.Parse(Header() +
            $"2 R. N... 100644 100644 100644 {Hash} {Hash} R100 new\n中文 name\0? old name\0" +
            Ordinary(".M", "with\ttab") + "? new directory/\0! ignored\0");
        Assert.Equal(1, status.Staged); Assert.Equal(1, status.Unstaged); Assert.Equal(1, status.Untracked); Assert.Equal(0, status.Conflicts);
    }

    [Theory]
    [InlineData("UU")] [InlineData("AA")] [InlineData("DD")] [InlineData("AU")]
    public void ConflictsAreNotMisreportedAsOrdinaryChanges(string conflict)
    {
        var status = PorcelainV2Parser.Parse(Header() + $"u {conflict} N... 100644 100644 100644 100644 {Hash} {Hash} {Hash} conflict.txt\0");
        Assert.Equal(1, status.Conflicts); Assert.True(status.IsDirty); Assert.Equal(0, status.Staged);
    }

    [Fact]
    public void DetachedAndUnbornAreExplicit()
    {
        var detached = PorcelainV2Parser.Parse(Header("(detached)"));
        Assert.Equal(HeadKind.Detached, detached.HeadKind); Assert.Equal(Hash, detached.Commit); Assert.Null(detached.Branch);
        var unborn = PorcelainV2Parser.Parse(Header("new", "(initial)") + "? file\0");
        Assert.Equal(HeadKind.Unborn, unborn.HeadKind); Assert.Null(unborn.Commit); Assert.Null(unborn.Ahead); Assert.True(unborn.IsDirty);
    }

    [Theory]
    [InlineData("S.M.")] [InlineData("S..U")] [InlineData("SC..")] [InlineData("SCMU")]
    public void SubmoduleChangesAreDirty(string sub)
    {
        var status = PorcelainV2Parser.Parse(Header() + Ordinary("..", sub: sub));
        Assert.True(status.IsDirty);
    }

    [Theory]
    [InlineData("")] [InlineData("? truncated")] [InlineData("x unknown\0")]
    [InlineData("1 M. N... broken\0")] [InlineData("# branch.ab +? -0\0")]
    [InlineData("# branch.oid wrong\0")] [InlineData("# branch.head duplicate\0")]
    public void IncompleteOrUnknownRecordsNeverLookClean(string suffix)
    {
        var output = suffix.Length == 0 ? "" : Header() + suffix;
        Assert.Throws<FormatException>(() => PorcelainV2Parser.Parse(output));
    }

    [Fact]
    public void MissingOriginalRenamePathFails()
    {
        Assert.Throws<FormatException>(() => PorcelainV2Parser.Parse(Header() + $"2 R. N... 100644 100644 100644 {Hash} {Hash} R100 new\0"));
    }
}
