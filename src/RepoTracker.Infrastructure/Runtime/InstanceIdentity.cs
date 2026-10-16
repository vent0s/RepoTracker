using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace RepoTracker.Infrastructure.Runtime;

public static class InstanceIdentity
{
    public static string ForProfile(string profileDirectory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory)).ToUpperInvariant();
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return "RepoTracker-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user + "|" + path)))[..32];
    }
}
