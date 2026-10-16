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
    public string RemoteText => !RemoteEnabled ? "远程已关闭" : Remote.IsChecking ? "远程检查中…" : Remote.Outcome switch
    {
        RemoteOutcome.Never => "尚未远程检查", RemoteOutcome.Success => "远程 " + Remote.SucceededAt?.ToLocalTime().ToString("HH:mm:ss"),
        RemoteOutcome.Skipped => "远程已跳过", RemoteOutcome.Authentication => "远程认证失败", RemoteOutcome.Offline => "远程离线",
        RemoteOutcome.Timeout => "远程超时", _ => "远程检查失败"
    };
    public string RemoteTooltip => RemoteText + "\n" + Remote.Message +
        (Remote.SucceededAt is { } time ? "\n上次远程成功：" + time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "\n本次运行尚未远程成功。") +
        "\n来源 remote 与当前分支上游可能不同；↑↓ 始终根据本地已知上游计算。";
    public Guid Id => Repository.Id;
    public string Name => Repository.DisplayName;
    public string Path => Repository.WorktreeRoot;
    public string BranchText => Status.Data?.HeadKind switch
    {
        HeadKind.Detached => "分离 HEAD", HeadKind.Unborn => Status.Data.Branch ?? "新仓库",
        HeadKind.Branch => Status.Data.Branch ?? "—", _ => "—"
    };
    public string BranchDetail => Status.Data?.HeadKind switch
    {
        HeadKind.Detached => Status.Data.Commit?[..Math.Min(7, Status.Data.Commit.Length)] ?? "",
        HeadKind.Unborn => "未首次提交", _ => ""
    };
    public string StatusText => Status.Error != StatusError.None ? Status.Error switch
    {
        StatusError.DirectoryUnavailable => "不可达", StatusError.NotRepository => "非仓库",
        StatusError.GitUnavailable => "未找到 Git", StatusError.Timeout => "检查超时",
        StatusError.AccessDenied => "无法访问", StatusError.InvalidOutput => "无法解析状态", _ => "检查失败"
    } : Status.Data is not { } data ? Status.IsChecking ? "检查中…" : "待检查"
        : data.Conflicts > 0 ? $"有冲突 · {data.Conflicts}" : data.IsDirty ? "有修改" : "干净";
    public string ComparisonText => Status.Error != StatusError.None ? "稍后自动重试" : Status.Data is not { } data ? "尚未读取 Git 状态"
        : data.HeadKind == HeadKind.Unborn ? "尚无提交" : data.Upstream is null ? "无上游"
        : data.Ahead is null || data.Behind is null ? "上游计数未知"
        : data.Ahead == 0 && data.Behind == 0 ? "与已知上游一致" : $"↑{data.Ahead}  ↓{data.Behind}";
    public string FreshnessText => Status.SucceededAt is { } time
        ? (Status.Error == StatusError.None ? "本地 " : "上次成功 ") + time.ToLocalTime().ToString("HH:mm:ss") + (Status.IsChecking ? " · 检查中" : "")
        : Status.CheckedAt is { } attempted ? "尝试 " + attempted.ToLocalTime().ToString("HH:mm:ss") : "";
    public string StatusTone => Status.Error != StatusError.None || Status.Data?.Conflicts > 0 ? "Error"
        : Status.Data is null ? "Muted" : Status.Data.IsDirty ? "Warning" : "Success";
    public string StatusTooltip => (Status.Message is { } message ? message + "\n" : "") +
        (Status.Data is { } data ? $"{(Status.Error != StatusError.None ? "上次成功结果：" : "")}暂存 {data.Staged}，未暂存 {data.Unstaged}，未跟踪项 {data.Untracked}，冲突 {data.Conflicts}\n" +
            $"分支：{BranchText} {BranchDetail}\n已知上游：{data.Upstream ?? "无"}\n" : "") +
        FreshnessText + "\n" + RemoteTooltip + (Status.Watching ? "" : "\n文件监听不可用时由定时检查兜底。");

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
    private string notice = "正在加载仓库清单…";
    private IReadOnlyList<GroupRow> groups = [];
    private Guid? highlightedId;
    private readonly Dictionary<Guid, RepositoryRow> rows = [];
    private readonly LocalStatusScheduler scheduler;
    private readonly RemoteScheduler remoteScheduler;
    public RepositoryRegistry Registry { get; }
    public bool KeepExpanded { get => keepExpanded; private set => Set(ref keepExpanded, value); }
    public Task SetKeepExpandedAsync(bool value) => ExecuteAsync(() => Registry.UpdatePreferencesAsync(p => p with { KeepExpanded = value }), "保持展开设置已保存。");
    public bool ManagingGroups { get => managingGroups; set => Set(ref managingGroups, value); }
    public string Notice { get => notice; set => Set(ref notice, value); }
    public bool IsBusy { get => busy; private set { Set(ref busy, value); Notify(nameof(CanEdit)); Notify(nameof(CanUndo)); CommandManager.InvalidateRequerySuggested(); } }
    public bool CanEdit => Registry.CanWrite && !IsBusy;
    public bool CanUndo => Registry.CanUndo && !IsBusy;
    public int RepositoryCount => Registry.State.Repositories.Length;
    public bool RemoteEnabled => Registry.State.RemoteEnabled;
    public string RemoteSummary => RemoteEnabled ? $"远程已开启 · 每 {Registry.State.Preferences.RemoteIntervalMinutes} 分钟" : "远程检查已关闭";
    public string LocalCheckSummary
    {
        get
        {
            var latest = rows.Values.Select(row => row.Status.CheckedAt).Max();
            var checking = rows.Values.Count(row => row.Status.IsChecking);
            return $"{RepositoryCount} 个仓库 · " + (checking > 0 ? $"正在检查 {checking} 个" : latest is { } time ? "本地 " + time.ToLocalTime().ToString("HH:mm:ss") : "等待本地检查");
        }
    }
    public IReadOnlyList<GroupRow> Groups { get => groups; private set => Set(ref groups, value); }
    public IReadOnlyList<GroupChoice> GroupChoices => Registry.State.Groups.Select(group => new GroupChoice(group.Id, group.Name))
        .Append(new(null, GroupNames.Ungrouped)).ToArray();

    public ShellViewModel(RepositoryRegistry registry, LocalStatusScheduler scheduler, RemoteScheduler remoteScheduler)
    {
        Registry = registry;
        this.scheduler = scheduler;
        this.remoteScheduler = remoteScheduler;
        Registry.Changed += (_, _) =>
        {
            var preferences = Registry.State.Preferences;
            scheduler.SetSchedule(TimeSpan.FromSeconds(preferences.LocalIntervalSeconds), preferences.LocalConcurrency);
            remoteScheduler.SetInterval(TimeSpan.FromMinutes(preferences.RemoteIntervalMinutes));
            scheduler.SetRepositories(Registry.State.Repositories); remoteScheduler.Configure(Registry.State.Repositories, Registry.State.RemoteEnabled);
            KeepExpanded = preferences.KeepExpanded;
            Refresh();
        };
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            await Registry.InitializeAsync();
            Notice = Registry.Notice ?? $"仓库与分组自动保存 · 每 {Registry.State.Preferences.LocalIntervalSeconds} 秒检查本地状态";
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
            .Append(new(null, GroupNames.Ungrouped, !state.UngroupedCollapsed,
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
        : "操作未完成，原列表仍保留。请检查目录权限、磁盘空间或文件占用后重试。";
}
