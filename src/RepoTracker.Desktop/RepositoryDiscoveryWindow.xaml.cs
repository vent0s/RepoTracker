using System.Windows;
using RepoTracker.Core.Localization;
using RepoTracker.Core.Tracking;
using RepoTracker.Desktop.ViewModels;

namespace RepoTracker.Desktop;

public partial class RepositoryDiscoveryWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly RepositoryRegistry registry;
    private Guid? groupId;
    private bool busy = true;
    private IReadOnlyList<DiscoveryChoice> choices = [];
    public IReadOnlyList<AddRepositoryResult> Imported { get; private set; } = [];

    public RepositoryDiscoveryWindow(IRepositoryScanner scanner, RepositoryRegistry registry, string path, Guid? groupId, bool completeSingle = false)
    {
        this.registry = registry;
        this.groupId = groupId;
        InitializeComponent();
        ThemeManager.PrepareDialog(this);
        Summary.Text = Text.Get("Repository.Scanning");
        Closed += (_, _) => lifetime.Cancel();
        Loaded += async (_, _) =>
        {
            var scanning = true;
            var progress = new Progress<RepositoryScanProgress>(value =>
            {
                if (scanning && IsVisible) Summary.Text = Text.Format("Repository.ScanProgress", value.VisitedDirectories, value.Repositories);
            });
            try
            {
                var result = await scanner.ScanAsync(path, lifetime.Token, progress);
                scanning = false;
                lifetime.Token.ThrowIfCancellationRequested();
                var root = result.Repositories[0].WorktreeRoot;
                this.groupId ??= registry.State.Repositories.FirstOrDefault(repo =>
                    string.Equals(repo.WorktreeRoot, root, StringComparison.OrdinalIgnoreCase))?.GroupId;
                if (completeSingle && result.Repositories.Count == 1 && result.Issues.Count == 0 && !result.LimitReached && result.SkippedLinks == 0)
                {
                    Imported = await registry.AddManyAsync([root], this.groupId, lifetime.Token);
                    if (IsVisible) DialogResult = true;
                    return;
                }
                choices = result.Repositories.OrderBy(repo => repo.WorktreeRoot == root ? "" : repo.WorktreeRoot, StringComparer.OrdinalIgnoreCase)
                    .Select(repo => new DiscoveryChoice(repo.WorktreeRoot, root,
                        registry.State.Repositories.Any(existing => string.Equals(existing.WorktreeRoot, repo.WorktreeRoot, StringComparison.OrdinalIgnoreCase))))
                    .ToArray();
                Results.ItemsSource = choices;
                Summary.Text = Text.Format("Repository.ScanSummary", choices.Count, result.VisitedDirectories);
                var warnings = new List<string>();
                if (result.TimeLimitReached) warnings.Add(Text.Get("Repository.ScanTimeLimit"));
                else if (result.LimitReached) warnings.Add(Text.Get("Repository.ScanLimited"));
                if (result.SkippedLinks > 0) warnings.Add(Text.Format("Repository.ScanLinks", result.SkippedLinks));
                if (result.Issues.Count > 0)
                {
                    warnings.Add(Text.Format("Repository.ScanIssues", result.Issues.Count));
                    warnings.AddRange(result.Issues.Take(20).Select(issue => issue.Path + "\n" + issue.Message));
                }
                Warnings.Text = string.Join("\n\n", warnings);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) when (ShellViewModel.IsUserFacing(error))
            {
                Summary.Text = Text.Get("Repository.ScanFailed");
                ErrorText.Text = ShellViewModel.Message(error);
            }
            finally { scanning = false; busy = false; UpdateSelection(); }
        };
    }

    private void SelectionChanged(object sender, RoutedEventArgs e) => UpdateSelection();
    private void UpdateSelection()
    {
        var count = choices.Count(choice => choice.IsSelected);
        SelectionSummary.Text = Text.Format("Repository.ScanSelected", count);
        ImportButton.IsEnabled = !busy && count > 0;
    }

    private async void Import(object sender, RoutedEventArgs e)
    {
        busy = true; Results.IsEnabled = false; UpdateSelection(); ErrorText.Text = "";
        try
        {
            Imported = await registry.AddManyAsync(choices.Where(choice => choice.IsSelected).Select(choice => choice.Path), groupId, lifetime.Token);
            if (IsVisible) DialogResult = true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (ShellViewModel.IsUserFacing(error)) { ErrorText.Text = ShellViewModel.Message(error); }
        finally { busy = false; Results.IsEnabled = true; UpdateSelection(); }
    }

    private sealed class DiscoveryChoice(string path, string root, bool alreadyTracked)
    {
        public string Path => path;
        public string Label => (path == root ? Text.Get("Repository.ScanRoot") : System.IO.Path.GetRelativePath(root, path))
            + (alreadyTracked ? " · " + Text.Get("Repository.ScanTracked") : "");
        public bool CanSelect => !alreadyTracked;
        public bool IsSelected { get; set; } = !alreadyTracked;
    }
}
