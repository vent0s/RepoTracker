using System.Windows;
using System.Collections.Immutable;
using System.Windows.Controls;
using RepoTracker.Core.Sources;
using RepoTracker.Core.Tracking;
using RepoTracker.Desktop.ViewModels;

namespace RepoTracker.Desktop;

public partial class SettingsWindow : Window
{
    private ShellViewModel Model => (ShellViewModel)DataContext;
    private Guid draftId = Guid.NewGuid();
    private bool saving;
    public SettingsWindow()
    {
        InitializeComponent();
        ThemeManager.PrepareDialog(this);
        SourceIcon.ItemsSource = Enum.GetValues<SourceIcon>(); SourceIcon.SelectedItem = Core.Sources.SourceIcon.Server;
        Loaded += (_, _) =>
        {
            ReloadRules();
            var preferences = Model.Registry.State.Preferences;
            LocalInterval.Text = preferences.LocalIntervalSeconds.ToString(); RemoteInterval.Text = preferences.RemoteIntervalMinutes.ToString();
            LocalConcurrency.Text = preferences.LocalConcurrency.ToString(); ThemeChoice.SelectedIndex = (int)preferences.Theme;
            ScaleChoice.SelectedIndex = Array.IndexOf(new[] { 100, 125, 150 }, preferences.ScalePercent);
            ReadStartup();
        };
        Closing += (_, e) => { if (saving) e.Cancel = true; };
    }
    private void ReloadRules() => RuleList.ItemsSource = Model.Registry.State.Sources;
    private void Finish(object sender, RoutedEventArgs e) => Close();
    private void NewRule(object sender, RoutedEventArgs e)
    {
        RuleList.SelectedItem = null; draftId = Guid.NewGuid(); SourceName.Text = ""; SourcePattern.Text = "";
        SourceIcon.SelectedItem = Core.Sources.SourceIcon.Server; Feedback.Text = "填写来源名称和主机名，再保存。";
    }
    private void SelectRule(object sender, SelectionChangedEventArgs e)
    {
        if (RuleList.SelectedItem is not SourceRule rule) return;
        draftId = rule.Id; SourceName.Text = rule.Name; SourcePattern.Text = rule.Pattern; SourceIcon.SelectedItem = rule.Icon;
    }
    private SourceRule Draft() => SourceRules.Validate(new(draftId, SourceName.Text, SourcePattern.Text,
        (SourceIcon)(SourceIcon.SelectedItem ?? Core.Sources.SourceIcon.Server)), Model.Registry.State.Sources);
    private async Task SaveAsync(Func<Task> action, string success)
    {
        if (saving) return;
        saving = true; Tabs.IsEnabled = FinishButton.IsEnabled = false;
        try { await action(); Feedback.Text = success; }
        catch (Exception error) when (ShellViewModel.IsUserFacing(error)) { Feedback.Text = ShellViewModel.Message(error); }
        finally { saving = false; Tabs.IsEnabled = FinishButton.IsEnabled = true; RemoteSwitch.IsChecked = Model.RemoteEnabled; KeepSwitch.IsChecked = Model.KeepExpanded; }
    }
    private async void SavePreferences(object sender, RoutedEventArgs e) => await SaveAsync(async () =>
    {
        if (!int.TryParse(LocalInterval.Text, out var local) || !int.TryParse(RemoteInterval.Text, out var remote) || !int.TryParse(LocalConcurrency.Text, out var concurrency))
            throw new TrackerException("检查间隔与并发数请填写整数。");
        var theme = (AppTheme)ThemeChoice.SelectedIndex;
        var scale = ScaleChoice.SelectedIndex switch { 0 => 100, 1 => 125, 2 => 150, _ => 0 };
        await Model.Registry.UpdatePreferencesAsync(p => p with { LocalIntervalSeconds = local, RemoteIntervalMinutes = remote, LocalConcurrency = concurrency, Theme = theme, ScalePercent = scale });
    }, "检查频率、主题与缩放已保存并生效。");
    private async void ToggleKeep(object sender, RoutedEventArgs e)
    {
        var enabled = KeepSwitch.IsChecked == true;
        await SaveAsync(() => Model.Registry.UpdatePreferencesAsync(p => p with { KeepExpanded = enabled }), "保持展开设置已保存。");
    }
    private void ReadStartup()
    {
        try { StartupSwitch.IsChecked = ((MainWindow)Owner).StartupRegistration.IsEnabled; }
        catch (Exception error) when (ShellViewModel.IsUserFacing(error) || error is System.Security.SecurityException)
        { StartupSwitch.IsEnabled = false; Feedback.Text = error is TrackerException ? error.Message : "无法读取 Windows 启动设置，请检查当前用户权限。"; }
    }
    private async void ToggleStartup(object sender, RoutedEventArgs e)
    {
        var enabled = StartupSwitch.IsChecked == true;
        await SaveAsync(() =>
        {
            try { ((MainWindow)Owner).StartupRegistration.SetEnabled(enabled); }
            catch (System.Security.SecurityException) { throw new TrackerException("Windows 未允许修改启动项，请检查当前用户权限。"); }
            return Task.CompletedTask;
        }, enabled ? "已启用登录启动，登录后收起到任务栏。" : "已关闭登录启动。");
        ReadStartup();
    }
    private async void ToggleRemote(object sender, RoutedEventArgs e)
    {
        var enabled = RemoteSwitch.IsChecked == true;
        await SaveAsync(() => Model.Registry.SetRemoteEnabledAsync(enabled), enabled ? "已开启远程检查，正在排队。" : "已关闭远程检查，正在取消未完成任务；本地检查继续。");
    }
    private void RefreshRemote(object sender, RoutedEventArgs e)
    { ((MainWindow)Owner).RemoteScheduler.Refresh(); Feedback.Text = "远程检查已排队；结果显示在仓库行与提示中。"; }
    private async void SaveRule(object sender, RoutedEventArgs e)
    {
        await SaveAsync(async () => { var draft = Draft(); await Model.Registry.SaveSourceAsync(draft); ReloadRules(); RuleList.SelectedItem = draft; }, "来源规则已保存，列表显示已更新。");
    }
    private async void DeleteRule(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not SourceRule rule) { Feedback.Text = "请先选择要删除的规则。"; return; }
        await SaveAsync(async () => { await Model.Registry.DeleteSourceAsync(rule.Id); ReloadRules(); NewRule(sender, e); }, "规则已删除；未匹配的 remote 会显示为未知服务。");
    }
    private void PreviewRule(object sender, RoutedEventArgs e)
    {
        try
        {
            var rules = Model.Registry.State.Sources;
            if (!string.IsNullOrWhiteSpace(SourceName.Text) || !string.IsNullOrWhiteSpace(SourcePattern.Text))
            { var draft = Draft(); rules = rules.Where(rule => rule.Id != draft.Id).Append(draft).ToImmutableArray(); }
            var result = SourceRules.Match(new(!string.IsNullOrWhiteSpace(PreviewUrl.Text), SourceRules.Host(PreviewUrl.Text)), rules);
            PreviewResult.Text = result.Name + " · " + result.Explanation;
        }
        catch (Core.Tracking.TrackerException error) { Feedback.Text = error.Message; }
    }
}
