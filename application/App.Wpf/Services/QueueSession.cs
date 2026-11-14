using System.IO;
using DownloadManager.Infrastructure.Persistence;
using DownloadManager.Infrastructure.Queueing;
namespace DownloadManager.App.Services;

public interface IQueueSession : IDisposable { Task<QueueCoordinator> InitializeAsync(); }
public sealed class QueueSession : IQueueSession
{
    private QueueStore? store;
    public async Task<QueueCoordinator> InitializeAsync() => await Task.Run(async () =>
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiInternetDownloadManager");
        store = new QueueStore(Path.Combine(directory, "queue.sqlite"), new WindowsQueueProtector());
        try
        {
            var coordinator = new QueueCoordinator(store, new QueuedDownloadExecutor());
            await coordinator.InitializeAsync();
            return coordinator;
        }
        catch { Dispose(); throw; }
    });
    public void Dispose() { store?.Dispose(); store = null; }
}
