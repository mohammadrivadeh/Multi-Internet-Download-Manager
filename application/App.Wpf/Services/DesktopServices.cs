using System.Windows.Threading;
using Microsoft.Win32;
namespace DownloadManager.App.Services;

public interface IDesktopTimer { void Start(); void Stop(); }
public interface IDesktopServices
{
    string Text(string key);
    string? ChooseDestination(string suggestion);
    string? ChooseFolder();
    void Post(Action action);
    IDesktopTimer CreateTimer(Func<Task> tick);
}

public sealed class DesktopServices(Dispatcher dispatcher) : IDesktopServices
{
    public string Text(string key) => (string)System.Windows.Application.Current.FindResource(key);
    public string? ChooseDestination(string suggestion)
    {
        var picker = new SaveFileDialog
        {
            InitialDirectory = System.IO.Path.GetDirectoryName(suggestion),
            FileName = System.IO.Path.GetFileName(suggestion),
            OverwritePrompt = false
        };
        return picker.ShowDialog() == true ? picker.FileName : null;
    }
    public string? ChooseFolder()
    {
        var picker = new OpenFolderDialog();
        return picker.ShowDialog() == true ? picker.FolderName : null;
    }
    public void Post(Action action) => dispatcher.InvokeAsync(action);
    public IDesktopTimer CreateTimer(Func<Task> tick) => new DesktopTimer(dispatcher, tick);
    private sealed class DesktopTimer : IDesktopTimer
    {
        private readonly DispatcherTimer timer;
        private bool inFlight;
        public DesktopTimer(Dispatcher dispatcher, Func<Task> tick)
        {
            timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, async (_, _) =>
            {
                if (inFlight) return;
                inFlight = true;
                try { await tick(); }
                finally { inFlight = false; }
            }, dispatcher);
            timer.Stop();
        }
        public void Start() => timer.Start();
        public void Stop() => timer.Stop();
    }
}
