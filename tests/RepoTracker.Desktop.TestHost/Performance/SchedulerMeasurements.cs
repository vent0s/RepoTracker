using System.Diagnostics;
using RepoTracker.Core.Status;

namespace RepoTracker.Desktop.TestHost.Performance;

internal sealed record CheckMeasurement(string Phase, string Kind, Guid Repository, double DurationMs, string Outcome);

internal sealed class SchedulerMeasurements(MainWindow window) : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<(string, Guid), long> starts = [];
    private readonly List<CheckMeasurement> checks = [];
    private string phase = "warmup";
    private int localPeak, remotePeak;
    public void Start()
    {
        window.StatusScheduler.Changed += LocalChanged;
        window.RemoteScheduler.Changed += RemoteChanged;
    }
    public void SetPhase(string value)
    { lock (sync) { phase = value; localPeak = remotePeak = 0; } }
    public (CheckMeasurement[] Checks, int LocalPeak, int RemotePeak) Read(string value)
    { lock (sync) return (checks.Where(check => check.Phase == value).ToArray(), localPeak, remotePeak); }
    private void LocalChanged(object? sender, EventArgs e)
    {
        lock (sync)
        {
            var snapshot = window.StatusScheduler.Snapshot;
            localPeak = Math.Max(localPeak, snapshot.Values.Count(state => state.IsChecking));
            foreach (var (id, state) in snapshot)
                Observe("local", id, state.IsChecking, state.Error.ToString());
        }
    }
    private void RemoteChanged(object? sender, EventArgs e)
    {
        lock (sync)
        {
            var snapshot = window.RemoteScheduler.Snapshot;
            remotePeak = Math.Max(remotePeak, snapshot.Values.Count(state => state.IsChecking));
            foreach (var (id, state) in snapshot)
                Observe("remote", id, state.IsChecking, state.Outcome.ToString());
        }
    }
    private void Observe(string kind, Guid id, bool running, string outcome)
    {
        var key = (kind, id);
        if (running) starts.TryAdd(key, Stopwatch.GetTimestamp());
        else if (starts.Remove(key, out var start))
            checks.Add(new(phase, kind, id, Stopwatch.GetElapsedTime(start).TotalMilliseconds, outcome));
    }
    public void Dispose()
    {
        window.StatusScheduler.Changed -= LocalChanged;
        window.RemoteScheduler.Changed -= RemoteChanged;
    }
}
