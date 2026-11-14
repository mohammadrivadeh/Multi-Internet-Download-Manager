using System.ComponentModel;
using System.Windows;
using DownloadManager.App.ViewModels;
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
        Closing += OnClosing;
    }
    private async Task LoadAsync()
    { await viewModel.InitializeAsync(); await viewModel.RefreshCommand.ExecuteAsync(null); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (mayClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        viewModel.PauseCommand.Execute(null);
        if (initialization is not null) await initialization;
        await viewModel.ShutdownAsync();
        mayClose = true;
        Close();
    }
}
