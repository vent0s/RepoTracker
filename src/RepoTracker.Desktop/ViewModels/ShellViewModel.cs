using RepoTracker.Core.Localization;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using RepoTracker.Core.Tracking;
using RepoTracker.Core.Status;
using RepoTracker.Core.Sources;

namespace RepoTracker.Desktop.ViewModels;

public sealed class RepositoryRow(TrackedRepository repository) : ObservableObject
{
    public TrackedRepository Repository { get; set; } = repository;
    private bool highlighted;
    public bool IsHighlighted { get => highlighted; set => Set(ref highlighted, value); }
    public StatusSnapshot Status { get; private set; } = new();
    public SourceMatch Source { get; private set; } = SourceRules.Match(null, []);
    public RemoteSnapshot Remote { get; private set; } = new();
    public bool RemoteEnabled { get; private set; }
    public string SourceBadge => Source.Badge;
    public string SourceIconKey => Source.Marker is null ? Source.Icon.ToString() : "Unknown";
    public string SourceTooltip => Source.Name + "\n" + Source.Explanation;
    public string RemoteText => !RemoteEnabled ? Text.Get("Remote.Off") : Remote.IsChecking ? Text.Get("Remote.Checking") : Remote.Outcome switch
    {
        RemoteOutcome.Never => Text.Get("Remote.Never"), RemoteOutcome.Success => Text.Get("Remote.TimePrefix") + Remote.SucceededAt?.ToLocalTime().ToString("HH:mm:ss"),
        RemoteOutcome.Skipped => Text.Get("Remote.Skipped"), RemoteOutcome.Authentication => Text.Get("Remote.Authentication"), RemoteOutcome.Offline => Text.Get("Remote.Offline"),
        RemoteOutcome.Timeout => Text.Get("Remote.Timeout"), _ => Text.Get("Remote.Failed")
    };
    public string RemoteTooltip => RemoteText + "\n" + (string.IsNullOrEmpty(Remote.Message) ? Text.Get("Remote.Never") : Remote.Message) +
        (Remote.SucceededAt is { } time ? Text.Get("Remote.LastSuccess") + time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : Text.Get("Remote.NoSuccess")) +
        Text.Get("Remote.UpstreamHint");
    public Guid Id => Repository.Id;
    public string Name => Repository.DisplayName;
    public string OpenFolderAccessibleName => Text.Format("Repository.OpenAccessible", Name);
    public string ActionsAccessibleName => Text.Format("Repository.ActionsAccessible", Name);
    public string Path => Repository.WorktreeRoot;
    public string BranchText => Status.Data?.HeadKind switch
    {
        HeadKind.Detached => Text.Get("Status.Detached"), HeadKind.Unborn => Status.Data.Branch ?? Text.Get("Status.NewRepository"),
        HeadKind.Branch => Status.Data.Branch ?? "—", _ => "—"
    };
    public string BranchDetail => Status.Data?.HeadKind switch
    {
        HeadKind.Detached => Status.Data.Commit?[..Math.Min(7, Status.Data.Commit.Length)] ?? "",
        HeadKind.Unborn => Text.Get("Status.Unborn"), _ => ""
    };
    public string StatusText => Status.Error != StatusError.None ? Status.Error switch
    {
        StatusError.DirectoryUnavailable => Text.Get("Status.Unreachable"), StatusError.NotRepository => Text.Get("Status.NotRepository"),
        StatusError.GitUnavailable => Text.Get("Status.GitMissing"), StatusError.Timeout => Text.Get("Status.Timeout"),
        StatusError.AccessDenied => Text.Get("Status.AccessDenied"), StatusError.InvalidOutput => Text.Get("Status.ParseFailed"), _ => Text.Get("Status.Failed")
    } : Status.Data is not { } data ? Status.IsChecking ? Text.Get("Status.Checking") : Text.Get("Status.Pending")
        : data.Conflicts > 0 ? Text.Format("Status.Conflicts", data.Conflicts) : data.IsDirty ? Text.Get("Status.Dirty") : Text.Get("Status.Clean");
    public string ComparisonText => Status.Error != StatusError.None ? Text.Get("Status.Retry") : Status.Data is not { } data ? Text.Get("Status.NotRead")
        : data.HeadKind == HeadKind.Unborn ? Text.Get("Status.NoCommits") : data.Upstream is null ? Text.Get("Status.NoUpstream")
        : data.Ahead is null || data.Behind is null ? Text.Get("Status.UnknownCounts")
        : data.Ahead == 0 && data.Behind == 0 ? Text.Get("Status.InSync") : $"↑{data.Ahead}  ↓{data.Behind}";
    public string FreshnessText => Status.SucceededAt is { } time
        ? (Status.Error == StatusError.None ? Text.Get("Status.LocalPrefix") : Text.Get("Status.LastSuccessPrefix")) + time.ToLocalTime().ToString("HH:mm:ss") + (Status.IsChecking ? Text.Get("Status.CheckingSuffix") : "")
        : Status.CheckedAt is { } attempted ? Text.Get("Status.AttemptPrefix") + attempted.ToLocalTime().ToString("HH:mm:ss") : "";
    public string StatusTone => Status.Error != StatusError.None || Status.Data?.Conflicts > 0 ? "Error"
        : Status.Data is null ? "Muted" : Status.Data.IsDirty ? "Warning" : "Success";
    public string StatusTooltip => (Status.Message is { } message ? message + "\n" : "") +
        (Status.Data is { } data ? (Status.Error != StatusError.None ? Text.Get("Status.PreviousResult") : "") + Text.Format("Status.CountDetails", data.Staged, data.Unstaged, data.Untracked, data.Conflicts) +
            Text.Format("Status.BranchDetails", BranchText, BranchDetail, data.Upstream ?? Text.Get("Status.None")) : "") +
        FreshnessText + "\n" + RemoteTooltip + (Status.Watching ? "" : Text.Get("Status.WatchHint"));

