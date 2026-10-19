using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RepoTracker.Core.Sources;
using RepoTracker.Core.Status;
using RepoTracker.Core.Tracking;
using RepoTracker.Infrastructure.Runtime;

namespace RepoTracker.Desktop.TestHost;

// Real production windows with in-memory sample data. No Git, tray, startup registration,
// real profile, filesystem watcher or network adapter is used by this documentation mode.
internal static class ReadmeScreenshots
{
    public static int Run(string output, string language)
    {
        if (language is not ("en" or "zh-Hans")) throw new ArgumentException("Use en or zh-Hans.");
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var name in new[] { "Palette.Dark", "Controls", "SourceIcons" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/RepoTracker.Desktop;component/Themes/{name}.xaml", UriKind.Relative)
            });
        app.Resources["UiScaleTransform"] = new ScaleTransform(1, 1);
        app.DispatcherUnhandledException += (_, e) =>
        {
            File.WriteAllText(Path.Combine(output, "error.txt"), e.Exception.ToString());
            e.Handled = true;
            app.Shutdown(1);
        };
        app.Startup += async (_, _) =>
        {
            try { await Capture(app, output, language); app.Shutdown(); }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString());
                app.Shutdown(1);
            }
        };
        return app.Run();
    }

    private static async Task Capture(Application app, string output, string language)
    {
        var work = Guid.NewGuid();
        var personal = Guid.NewGuid();
        TrackedRepository Repo(string path, string name, Guid? group) => new(Guid.NewGuid(),
            path, path + @"\.git", path + @"\.git", name, group, DateTimeOffset.UnixEpoch);
        var state = new TrackerState
        {
            Groups = [new(work, "Workspace", 0), new(personal, "Personal", 1)],
            Repositories =
            [
                Repo(@"E:\Projects\atlas", "atlas", work),
                Repo(@"E:\Projects\atlas\packages\api", "api", work),
                Repo(@"E:\Projects\atlas\packages\web", "web", work),
                Repo(@"E:\Projects\design-system", "design-system", work),
                Repo(@"E:\Projects\dotfiles", "dotfiles", personal),
                Repo(@"E:\Projects\notes", "notes", null)
            ],
            Preferences = new() { Language = language, KeepExpanded = true }
        };
        var registry = new RepositoryRegistry(new MemoryStore(state), new NoLocator(), new NoFolders());
        await using var local = new LocalStatusScheduler(new SampleStatus(), new NoWatchers());
        await using var remote = new RemoteScheduler(new NoRemote(), local.Refresh);
        var panel = new MainWindow(new SessionLog(output), registry, local, remote, new NoStartup());
        app.MainWindow = panel;
        panel.Show();
        await panel.Initialization;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (panel.Model.Groups.SelectMany(group => group.Repositories).Any(row => row.Status.Data is null))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Sample statuses did not render.");
            await Task.Delay(50);
        }
        await Save(panel, output, $"main-{language}.png");
        await registry.UpdatePreferencesAsync(p => p with { Theme = AppTheme.Light });
        await Save(panel, output, $"main-light-{language}.png");
        await registry.UpdatePreferencesAsync(p => p with { Theme = AppTheme.Dark });

        // Open the actual Add command, fill a fictional path, then cancel without adding it.
        var addCapture = app.Dispatcher.InvokeAsync(async () =>
        {
            var entry = app.Windows.OfType<EntryWindow>().Single();
            try
            {
                var input = (TextBox)entry.FindName("InputBox");
                input.Text = @"E:\Projects\new-project";
                input.Select(input.Text.Length, 0);
                await Save(entry, output, $"add-repository-{language}.png");
            }
            finally { entry.Close(); }
        }, DispatcherPriority.ApplicationIdle).Task;
        panel.ShowAddRepository();
        await await addCapture;

        var settingsCapture = app.Dispatcher.InvokeAsync(async () =>
        {
            var settings = app.Windows.OfType<SettingsWindow>().Single();
            try
            {
                ((ScrollViewer)settings.FindName("CheckScroll")).ScrollToTop();
                await Save(settings, output, $"settings-{language}.png");
            }
            finally { settings.Close(); }
        }, DispatcherPriority.ApplicationIdle).Task;
        panel.ShowSettings();
        await await settingsCapture;
        File.WriteAllText(Path.Combine(output, "capture.txt"),
            $"Production WPF windows; language={language}; 6 fictional repositories; remote checks off; 4 PNG files.\n");
        panel.Close();
    }

    private static async Task Save(Window window, string output, string fileName)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        var height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        if (width < 100 || height < 100) throw new InvalidOperationException("Window has no rendered content.");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(content), null,
                new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, fileName));
        encoder.Save(file);
    }

    private sealed class MemoryStore(TrackerState state) : IConfigurationStore
    {
        private int revision;
        public Task<ConfigurationSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConfigurationSnapshot(state, revision.ToString()));
        public Task<string> SaveAsync(TrackerState updated, string expectedRevision, CancellationToken cancellationToken = default)
        {
            state = updated;
            return Task.FromResult((++revision).ToString());
        }
    }
    private sealed class SampleStatus : ILocalStatusChecker
    {
        public Task<StatusResult> CheckAsync(TrackedRepository repo, CancellationToken cancellationToken)
        {
            var (branch, ahead, behind, changed) = repo.DisplayName switch
            {
                "api" => ("feature/auth", 2, 0, 1),
                "web" => ("main", 0, 3, 0),
                "design-system" => ("main", 0, 0, 1),
                _ => ("main", 0, 0, 0)
            };
            var hasRemote = repo.DisplayName != "notes";
            return Task.FromResult(new StatusResult(new GitStatus(HeadKind.Branch, branch, null,
                hasRemote ? "origin/" + branch : null, hasRemote ? ahead : null, hasRemote ? behind : null,
                0, changed, 0, 0), Remote: new RemoteIdentity(hasRemote,
                    hasRemote ? repo.DisplayName == "design-system" ? "gitlab.com" : "github.com" : null)));
        }
    }
    private sealed class NoWatchers : IRepositoryWatchFactory
    {
        public IRepositoryWatch Watch(TrackedRepository repository, Action changed) => new NoWatch();
        private sealed class NoWatch : IRepositoryWatch { public bool IsActive => false; public void Dispose() { } }
    }
    private sealed class NoLocator : IRepositoryLocator
    {
        public Task<RepositoryLocation> LocateAsync(string path, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Screenshot mode never opens repositories.");
    }
    private sealed class NoFolders : IFolderLauncher
    {
        public void Open(string path) => throw new InvalidOperationException("Screenshot mode never opens folders.");
    }
    private sealed class NoRemote : IRemoteChecker
    {
        public Task<RemoteResult> CheckAsync(TrackedRepository repository, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Screenshot mode never connects to a remote.");
    }
    private sealed class NoStartup : IStartupRegistration
    {
        public bool IsEnabled => false;
        public void SetEnabled(bool enabled) => throw new InvalidOperationException("Screenshot mode never registers login startup.");
    }
}
