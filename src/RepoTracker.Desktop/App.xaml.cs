using RepoTracker.Core.Localization;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using RepoTracker.Desktop.Windows;
using RepoTracker.Infrastructure.Runtime;
using RepoTracker.Core.Tracking;
using RepoTracker.Infrastructure.Configuration;
using RepoTracker.Infrastructure.Git;
using RepoTracker.Core.Status;

namespace RepoTracker.Desktop;

public partial class App : Application
{
    private SingleInstance? instance;
    private SessionLog? log;
    private LocalStatusScheduler? scheduler;
    private RemoteScheduler? remoteScheduler;
    private InstallationSession? installation;
    internal TrayIcon? TrayIcon { get; private set; }
    // Only the in-process test host can opt out; the shipped app always has its tray entry.
    internal bool EnableTrayIcon { get; init; } = true;
    internal string? ShellAppId { get; init; }
    public Func<string, IStartupRegistration>? StartupRegistrationFactory { get; init; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var systemDisplayCulture = CultureInfo.CurrentUICulture;
        try
        {
            var options = StartupOptions.Parse(e.Args);
            var profile = options.Profile;
            var identity = InstanceIdentity.ForProfile(profile);
            log = new SessionLog(profile);
            // Profile isolation belongs to the mutex/pipe, not the shell's application identity.
            NativeWindows.SetAppId(ShellAppId ?? "Septim.RepoTracker");
            instance = new SingleInstance(identity);
            if (!instance.IsPrimary)
            {
                if (options.Minimized) { log.Write("secondary.startup.ignored"); Shutdown(); return; }
                var activated = await instance.ActivatePrimaryAsync(TimeSpan.FromSeconds(5));
                log.Write(activated ? "secondary.activation.sent" : "secondary.activation.failed");
                if (!activated)
                    MessageBox.Show(Text.Get("App.AlreadyRunning"),
                        "RepoTracker", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(activated ? 0 : 2);
                return;
            }

            var registry = new RepositoryRegistry(new JsonConfigurationStore(profile), new GitRepositoryLocator(new GitProcess()), new FolderLauncher());
            await registry.InitializeAsync();
            var displayCulture = Text.SelectLanguage(registry.State.Preferences.Language, systemDisplayCulture);
            CultureInfo.DefaultThreadCurrentUICulture = displayCulture;
            CultureInfo.CurrentUICulture = displayCulture;
            scheduler = new LocalStatusScheduler(new GitStatusChecker(new GitProcess()), new RepositoryWatchFactory());
            remoteScheduler = new RemoteScheduler(new GitRemoteChecker(new GitProcess(timeout: TimeSpan.FromSeconds(60), remote: true),
                new GitRemoteReader(new GitProcess())), scheduler.Refresh);
            var startup = StartupRegistrationFactory?.Invoke(profile) ?? new StartupRegistration(new RegistryStartupValueStore(),
                Environment.ProcessPath ?? throw new IOException("Application path unavailable."), profile);
            var window = new MainWindow(log, registry, scheduler, remoteScheduler, startup);
            MainWindow = window;
            if (EnableTrayIcon)
            {
                installation = InstallationSession.Create(profile, log);
                // Do not discard an open editor or interrupt an in-flight configuration save.
                SessionEnding += (_, args) => args.Cancel = !window.CanUseTrayCommands || window.Model.IsBusy;
            }
            instance.Listen(() => Dispatcher.BeginInvoke(() =>
            {
                log.Write("primary.activation.received");
                window.RestorePanel();
            }));
            if (options.Minimized)
            {
                // Create the native handle and icon without ever showing a startup window.
                window.ShowActivated = false;
                new WindowInteropHelper(window).EnsureHandle();
            }
            else window.Show();
            if (EnableTrayIcon) TrayIcon = new TrayIcon(window, log);
            else log.Write("tray.disabled-by-host");
            log.Write("application.started");
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            log?.Write("application.start.failed:" + error.GetType().Name);
            MessageBox.Show(Text.Get("App.StartFailed"), "RepoTracker",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TrayIcon?.Dispose();
        remoteScheduler?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        scheduler?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        instance?.Dispose();
        installation?.Dispose();
        log?.Write("application.stopped");
        base.OnExit(e);
    }
}
