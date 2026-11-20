using System.Windows;
using DownloadManager.App.Presentation;
using DownloadManager.App.Services;
using DownloadManager.App.ViewModels;
using DownloadManager.App.Views.Shell;
namespace DownloadManager.App;
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppMotion.Register();
        // The composition root owns desktop adapters and the protected queue session.
        var model = new MainViewModel(new DesktopServices(Dispatcher), new QueueSession());
        var window = new MainWindow(model);
        MainWindow = window;
        window.Show();
    }
}
