using System.IO;

namespace RepoTracker.Desktop;

public static class StartupOptions
{
    public sealed record Options(string Profile, bool Minimized);
    public static string ProfileDirectory(string[] args) => Parse(args).Profile;
    public static Options Parse(string[] args)
    {
        string? profile = null; var minimized = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--profile" && profile is null && index + 1 < args.Length && Path.IsPathFullyQualified(args[index + 1]))
                profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[++index]));
            else if (args[index] == "--minimized" && !minimized) minimized = true;
            else throw new ArgumentException("Expected [--profile <absolute-directory>] [--minimized].");
        }
        return new(profile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepoTracker"), minimized);
    }
}
