using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace RepoTracker.Desktop.TestHost.Performance;

internal static class PerformanceRun
{
    public static int Start(string outputDirectory, bool smoke)
    {
        var directory = PerformanceFixture.ValidateDirectory(outputDirectory);
        using var endpoint = new TcpListener(IPAddress.Loopback, 0);
        endpoint.Start();
        using var lifetime = new CancellationTokenSource();
        var endpointTask = AcceptAsync(endpoint, lifetime.Token);
        // Build fixtures before assigning this process to the accounting job.
        PerformanceFixture.CreateAsync(directory, ((IPEndPoint)endpoint.LocalEndpoint).Port).GetAwaiter().GetResult();
        using var accounting = new JobAccounting();
        var application = new App
        {
            StartupRegistrationFactory = profile => new FixtureStartupRegistration(profile),
            EnableTrayIcon = false,
            ShellAppId = "Septim.RepoTracker.TestHost"
        };
        application.InitializeComponent();
        application.DispatcherUnhandledException += (_, e) =>
        {
            File.WriteAllText(Path.Combine(directory, "failure.txt"), e.Exception.ToString());
            e.Handled = true; application.Shutdown(1);
        };
        application.Startup += (_, _) => application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
        {
            try
            {
                var deadline = Stopwatch.StartNew();
                while (application.MainWindow is not MainWindow)
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Main window startup timed out.");
                    await Task.Delay(50);
                }
                var window = (MainWindow)application.MainWindow;
                if (application.TrayIcon is not null) throw new InvalidOperationException("Performance fixtures must not register a tray icon.");
                await window.Initialization;
                await Task.Run(() => MeasureAsync(window, accounting, directory, smoke));
                application.Shutdown();
            }
            catch (Exception error)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), error.ToString());
                application.Shutdown(1);
            }
        });
        try { return application.Run(); }
        finally
        {
            lifetime.Cancel(); endpoint.Stop();
            endpointTask.GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(directory, "writer.stop"), "stop");
        }
    }

    private static async Task AcceptAsync(TcpListener listener, CancellationToken token)
    {
        try { while (!token.IsCancellationRequested) { using var client = await listener.AcceptTcpClientAsync(token); } }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task MeasureAsync(MainWindow window, JobAccounting accounting, string directory, bool smoke)
    {
        using var monitor = new SchedulerMeasurements(window);
        monitor.Start();
        using var process = Process.GetCurrentProcess();
        var summaries = new List<object>();
        var allSamples = new Dictionary<string, List<ResourceSample>>();
        var allChecks = new List<CheckMeasurement>();
        var localPeak = 0; var remotePeak = 0;
        var timings = smoke ? new[] { 3, 5, 3, 5, 6, 3 } : [30, 300, 60, 60, 90, 120];
        var phases = new[] { "warmup", "minimized", "expanded", "churn", "remote-failure", "recovery" };
        var openToRenderMs = 0.0;
        for (var phaseIndex = 0; phaseIndex < phases.Length; phaseIndex++)
        {
            var phase = phases[phaseIndex];
            if (phase == "expanded") openToRenderMs = await OpenToRenderAsync(window);
            if (phase == "remote-failure")
                await window.Dispatcher.InvokeAsync(async () =>
                {
                    await window.Model.Registry.UpdatePreferencesAsync(preferences => preferences with { RemoteIntervalMinutes = 1 });
                    await window.Model.Registry.SetRemoteEnabledAsync(true);
                }).Task.Unwrap();
            if (phase == "recovery")
                await window.Dispatcher.InvokeAsync(async () =>
                {
                    await window.Model.Registry.SetRemoteEnabledAsync(false);
                    window.CollapsePanel();
                }).Task.Unwrap();
            monitor.SetPhase(phase);
            await File.WriteAllTextAsync(Path.Combine(directory, "phase.pending"), phase);
            File.Move(Path.Combine(directory, "phase.pending"), Path.Combine(directory, "phase.txt"), true);
            var watch = Stopwatch.StartNew();
            var samples = new List<ResourceSample> { ReadSample(process, accounting, watch.Elapsed.TotalSeconds, 0) };
            while (watch.Elapsed.TotalSeconds < timings[phaseIndex])
            {
                await Task.Delay(1000);
                var ping = Stopwatch.StartNew();
                await window.Dispatcher.InvokeAsync(() =>
                {
                    // Retain the historical phase name; minimized now means hidden to the tray.
                    var expectedVisible = phase is not ("warmup" or "minimized" or "recovery");
                    if (window.IsVisible != expectedVisible || window.WindowState != WindowState.Normal ||
                        window.Model.Registry.State.RemoteEnabled != (phase == "remote-failure"))
                        throw new InvalidOperationException("Window or remote state changed during the performance phase.");
                }, DispatcherPriority.Input).Task.WaitAsync(TimeSpan.FromSeconds(10));
                samples.Add(ReadSample(process, accounting, watch.Elapsed.TotalSeconds, ping.Elapsed.TotalMilliseconds));
            }
            var readings = monitor.Read(phase);
            localPeak = Math.Max(localPeak, readings.LocalPeak);
            remotePeak = Math.Max(remotePeak, readings.RemotePeak);
            allChecks.AddRange(readings.Checks);
            allSamples.Add(phase, samples);
            var first = samples[0]; var last = samples[^1];
            var seconds = last.Seconds - first.Seconds;
            var totalCpu = (last.JobCpuMs - first.JobCpuMs) / seconds / 10;
            var appCpu = (last.AppCpuMs - first.AppCpuMs) / seconds / 10;
            var local = readings.Checks.Where(check => check.Kind == "local").ToArray();
            var remote = readings.Checks.Where(check => check.Kind == "remote").ToArray();
            summaries.Add(new
            {
                Phase = phase, Seconds = seconds, AppCpuCorePercent = appCpu, DescendantCpuCorePercent = Math.Max(0, totalCpu - appCpu),
                TotalCpuCorePercent = totalCpu, TotalCpuMachinePercent = totalCpu / Environment.ProcessorCount,
                AppWorkingSetMeanMiB = samples.Average(sample => sample.AppWorkingSetMiB), AppWorkingSetPeakMiB = samples.Max(sample => sample.AppWorkingSetMiB),
                AppPrivateStartMiB = first.AppPrivateMiB, AppPrivateEndMiB = last.AppPrivateMiB, AppPrivatePeakMiB = samples.Max(sample => sample.AppPrivateMiB),
                ManagedStartMiB = first.ManagedMiB, ManagedEndMiB = last.ManagedMiB,
                AllocatedMiB = last.AllocatedMiB - first.AllocatedMiB,
                SampledDescendantWorkingSetPeakMiB = samples.Max(sample => sample.DescendantWorkingSetMiB),
                DescendantsCreated = last.TotalProcesses - first.TotalProcesses, SampledActiveDescendantsPeak = samples.Max(sample => sample.ActiveDescendants),
                HandlesStart = first.Handles, HandlesEnd = last.Handles, HandlesPeak = samples.Max(sample => sample.Handles),
                ThreadsPeak = samples.Max(sample => sample.Threads),
                DispatcherP95Ms = Percentile(samples.Skip(1).Select(sample => sample.DispatcherMs), .95),
                DispatcherMaxMs = samples.Max(sample => sample.DispatcherMs),
                LocalChecks = local.Length, LocalErrorCount = local.Count(check => check.Outcome != "None"), LocalConcurrencyPeak = readings.LocalPeak,
                LocalCheckP95Ms = Percentile(local.Select(check => check.DurationMs), .95), LocalCheckMaxMs = local.Select(check => check.DurationMs).DefaultIfEmpty().Max(),
                RemoteChecks = remote.Length, RemoteConcurrencyPeak = readings.RemotePeak,
                RemoteOutcomes = remote.GroupBy(check => check.Outcome).ToDictionary(group => group.Key, group => group.Count()),
                RemoteCheckP95Ms = Percentile(remote.Select(check => check.DurationMs), .95)
            });
            await File.WriteAllTextAsync(Path.Combine(directory, "progress.json"), JsonSerializer.Serialize(summaries, PerformanceFixture.Json));
        }
        var cleanup = Stopwatch.StartNew();
        var localSnapshots = window.StatusScheduler.Snapshot;
        var completedRepositories = localSnapshots.Values.Count(snapshot => snapshot.SucceededAt is not null && snapshot.Watching);
        await window.RemoteScheduler.DisposeAsync();
        await window.StatusScheduler.DisposeAsync();
        var cleanupMs = cleanup.Elapsed.TotalMilliseconds;
        var remaining = accounting.Read().ActiveProcesses - 1;
        var coveragePassed = completedRepositories == 20 && localPeak <= 2 && remotePeak == 1 &&
            allChecks.Count(check => check.Kind == "remote" && check.Outcome == "Offline") >= (smoke ? 1 : 20) &&
            allChecks.All(check => check.Kind != "local" || check.Outcome == "None");
        await File.WriteAllTextAsync(Path.Combine(directory, "samples.json"), JsonSerializer.Serialize(allSamples, PerformanceFixture.Json));
        await File.WriteAllTextAsync(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(allChecks, PerformanceFixture.Json));
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
        {
            Passed = remaining == 0 && coveragePassed, Smoke = smoke,
            At = DateTimeOffset.Now, Framework = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount,
            Measurement = "One-second samples; Windows Job Object CPU/process totals include exited descendants. Load writer is a sibling outside job. Harness/loopback server overhead included. No forced GC.",
            Limitations = "Descendant memory and active-process peaks are sampled lower bounds. Dispatcher latency is an input-priority queue probe. Check durations observe scheduler events, attributed at completion. Remote workload is loopback failure, not a successful fetch.",
            OpenToRenderMs = openToRenderMs, CleanupMs = cleanupMs, RemainingDescendants = remaining,
            RepositoriesObserved = localSnapshots.Count, CompletedAndWatchingRepositories = completedRepositories, Phases = summaries
        }, PerformanceFixture.Json));
        if (remaining != 0) throw new InvalidOperationException($"{remaining} descendant processes remain after scheduler shutdown.");
    }

    private static ResourceSample ReadSample(Process process, JobAccounting accounting, double seconds, double dispatcherMs)
    {
        process.Refresh();
        var job = accounting.Read();
        return new(seconds, job.CpuMs, process.TotalProcessorTime.TotalMilliseconds,
            process.WorkingSet64 / 1048576.0, process.PrivateMemorySize64 / 1048576.0,
            GC.GetTotalMemory(false) / 1048576.0, GC.GetTotalAllocatedBytes() / 1048576.0,
            accounting.ActiveDescendantWorkingSet() / 1048576.0, job.TotalProcesses, job.ActiveProcesses - 1,
            process.HandleCount, process.Threads.Count, dispatcherMs);
    }
    private static async Task<double> OpenToRenderAsync(MainWindow window)
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler callback = (_, _) => rendered.TrySetResult();
        var watch = Stopwatch.StartNew();
        await window.Dispatcher.InvokeAsync(() => { CompositionTarget.Rendering += callback; window.RestorePanel(); });
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10)); return watch.Elapsed.TotalMilliseconds; }
        finally { await window.Dispatcher.InvokeAsync(() => CompositionTarget.Rendering -= callback); }
    }
    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    }
    private sealed record ResourceSample(double Seconds, double JobCpuMs, double AppCpuMs, double AppWorkingSetMiB,
        double AppPrivateMiB, double ManagedMiB, double AllocatedMiB, double DescendantWorkingSetMiB,
        uint TotalProcesses, uint ActiveDescendants, int Handles, int Threads, double DispatcherMs);
}
