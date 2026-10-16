using System.Text;

namespace RepoTracker.Infrastructure.Runtime;

public sealed class SessionLog
{
    private readonly object gate = new();
    private int lines;
    public string FilePath { get; }

    public SessionLog(string profileDirectory)
    {
        FilePath = Path.Combine(profileDirectory, "logs", $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
    }

    public void Write(string eventName)
    {
        lock (gate)
        {
            if (lines >= 10000) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTime.UtcNow:O} {eventName}{Environment.NewLine}", Encoding.UTF8);
                lines++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
