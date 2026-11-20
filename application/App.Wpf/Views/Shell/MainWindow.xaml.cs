using System.ComponentModel;
using System.Windows;
using DownloadManager.App.ViewModels;
using DownloadManager.App.Views.Sections;
using DownloadManager.App.Models;
using DownloadManager.App.Presentation;
using DownloadManager.Core;
using System.Windows.Controls;
namespace DownloadManager.App.Views.Shell;
public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private Task? initialization;
    private bool closing;
    private bool mayClose;
    public MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => { initialization = LoadAsync(); await initialization; };
        Closed += (_, _) => viewModel.Dispose();
        StateChanged += (_, _) => UpdateWindowStateButton();
        Closing += OnClosing;
    }
    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => ToggleWindowState();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void Navigate(string page)
    {
        if (viewModel.CurrentPage == page) return;
        viewModel.CurrentPage = page;
        AppMotion.Enter(PageSurface);
    }
    private void DownloadsClick(object sender, RoutedEventArgs e) => Navigate("Downloads");
    private void BalancerClick(object sender, RoutedEventArgs e) => Navigate("Network Balancer");
    private void QueueClick(object sender, RoutedEventArgs e) => Navigate("Queue");
    private void PreferencesClick(object sender, RoutedEventArgs e) => Navigate("Preferences");
    private async void MotionSettingsClick(object sender, RoutedEventArgs e)
    {
        var levels = new[] { "Off", "Subtle", "Standard", "Expressive" };
        await EditorDialogs.EditFieldsAsync(this, "Animation settings",
            [new("Animation intensity", levels[viewModel.MotionLevel], Options: levels)],
            values => viewModel.SaveMotionLevelAsync(Array.IndexOf(levels, values[0])));
    }
    private void NewDownloadClick(object sender, RoutedEventArgs e)
    {
        Navigate("Downloads");
        viewModel.PrepareNewDownload();
        var dialog = EditorDialogs.Create(this, "New download", new NewDownloadView(), 560, hasOwnHeader: true);
        dialog.DataContext = viewModel;
        dialog.ShowDialog();
    }
    private async void CategorySettingsClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: CategoryOption { Key.Length: > 0 } category }) return;
        var settings = viewModel.GetCategoryDefaults(category.Key);
        await EditorDialogs.EditFieldsAsync(this, $"{category.Name} settings",
            [new("Concurrent requests", settings.Workers.ToString(), Options: Enumerable.Range(1, 128).Select(value => value.ToString()).ToArray()),
             new("Priority", settings.Priority.ToString(), Options: Enumerable.Range(-10, 21).Select(value => value.ToString()).ToArray()),
             new("Base download folder", settings.DestinationRoot ?? "", ChooseSettingsFolder)],
            values =>
            {
                if (!int.TryParse(values[0], out var workers) || !int.TryParse(values[1], out var priority))
                    return Task.FromResult<string?>("Enter a valid request count and priority.");
                var root = string.IsNullOrWhiteSpace(values[2]) ? null : values[2].Trim();
                if (root is not null && !System.IO.Directory.Exists(root))
                    return Task.FromResult<string?>("Choose an existing base download folder.");
                return viewModel.SaveCategoryDefaultsAsync(new(category.Key, workers, priority, root));
            });
    }

    private string? ChooseSettingsFolder()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog();
        return picker.ShowDialog(this) == true ? picker.FolderName : null;
    }

    private async void StartQueueClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DownloadQueueSettings queue })
            await viewModel.StartQueueAsync(queue.Name, resumePaused: true);
    }

    private void PauseQueueClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DownloadQueueSettings queue })
            viewModel.PauseQueue(queue.Name);
    }

    private async void QueueSettingsClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: DownloadQueueSettings queue }) return;
        string? action = null;
        (string Label, Action Select)[] actions = string.Equals(queue.Name, QueueJob.MainQueue, StringComparison.OrdinalIgnoreCase)
            ? [] : [("Rename", () => action = "rename"), ("Delete", () => action = "delete")];
        await EditorDialogs.EditFieldsAsync(this, $"{queue.Name} queue settings",
            [new("Concurrent downloads", queue.MaxConcurrentDownloads == 0 ? "All" : queue.MaxConcurrentDownloads.ToString(),
                 Options: ["All", .. Enumerable.Range(1, 32).Select(value => value.ToString())]),
             new("Start (HH:mm)", queue.StartTime), new("Stop (HH:mm)", queue.StopTime)],
            values => viewModel.SaveQueueSettingsForAsync(queue.Name, values[0], values[1], values[2]), actions);
        if (action is null) return;
        viewModel.SelectedQueueName = queue.Name;
        if (action == "rename") RenameQueueClick(sender, e);
        else DeleteQueueClick(sender, e);
    }
    private async void CreateQueueClick(object sender, RoutedEventArgs e)
    {
        var saved = await EditorDialogs.EditTextAsync(this, "New queue", "Queue name", "", async name =>
        {
            viewModel.NewQueueName = name;
            await viewModel.CreateQueueCommand.ExecuteAsync(null);
            return viewModel.NewQueueName.Length == 0 && !string.IsNullOrWhiteSpace(name) ? null : viewModel.Status;
        });
        if (saved) Navigate("Queue");
    }
    private async void RenameQueueClick(object sender, RoutedEventArgs e)
    {
        await EditorDialogs.EditTextAsync(this, "Rename queue", "Queue name", viewModel.SelectedQueueName, async name =>
        {
            viewModel.NewQueueName = name;
            await viewModel.RenameQueueCommand.ExecuteAsync(null);
            return viewModel.NewQueueName.Length == 0 && !string.IsNullOrWhiteSpace(name) ? null : viewModel.Status;
        });
    }
    private async void DeleteQueueClick(object sender, RoutedEventArgs e)
    {
        var choice = MessageBox.Show(this, $"Delete the queue “{viewModel.SelectedQueueName}”? Its downloads will move to Main.",
            "Delete queue", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (choice == MessageBoxResult.Yes)
        {
            await viewModel.DeleteQueueCommand.ExecuteAsync(null);
            if (!string.IsNullOrWhiteSpace(viewModel.Status))
                MessageBox.Show(this, viewModel.Status, "Delete queue");
        }
    }
    private void CategorySelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded && e.AddedItems.Count > 0) Navigate("Downloads");
    }
    private void QueueSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded && e.AddedItems.Count > 0 && sender is System.Windows.Controls.ListBox list && (list.IsMouseOver || list.IsKeyboardFocusWithin)) Navigate("Queue");
    }
    private void UpdateWindowStateButton()
    {
        if (MaximizeGlyph is null) return;
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }
    private async Task LoadAsync()
    {
        await viewModel.InitializeAsync();
        if (!closing) AppMotion.Reveal(WindowSurface);
        await viewModel.RefreshCommand.ExecuteAsync(null);
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (mayClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        viewModel.PauseCommand.Execute(null);
        await BalancerPanel.StopCaptureAsync();
        if (initialization is not null) await initialization;
        await viewModel.ShutdownAsync();
        await AppMotion.DismissAsync(WindowSurface);
        mayClose = true;
        Close();
    }
}
