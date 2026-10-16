using System.IO;
using RepoTracker.Core.Tracking;

namespace RepoTracker.Desktop.TestHost;

// The test host never writes the real user's login entries.
internal sealed class FixtureStartupRegistration(string profile) : IStartupRegistration
{
    private readonly string path = Path.Combine(profile, "test-startup.txt");
    public bool Fail { get; set; }
    public bool IsEnabled => File.Exists(path);
    public void SetEnabled(bool enabled)
    {
        if (Fail) throw new IOException("Injected startup permission failure");
        if (enabled) File.WriteAllText(path, "isolated login registration");
        else File.Delete(path);
    }
}