    public void ApplyRemote(RemoteSnapshot remote, bool enabled, IEnumerable<SourceRule> rules)
    {
        Source = SourceRules.Match(Status.Remote, rules); Remote = remote; RemoteEnabled = enabled;
        foreach (var property in new[] { nameof(SourceBadge), nameof(SourceIconKey), nameof(SourceTooltip), nameof(RemoteText), nameof(RemoteTooltip), nameof(StatusTooltip) }) Notify(property);
    }

    public void Apply(StatusSnapshot status)
    {
        if (status == Status) return;
        Status = status;
        foreach (var property in new[] { nameof(Status), nameof(BranchText), nameof(BranchDetail), nameof(StatusText), nameof(ComparisonText), nameof(FreshnessText), nameof(StatusTone), nameof(StatusTooltip) }) Notify(property);
    }
}
public sealed record GroupRow(Guid? Id, string Name, bool IsExpanded, IReadOnlyList<RepositoryRow> Repositories)
{
    public bool CanManage => Id is not null;
}
public sealed record GroupChoice(Guid? Id, string Name);

public sealed class ShellViewModel : ObservableObject
{
    private bool keepExpanded;
    private bool busy;
    private bool managingGroups;
    private string notice = Text.Get("Repository.Loading");
    private IReadOnlyList<GroupRow> groups = [];
    private Guid? highlightedId;
    private readonly Dictionary<Guid, RepositoryRow> rows = [];
    private readonly LocalStatusScheduler scheduler;
    private readonly RemoteScheduler remoteScheduler;
    public RepositoryRegistry Registry { get; }
    public bool KeepExpanded { get => keepExpanded; private set => Set(ref keepExpanded, value); }
    public Task SetKeepExpandedAsync(bool value) => ExecuteAsync(() => Registry.UpdatePreferencesAsync(p => p with { KeepExpanded = value }), Text.Get("Window.KeepSaved"));
    public bool ManagingGroups { get => managingGroups; set => Set(ref managingGroups, value); }
    public string Notice { get => notice; set => Set(ref notice, value); }
    public bool IsBusy { get => busy; private set { Set(ref busy, value); Notify(nameof(CanEdit)); Notify(nameof(CanUndo)); CommandManager.InvalidateRequerySuggested(); } }
    public bool CanEdit => Registry.CanWrite && !IsBusy;
    public bool CanUndo => Registry.CanUndo && !IsBusy;
    public int RepositoryCount => Registry.State.Repositories.Length;
    public bool RemoteEnabled => Registry.State.RemoteEnabled;
    public string RemoteSummary => RemoteEnabled ? Text.Format("Remote.EnabledSummary", Registry.State.Preferences.RemoteIntervalMinutes) : Text.Get("Remote.DisabledSummary");
    public string LocalCheckSummary
    {
        get
        {
            var latest = rows.Values.Select(row => row.Status.CheckedAt).Max();
            var checking = rows.Values.Count(row => row.Status.IsChecking);
            return Text.Format("Status.RepositoryCount", RepositoryCount) + (checking > 0 ? Text.Format("Status.CheckingCount", checking) : latest is { } time ? Text.Get("Status.LocalPrefix") + time.ToLocalTime().ToString("HH:mm:ss") : Text.Get("Status.Waiting"));
        }
    }
    public IReadOnlyList<GroupRow> Groups { get => groups; private set => Set(ref groups, value); }
    public IReadOnlyList<GroupChoice> GroupChoices => Registry.State.Groups.Select(group => new GroupChoice(group.Id, group.Name))
        .Append(new(null, Text.Get("Group.Ungrouped"))).ToArray();

