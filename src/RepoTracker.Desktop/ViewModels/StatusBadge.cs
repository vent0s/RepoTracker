namespace RepoTracker.Desktop.ViewModels;

public sealed record StatusBadge(string Glyph, string Description, string Tone = "Muted", string? Details = null)
{
    public string Tooltip => Details is null ? Description : Description + "\n" + Details;
}
