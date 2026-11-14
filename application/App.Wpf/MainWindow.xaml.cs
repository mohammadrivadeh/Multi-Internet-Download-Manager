using System.ComponentModel;
using System.Windows;
namespace DownloadManager.App;
public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    private bool closing;
    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.RefreshCommand.ExecuteAsync(null);
        Closing += OnClosing;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!viewModel.IsBusy) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        viewModel.PauseCommand.Execute(null);
        if (viewModel.StartCommand.ExecutionTask is { } task) await task;
        Close();
    }
}
