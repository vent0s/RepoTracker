using RepoTracker.Core.Localization;
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
            LanguageChoice.SelectedIndex = Array.IndexOf(new[] { Text.SystemLanguage, "zh-Hans", "en" }, preferences.Language);
            ReadStartup();
        };
        Closing += (_, e) => { if (saving) e.Cancel = true; };
    }
    private void ReloadRules() => RuleList.ItemsSource = Model.Registry.State.Sources;
    private void Finish(object sender, RoutedEventArgs e) => Close();
    private async void SaveLanguage(object sender, RoutedEventArgs e)
    {
        var language = LanguageChoice.SelectedIndex switch { 0 => Text.SystemLanguage, 1 => "zh-Hans", 2 => "en", _ => "" };
        await SaveAsync(() => Model.Registry.UpdatePreferencesAsync(p => p with { Language = language }), Text.Get("Language.Saved"));
    }
    private void NewRule(object sender, RoutedEventArgs e)
    {
        RuleList.SelectedItem = null; draftId = Guid.NewGuid(); SourceName.Text = ""; SourcePattern.Text = "";
        SourceIcon.SelectedItem = Core.Sources.SourceIcon.Server; Feedback.Text = Text.Get("Source.NewHint");
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
            throw new TrackerException(Text.Get("Settings.Integers"));
        var theme = (AppTheme)ThemeChoice.SelectedIndex;
        var scale = ScaleChoice.SelectedIndex switch { 0 => 100, 1 => 125, 2 => 150, _ => 0 };
        await Model.Registry.UpdatePreferencesAsync(p => p with { LocalIntervalSeconds = local, RemoteIntervalMinutes = remote, LocalConcurrency = concurrency, Theme = theme, ScalePercent = scale });
    }, Text.Get("Settings.Saved"));
    private async void ToggleKeep(object sender, RoutedEventArgs e)
    {
        var enabled = KeepSwitch.IsChecked == true;
        await SaveAsync(() => Model.Registry.UpdatePreferencesAsync(p => p with { KeepExpanded = enabled }), Text.Get("Window.KeepSaved"));
    }
    private void ReadStartup()
    {
        try { StartupSwitch.IsChecked = ((MainWindow)Owner).StartupRegistration.IsEnabled; }
        catch (Exception error) when (ShellViewModel.IsUserFacing(error) || error is System.Security.SecurityException)
        { StartupSwitch.IsEnabled = false; Feedback.Text = error is TrackerException ? error.Message : Text.Get("Startup.ReadFailed"); }
    }
    private async void ToggleStartup(object sender, RoutedEventArgs e)
    {
        var enabled = StartupSwitch.IsChecked == true;
        await SaveAsync(() =>
        {
            try { ((MainWindow)Owner).StartupRegistration.SetEnabled(enabled); }
            catch (System.Security.SecurityException) { throw new TrackerException(Text.Get("Startup.WriteDenied")); }
            return Task.CompletedTask;
        }, enabled ? Text.Get("Startup.Enabled") : Text.Get("Startup.Disabled"));
        ReadStartup();
    }
    private async void ToggleRemote(object sender, RoutedEventArgs e)
    {
        var enabled = RemoteSwitch.IsChecked == true;
        await SaveAsync(() => Model.Registry.SetRemoteEnabledAsync(enabled), enabled ? Text.Get("Remote.Enabled") : Text.Get("Remote.Disabled"));
    }
    private void RefreshRemote(object sender, RoutedEventArgs e)
    { ((MainWindow)Owner).RemoteScheduler.Refresh(); Feedback.Text = Text.Get("Remote.Queued"); }
    private async void SaveRule(object sender, RoutedEventArgs e)
    {
        await SaveAsync(async () => { var draft = Draft(); await Model.Registry.SaveSourceAsync(draft); ReloadRules(); RuleList.SelectedItem = draft; }, Text.Get("Source.Saved"));
    }
    private async void DeleteRule(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not SourceRule rule) { Feedback.Text = Text.Get("Source.SelectFirst"); return; }
        await SaveAsync(async () => { await Model.Registry.DeleteSourceAsync(rule.Id); ReloadRules(); NewRule(sender, e); }, Text.Get("Source.Deleted"));
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
