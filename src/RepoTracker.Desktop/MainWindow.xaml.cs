using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using RepoTracker.Core.Windows;
using RepoTracker.Core.Tracking;
using RepoTracker.Core.Status;
using RepoTracker.Desktop.ViewModels;
using RepoTracker.Desktop.Windows;
using RepoTracker.Infrastructure.Runtime;

namespace RepoTracker.Desktop;

public partial class MainWindow : Window
{
    private readonly SessionLog log;
    private readonly AutoCollapseController collapse;
    private IDisposable? menuSuspension;
    private HwndSource? source;
    private PixelRect? lastPlacement;
    private bool placing;
    private readonly CancellationTokenSource lifetime = new();
    public ShellViewModel Model { get; }
    public Task Initialization { get; }
    public LocalStatusScheduler StatusScheduler { get; }
    public RemoteScheduler RemoteScheduler { get; }
    public IStartupRegistration StartupRegistration { get; }
    public RelayCommand RefreshCommand { get; }
    private int statusRefreshPending;
    private bool closing, shutdownReady;
    public RelayCommand ToggleKeepCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand CollapseCommand { get; }
    public RelayCommand NewGroupCommand { get; }
    public RelayCommand ManageGroupsCommand { get; }
    public RelayCommand UndoCommand { get; }
    public bool IsAutoCollapseSuspended => collapse.IsSuspended;

