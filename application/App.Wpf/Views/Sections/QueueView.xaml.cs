using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using DownloadManager.App.ViewModels;
using DownloadManager.App.Views.Shell;
using DownloadManager.App.Models;
using DownloadManager.Core;
namespace DownloadManager.App.Views.Sections;
public partial class QueueView : UserControl
{
    public QueueView() => InitializeComponent();
    private bool updatingSelection;

    private void ToggleSelectionMode(object sender, RoutedEventArgs e)
    {
        var enable = SelectionColumn.Visibility != Visibility.Visible;
        if (!enable) DownloadsGrid.UnselectAll();
        DownloadsGrid.SelectionMode = enable ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single;
        SelectionColumn.Visibility = enable ? Visibility.Visible : Visibility.Collapsed;
        SelectionModeButton.Content = enable ? "Done selecting" : "Select multiple";
    }

    private void ToggleAllDownloads(object sender, RoutedEventArgs e)
    {
        if (DownloadsGrid.SelectedItems.Count == DownloadsGrid.Items.Count)
            DownloadsGrid.UnselectAll();
        else
            DownloadsGrid.SelectAll();
        if (sender is CheckBox checkBox)
            checkBox.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
    }

    private QueueRow[] SelectedDownloads() => DownloadsGrid.SelectedItems.OfType<QueueRow>().ToArray();

    private void DownloadsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource) || DownloadsGrid is null) return;
        var selected = SelectedDownloads();
        DownloadsGrid.Tag = selected.Length == 0 ? false : selected.Length == DownloadsGrid.Items.Count ? true : (bool?)null;
        var canEdit = !updatingSelection && selected.Length > 0 &&
                      selected.All(row => row.Job.State != QueueJobState.Running);
        MoveButton.IsEnabled = CategoryButton.IsEnabled = canEdit;
        SelectionHint.Text = selected.Length == 0
            ? "Check downloads to move or categorize them."
            : selected.Any(row => row.Job.State == QueueJobState.Running)
                ? $"{selected.Length} selected · Pause active downloads before editing."
                : $"{selected.Length} selected · Move them to a queue or set a category.";
    }

    private void SelectContextRow(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(grid, source) is DataGridRow row && !row.IsSelected)
            grid.SelectedItem = row.Item;
    }

    private async void AssignCategoryClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedDownloads();
        if (Window.GetWindow(this) is not { } owner || selected.Length == 0) return;
        var model = (MainViewModel)DataContext;
        await EditorDialogs.EditFieldsAsync(owner, "Set category",
            [new("Category", selected.Length == 1 ? selected[0].Job.Category : "",
                 Options: model.DownloadCategories.Select(item => item.Name).ToArray(), AllowCustomValue: true)],
            values =>
            {
                var value = values[0];
                try { DownloadCategory.Normalize(value); }
                catch (ArgumentException) { return Task.FromResult<string?>("Enter a category name."); }
                return ApplyCategoryAsync(value);
            });

        async Task<string?> ApplyCategoryAsync(string value)
        {
            await ApplySelectionAsync(selected, value, move: false);
            return null;
        }
    }

    private async void MoveDownloadsClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedDownloads();
        if (DataContext is not MainViewModel model || Window.GetWindow(this) is not { } owner || selected.Length == 0) return;
        var queues = model.Queues.Select(queue => queue.Name).ToArray();
        if (queues.Length == 0) return;
        var initial = queues.Contains(model.MoveTargetQueueName) ? model.MoveTargetQueueName : queues[0];
        await EditorDialogs.EditFieldsAsync(owner, "Move downloads",
            [new("Destination queue", initial, Options: queues)],
            async values =>
            {
                if (string.IsNullOrEmpty(values[0])) return "Choose a destination queue.";
                model.MoveTargetQueueName = values[0];
                await ApplySelectionAsync(selected, values[0], move: true);
                return null;
            });
    }

    private async Task ApplySelectionAsync(QueueRow[] selected, string value, bool move)
    {
        if (updatingSelection || selected.Length == 0 || DataContext is not MainViewModel model) return;
        if (selected.Any(row => row.Job.State == QueueJobState.Running))
        {
            SelectionHint.Text = "Pause active downloads before moving or categorizing them.";
            return;
        }
        updatingSelection = true;
        MoveButton.IsEnabled = CategoryButton.IsEnabled = false;
        try { SelectionHint.Text = await model.UpdateDownloadSelectionAsync(selected.Select(row => row.Job.Id).ToArray(), value, move); }
        finally
        {
            updatingSelection = false;
            var current = SelectedDownloads();
            MoveButton.IsEnabled = CategoryButton.IsEnabled = current.Length > 0 && current.All(row => row.Job.State != QueueJobState.Running);
        }
    }

    private async void EditDownloadSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } owner || DataContext is not MainViewModel model ||
            !model.EditSelectedCommand.CanExecute(null)) return;

        await model.EditSelectedCommand.ExecuteAsync(null);
        if (!model.IsEditing) return;
        try
        {
            await EditorDialogs.EditFieldsAsync(owner, "Edit download settings",
                [new("Concurrent requests", model.WorkerCount, Options: Enumerable.Range(1, 128).Select(n => n.ToString()).ToArray()),
                 new("Priority", model.Priority, Options: Enumerable.Range(-10, 21).Select(n => n.ToString()).ToArray()),
                 new("Category", model.Category, Options: model.DownloadCategories.Select(item => item.Name).ToArray(), AllowCustomValue: true),
                 new("Speed limit (KiB/s, 0 for unlimited)", model.SpeedLimitKiB),
                 new("Scheduled start (ISO date with time zone)", model.ScheduledStart),
                 new("Daily start (HH:mm)", model.WindowStart), new("Daily stop (HH:mm)", model.WindowEnd),
                 new("Time zone offset", model.WindowOffset),
                 new("Network routing", model.UseSystemRoute ? "Windows system route" : "Saved download routes",
                     Options: model.UseSystemRoute ? ["Windows system route"] : ["Saved download routes", "Windows system route"])],
                async values =>
                {
                    model.WorkerCount = values[0];
                    model.Priority = values[1];
                    model.Category = values[2];
                    model.SpeedLimitKiB = values[3];
                    model.ScheduledStart = values[4];
                    model.WindowStart = values[5];
                    model.WindowEnd = values[6];
                    model.WindowOffset = values[7];
                    model.UseSystemRoute = values[8] == "Windows system route";
                    await model.SaveEditedCommand.ExecuteAsync(null);
                    return model.IsEditing ? model.Status : null;
                });
        }
        finally
        {
            if (model.IsEditing) model.CancelEditCommand.Execute(null);
        }
    }

    private void PropertiesClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } owner || DataContext is not MainViewModel model ||
            model.SelectedJob is not { } row) return;

        var panel = new StackPanel { Margin = new Thickness(18) };
        AddField("File", row.Filename);
        AddField("Destination", row.Destination);
        AddField("Origin", row.Origin);
        AddField("Expected SHA-256", row.Checksum);
        AddField("Start at", row.Schedule);
        AddField("Transfer window", row.DailyWindow);
        if (!string.IsNullOrWhiteSpace(row.Failure))
            AddField("Issue", row.Failure);
        panel.Children.Add(new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right });
        var content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        EditorDialogs.Create(owner, "Download properties", content, 620).ShowDialog();

        void AddField(string label, string value)
        {
            var field = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(new Label { Content = label, Target = field });
            panel.Children.Add(field);
        }
    }
}
