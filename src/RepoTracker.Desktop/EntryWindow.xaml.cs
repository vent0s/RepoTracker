using System.Windows;
using RepoTracker.Desktop.ViewModels;

namespace RepoTracker.Desktop;

public partial class EntryWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<string, Guid?, CancellationToken, Task> save;
    private readonly Func<Window, string?>? browse;
    public EntryWindow(string title, string description, string? label, string initialValue,
        IReadOnlyList<GroupChoice>? groups, Guid? groupId, string submitLabel,
        Func<string, Guid?, CancellationToken, Task> save, Func<Window, string?>? browse = null)
    {
        this.save = save;
        this.browse = browse;
        InitializeComponent();
        Title = title + " · RepoTracker";
        Heading.Text = title;
        Description.Text = description;
        InputLabel.Text = label;
        InputBox.Text = initialValue;
        InputLabel.Visibility = InputRow.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
        BrowseButton.Visibility = browse is null ? Visibility.Collapsed : Visibility.Visible;
        ChoiceRow.Visibility = groups is null ? Visibility.Collapsed : Visibility.Visible;
        ChoicesBox.ItemsSource = groups;
        ChoicesBox.SelectedItem = groups?.FirstOrDefault(group => group.Id == groupId);
        SubmitButton.Content = submitLabel;
        Height = groups is not null || browse is not null ? 440 : label is not null ? 340 : 310;
        System.Windows.Automation.AutomationProperties.SetName(InputBox, label ?? title);
        ThemeManager.PrepareDialog(this);
        Loaded += (_, _) => { if (label is not null) { InputBox.Focus(); InputBox.SelectAll(); } else SubmitButton.Focus(); };
        Closed += (_, _) => lifetime.Cancel();
    }

    private void Browse(object sender, RoutedEventArgs e)
    {
        if (browse?.Invoke(this) is { } selected) InputBox.Text = selected;
    }

    private async void Submit(object sender, RoutedEventArgs e)
    {
        SubmitButton.IsEnabled = Fields.IsEnabled = false;
        ErrorText.Text = "";
        try
        {
            await save(InputBox.Text, (ChoicesBox.SelectedItem as GroupChoice)?.Id, lifetime.Token);
            if (IsVisible) DialogResult = true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (ShellViewModel.IsUserFacing(error)) { ErrorText.Text = ShellViewModel.Message(error); }
        finally { SubmitButton.IsEnabled = Fields.IsEnabled = true; }
    }
}