    public ShellViewModel(RepositoryRegistry registry, LocalStatusScheduler scheduler, RemoteScheduler remoteScheduler)
    {
        Registry = registry;
        this.scheduler = scheduler;
        this.remoteScheduler = remoteScheduler;
        Registry.Changed += (_, _) => ApplyRegistryState();
    }

    private void ApplyRegistryState()
    {
        var preferences = Registry.State.Preferences;
        scheduler.SetSchedule(TimeSpan.FromSeconds(preferences.LocalIntervalSeconds), preferences.LocalConcurrency);
        remoteScheduler.SetInterval(TimeSpan.FromMinutes(preferences.RemoteIntervalMinutes));
        scheduler.SetRepositories(Registry.State.Repositories); remoteScheduler.Configure(Registry.State.Repositories, Registry.State.RemoteEnabled);
        KeepExpanded = preferences.KeepExpanded;
        Refresh();
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            await Registry.InitializeAsync();
            ApplyRegistryState();
            Notice = Registry.Notice ?? Text.Format("Status.AutoSave", Registry.State.Preferences.LocalIntervalSeconds);
        }
        finally { IsBusy = false; }
    }

    public void Highlight(Guid id) { highlightedId = id; Refresh(); }

    public void Refresh()
    {
        var state = Registry.State;
        foreach (var id in rows.Keys.Except(state.Repositories.Select(repo => repo.Id)).ToArray()) rows.Remove(id);
        foreach (var repo in state.Repositories)
        {
            if (!rows.TryGetValue(repo.Id, out var row)) rows.Add(repo.Id, row = new(repo));
            row.Repository = repo; row.IsHighlighted = repo.Id == highlightedId;
        }
        Groups = state.Groups.Select(group => new GroupRow(group.Id, group.Name, !group.Collapsed,
                state.Repositories.Where(repo => repo.GroupId == group.Id).Select(repo => rows[repo.Id]).ToArray()))
            .Append(new(null, Text.Get("Group.Ungrouped"), !state.UngroupedCollapsed,
                state.Repositories.Where(repo => repo.GroupId is null).Select(repo => rows[repo.Id]).ToArray())).ToArray();
        RefreshStatuses();
        Notify(nameof(RepositoryCount)); Notify(nameof(GroupChoices)); Notify(nameof(CanEdit)); Notify(nameof(CanUndo));
        CommandManager.InvalidateRequerySuggested();
    }

    public void RefreshStatuses()
    {
        var statuses = scheduler.Snapshot;
        var remoteStatuses = remoteScheduler.Snapshot;
        foreach (var row in rows.Values)
        {
            row.Apply(statuses.GetValueOrDefault(row.Id) ?? new());
            row.ApplyRemote(remoteStatuses.GetValueOrDefault(row.Id) ?? new(), RemoteEnabled, Registry.State.Sources);
        }
        Notify(nameof(LocalCheckSummary));
        Notify(nameof(RemoteSummary)); Notify(nameof(RemoteEnabled));
    }

    public async Task ExecuteAsync(Func<Task> action, string success)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); Notice = success; }
        catch (OperationCanceledException) { }
        catch (Exception error) when (IsUserFacing(error)) { Notice = Message(error); Refresh(); }
        finally { IsBusy = false; }
    }

    public static bool IsUserFacing(Exception error) => error is TrackerException or IOException or UnauthorizedAccessException or Win32Exception or ArgumentException;
    public static string Message(Exception error) => error is TrackerException ? error.Message
        : Text.Get("Action.Failed");
}
