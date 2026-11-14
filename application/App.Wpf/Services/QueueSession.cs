using System.IO;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Persistence;
using DownloadManager.Infrastructure.Queueing;
namespace DownloadManager.App.Services;

public interface IQueueSession : IDisposable { Task<QueueCoordinator> InitializeAsync(); Task<DesktopPreferences> LoadPreferencesAsync(); Task SavePreferencesAsync(DesktopPreferences value); }
public sealed class QueueSession : IQueueSession
{
    private QueueStore? store;
    private PreferencesStore? preferences;
    public async Task<QueueCoordinator> InitializeAsync() => await Task.Run(async () =>
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiInternetDownloadManager");
        store = new QueueStore(Path.Combine(directory, "queue.sqlite"), new WindowsQueueProtector());
        try
        {
            preferences = new PreferencesStore(Path.Combine(directory, "preferences.bin"), new WindowsQueueProtector());
            var coordinator = new QueueCoordinator(store, new QueuedDownloadExecutor());
            await coordinator.InitializeAsync();
            return coordinator;
        }
        catch { Dispose(); throw; }
    });
    public Task<DesktopPreferences> LoadPreferencesAsync() => Task.Run(() => (preferences ?? throw new InvalidOperationException("Initialize the session first.")).Load());
    public Task SavePreferencesAsync(DesktopPreferences value) => Task.Run(() => (preferences ?? throw new InvalidOperationException("Initialize the session first.")).Save(value));
    public void Dispose() { store?.Dispose(); store = null; }
}