    public MainWindow(SessionLog log, RepositoryRegistry registry, LocalStatusScheduler scheduler, RemoteScheduler remoteScheduler, IStartupRegistration startupRegistration)
    {
        this.log = log;
        StatusScheduler = scheduler;
        RemoteScheduler = remoteScheduler;
        StartupRegistration = startupRegistration;
        Model = new ShellViewModel(registry, scheduler, remoteScheduler);
        RefreshCommand = new RelayCommand(scheduler.Refresh, () => !closing && !collapse!.IsSuspended);
        collapse = new AutoCollapseController(this, () => Model.KeepExpanded);
        ToggleKeepCommand = new RelayCommand(async () => await Model.SetKeepExpandedAsync(!Model.KeepExpanded), () => Model.CanEdit);
        SettingsCommand = new RelayCommand(ShowSettings, () => !collapse.IsSuspended);
        ChooseFolderCommand = new RelayCommand(ShowAddRepository, () => !collapse.IsSuspended && Model.CanEdit);
        NewGroupCommand = new RelayCommand(ShowNewGroup, () => !collapse.IsSuspended && Model.CanEdit);
        ManageGroupsCommand = new RelayCommand(() => Model.ManagingGroups = !Model.ManagingGroups, () => Model.CanEdit);
        UndoCommand = new RelayCommand(async () => await Model.ExecuteAsync(() => registry.UndoRemoveAsync(lifetime.Token), "已恢复仓库跟踪。"), () => Model.CanUndo);
        CollapseCommand = new RelayCommand(() => WindowState = WindowState.Minimized, () => !collapse.IsSuspended);
        InitializeComponent();
        scheduler.Changed += StatusChanged;
        remoteScheduler.Changed += StatusChanged;
        Closing += StopBeforeClose;
        DataContext = Model;
        InputBindings.Add(new KeyBinding(CollapseCommand, Key.Escape, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(SettingsCommand, Key.OemComma, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ChooseFolderCommand, Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ToggleKeepCommand, Key.P, ModifierKeys.Control | ModifierKeys.Shift));
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Model.KeepExpanded))
            {
                log.Write("window.keep-expanded:" + Model.KeepExpanded);
                collapse.Schedule();
            }
        };
        registry.Changed += (_, _) =>
        {
            ThemeManager.Apply(registry.State.Preferences);
            System.Windows.Shell.WindowChrome.GetWindowChrome(this).CaptionHeight = 76 * registry.State.Preferences.ScalePercent / 100.0;
            if (IsLoaded) ClampPosition();
        };
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(WindowMessage);
            Reposition(NativeWindows.GetForegroundWindow(), null);
        };
        StateChanged += (_, _) =>
        {
            log.Write("window.state:" + WindowState);
            if (WindowState == WindowState.Normal) ClampPosition();
        };
        LocationChanged += (_, _) =>
        {
            if (!placing && IsLoaded && WindowState == WindowState.Normal)
                lastPlacement = NativeWindows.Bounds(this);
        };
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            menuSuspension?.Dispose();
            collapse.Dispose();
            source?.RemoveHook(WindowMessage);
            log.Write("window.closed");
        };
        Initialization = Model.InitializeAsync();
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0218)
        {
            if (wParam == 4) { StatusScheduler.Suspend(); RemoteScheduler.Suspend(); }
            else if (wParam is 7 or 18) { StatusScheduler.Resume(); RemoteScheduler.Resume(); }
        }
        if (message is 0x02E0 or 0x007E or 0x001A) // DPI, display layout, work area change
            Dispatcher.BeginInvoke(ClampPosition);
        return 0;
    }

    private void StatusChanged(object? sender, EventArgs e)
    {
        if (closing || Interlocked.Exchange(ref statusRefreshPending, 1) != 0) return;
        Dispatcher.BeginInvoke(() => { Interlocked.Exchange(ref statusRefreshPending, 0); if (!closing) Model.RefreshStatuses(); });
    }

    private async void StopBeforeClose(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (shutdownReady) return;
        e.Cancel = true;
        if (closing) return;
        closing = true; IsEnabled = false; lifetime.Cancel();
        StatusScheduler.Changed -= StatusChanged;
        RemoteScheduler.Changed -= StatusChanged;
        await RemoteScheduler.DisposeAsync();
        await StatusScheduler.DisposeAsync();
        log.Write("status.stopped");
        shutdownReady = true;
        Close();
    }

    private void Reposition(nint monitorWindow, PixelRect? previous)
    {
        if (placing) return;
        placing = true;
        try { lastPlacement = NativeWindows.Place(this, monitorWindow, previous, Model.Registry.State.Preferences.ScalePercent / 100.0); }
        finally { placing = false; }
    }

    public void ClampPosition()
    {
        if (WindowState == WindowState.Normal && source is not null)
            Reposition(source.Handle, lastPlacement);
    }

    public void RestorePanel()
    {
        WindowState = WindowState.Normal;
        ClampPosition();
        NativeWindows.Activate(this);
        log.Write("window.restored");
    }

    public void ShowSettings()
    {
        var focus = Keyboard.FocusedElement;
        using var suspension = collapse.Suspend();
        log.Write("settings.opened");
        new SettingsWindow { Owner = this, DataContext = Model }.ShowDialog();
        RestoreFocusAfterDialog(focus);
        log.Write("settings.closed");
    }

    public string? ChooseFolder(Window? owner = null)
    {
        using var suspension = collapse.Suspend();
        log.Write("folder-picker.opened");
        var picker = new OpenFolderDialog { Title = "选择 Git 仓库目录", Multiselect = false };
        var accepted = picker.ShowDialog(owner ?? this) == true;
        log.Write("folder-picker.closed");
        return accepted ? picker.FolderName : null;
    }

    public void ShowNewGroup() => ShowEntry(new("新建分组", "按用途组织仓库。名称需为 1–30 个字符，不能重复。", "分组名称", "", null, null,
        "创建", async (name, _, token) => { await Model.Registry.CreateGroupAsync(name, token); Model.Notice = "已创建分组。"; }));

    public void ShowAddRepository()
    {
        AddRepositoryResult? added = null;
        ShowEntry(new("添加仓库", "选择仓库目录或其中的子目录。只记录工作树位置，不移动或修改仓库文件。", "仓库目录", "",
            Model.GroupChoices, null, "添加", async (path, groupId, token) =>
            {
                added = await Model.Registry.AddAsync(path, groupId, token);
                Model.Notice = added.AlreadyTracked ? "该工作树已在跟踪列表中，已定位到对应仓库。"
                    : added.GroupFellBack ? "仓库已添加；原分组已删除，已放入未分组。" : "仓库已添加，正在后台检查本地状态。";
            }, ChooseFolder));
        if (added is not null) _ = RevealRepositoryAsync(added.Repository);
    }

    private async Task RevealRepositoryAsync(TrackedRepository repo)
    {
        await Model.ExecuteAsync(async () =>
        {
            var group = Model.Groups.First(item => item.Id == repo.GroupId);
            if (!group.IsExpanded) await Model.Registry.SetCollapsedAsync(repo.GroupId, false, lifetime.Token);
            Model.Highlight(repo.Id);
        }, Model.Notice);
        await Dispatcher.InvokeAsync(() => { BringRepositoryIntoView(this, repo.Id); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static bool BringRepositoryIntoView(DependencyObject parent, Guid id)
    {
        if (parent is Border { DataContext: RepositoryRow row } border && row.Id == id) { border.BringIntoView(); return true; }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (BringRepositoryIntoView(VisualTreeHelper.GetChild(parent, index), id)) return true;
        return false;
    }

    private void ShowEntry(EntryWindow dialog)
    {
        var focus = Keyboard.FocusedElement;
        using var suspension = collapse.Suspend();
        dialog.Owner = this;
        dialog.ShowDialog();
        RestoreFocusAfterDialog(focus);
    }

    private void RestoreFocusAfterDialog(IInputElement? previous)
    {
        // Owner activation and data-template regeneration finish after the modal frame unwinds.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
        {
            if (!IsVisible || WindowState != WindowState.Normal || closing) return;
            FrameworkElement? target = previous as FrameworkElement;
            if (target is not { IsLoaded: true, IsVisible: true, IsEnabled: true })
            {
                var id = target is null ? "" : System.Windows.Automation.AutomationProperties.GetAutomationId(target);
                var context = target?.DataContext;
                target = FindFocusableReplacement(this, id, context) ?? KeepButton;
            }
            target.Focus();
        });
    }
    private static FrameworkElement? FindFocusableReplacement(DependencyObject root, string id, object? context)
    {
        if (root is FrameworkElement { Focusable: true, IsVisible: true, IsEnabled: true } element && id.Length > 0 &&
            System.Windows.Automation.AutomationProperties.GetAutomationId(element) == id &&
            (context is GroupRow group ? element.DataContext is GroupRow currentGroup && currentGroup.Id == group.Id :
             context is RepositoryRow row ? element.DataContext is RepositoryRow currentRow && currentRow.Id == row.Id : true)) return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindFocusableReplacement(VisualTreeHelper.GetChild(root, i), id, context) is { } found) return found;
        return null;
    }

    private void RenameGroup(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GroupRow group } || !group.CanManage) return;
        ShowEntry(new("重命名分组", "改名会保留分组内的所有仓库和顺序。", "分组名称", group.Name, null, null,
            "保存", async (name, _, token) => { await Model.Registry.RenameGroupAsync(group.Id, name, token); Model.Notice = "分组已改名。"; }));
    }

    private void DeleteGroup(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GroupRow group } || !group.CanManage) return;
        ShowEntry(new("删除分组", $"删除“{group.Name}”后，{group.Repositories.Count} 个仓库会移回未分组并继续跟踪。不会删除仓库文件。",
            null, "", null, null, "删除分组", async (_, _, token) =>
            { await Model.Registry.DeleteGroupAsync(group.Id, token); Model.Notice = "分组已删除，成员已移回未分组。"; }));
    }

    private async void GroupExpansionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: GroupRow group } expander || !IsLoaded || group.IsExpanded == expander.IsExpanded) return;
        if (!Model.CanEdit) { expander.IsExpanded = group.IsExpanded; return; }
        await Model.ExecuteAsync(() => Model.Registry.SetCollapsedAsync(group.Id, !expander.IsExpanded, lifetime.Token), "分组展开状态已保存。");
    }

    private async void OpenRepositoryFolder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RepositoryRow row })
            await Model.ExecuteAsync(() => { Model.Registry.OpenFolder(row.Id); return Task.CompletedTask; }, "已打开仓库目录。");
    }

    private void RepositoryMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RepositoryRow row } button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        menu.Opened += MenuOpened;
        menu.Closed += MenuClosed;
        var move = new MenuItem { Header = "移动到分组", IsEnabled = Model.CanEdit };
        foreach (var group in Model.GroupChoices)
        {
            var item = new MenuItem { Header = group.Name, IsCheckable = true, IsChecked = row.Repository.GroupId == group.Id };
            item.Click += async (_, _) => await Model.ExecuteAsync(() => Model.Registry.MoveAsync(row.Id, group.Id, lifetime.Token), "仓库分组已更新。");
            move.Items.Add(item);
        }
        menu.Items.Add(move);
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "移除跟踪…", IsEnabled = Model.CanEdit };
        remove.Click += (_, _) => Dispatcher.BeginInvoke(() => ShowEntry(new("移除仓库跟踪", $"仅从列表移除“{row.Name}”，不会删除本地文件。移除后可撤销最近一次操作。",
            null, "", null, null, "移除跟踪", async (_, _, token) =>
            { await Model.Registry.RemoveAsync(row.Id, token); Model.Notice = "已移除跟踪，可点击“撤销移除”恢复。"; })));
        menu.Items.Add(remove);
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void OpenMenu(object sender, RoutedEventArgs e)
    {
        AppMenu.PlacementTarget = MenuButton;
        AppMenu.IsOpen = true;
    }
    private void MenuOpened(object sender, RoutedEventArgs e) => menuSuspension ??= collapse.Suspend();
    private void MenuClosed(object sender, RoutedEventArgs e) { menuSuspension?.Dispose(); menuSuspension = null; }
    private void OpenSettingsFromMenu(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(ShowSettings);
    private void CollapseFromMenu(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ExitFromMenu(object sender, RoutedEventArgs e) => Close();
}
