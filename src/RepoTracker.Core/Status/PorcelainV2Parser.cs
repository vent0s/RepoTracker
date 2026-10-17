using RepoTracker.Core.Localization;
using System.Globalization;

namespace RepoTracker.Core.Status;

public static class PorcelainV2Parser
{
    public static GitStatus Parse(string output)
    {
        // Never split paths by newline: -z permits literal newlines, spaces and tabs in names.
        if (!output.EndsWith('\0')) throw Invalid();
        var records = output.Split('\0');
        string? head = null, oid = null, upstream = null;
        int? ahead = null, behind = null;
        var staged = 0; var unstaged = 0; var untracked = 0; var conflicts = 0;
        var headers = new HashSet<string>();
        for (var index = 0; index < records.Length - 1; index++)
        {
            var record = records[index];
            if (record.StartsWith("# ", StringComparison.Ordinal))
            {
                var split = record.IndexOf(' ', 2);
                if (split < 0) continue; // Unknown headers are explicitly extensible.
                var key = record[2..split]; var value = record[(split + 1)..];
                if (key is not ("branch.head" or "branch.oid" or "branch.upstream" or "branch.ab")) continue;
                if (!headers.Add(key) || value.Length == 0) throw Invalid();
                switch (key)
                {
                    case "branch.head": head = value; break;
                    case "branch.oid": oid = value; break;
                    case "branch.upstream": upstream = value; break;
                    case "branch.ab":
                        var counts = value.Split(' ');
                        if (counts.Length != 2 || !counts[0].StartsWith('+') || !counts[1].StartsWith('-') ||
                            !int.TryParse(counts[0].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var a) ||
                            !int.TryParse(counts[1].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var b)) throw Invalid();
                        ahead = a; behind = b; break;
                }
                continue;
            }
            if (record.StartsWith("? ", StringComparison.Ordinal)) { if (record.Length < 3) throw Invalid(); untracked++; continue; }
            if (record.StartsWith("! ", StringComparison.Ordinal)) { if (record.Length < 3) throw Invalid(); continue; }
            if (record.Length < 2 || record[1] != ' ' || record[0] is not ('1' or '2' or 'u')) throw Invalid();
            var fields = record.Split(' ', record[0] == '1' ? 9 : record[0] == '2' ? 10 : 11);
            var expected = record[0] == '1' ? 9 : record[0] == '2' ? 10 : 11;
            if (fields.Length != expected || fields[^1].Length == 0 || fields[1].Length != 2 ||
                fields[1].Any(c => !".MTADRCU".Contains(c)) || fields[2].Length != 4 ||
                (fields[2] != "N..." && !(fields[2][0] == 'S' && ".C".Contains(fields[2][1]) && ".M".Contains(fields[2][2]) && ".U".Contains(fields[2][3])))) throw Invalid();
            if (record[0] == '2')
            {
                if (++index >= records.Length - 1 || records[index].Length == 0) throw Invalid();
            }
            if (record[0] == 'u') { conflicts++; continue; }
            if (fields[1][0] != '.') staged++;
            if (fields[1][1] != '.' || (fields[2][0] == 'S' && fields[2][1..].Any(c => c != '.'))) unstaged++;
        }
        if (head is null || oid is null || (oid != "(initial)" && (oid.Length is not (40 or 64) || !oid.All(Uri.IsHexDigit)))) throw Invalid();
        var kind = oid == "(initial)" ? HeadKind.Unborn : head == "(detached)" ? HeadKind.Detached : HeadKind.Branch;
        if (kind == HeadKind.Unborn && head == "(detached)") throw Invalid();
        // Counts are comparable only when both a commit and an upstream are present.
        if (upstream is null || kind != HeadKind.Branch) { ahead = null; behind = null; }
        return new(kind, kind == HeadKind.Detached ? null : head, kind == HeadKind.Unborn ? null : oid,
            upstream, ahead, behind, staged, unstaged, untracked, conflicts);
    }
    private static FormatException Invalid() => new(Text.Get("Git.ParseFailed"));
}
