using System.Net;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Downloads;
using DownloadManager.Infrastructure.Networking;

namespace DownloadManager.Infrastructure.Queueing;

public sealed class QueuedDownloadExecutor : IQueuedDownloadExecutor
{
    public async Task ExecuteAsync(QueueJob job, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        job.Validate();
        using var system = job.UseSystemRoute ? SystemRouteTransport.Create() : null;
        if (system is not null)
        {
            await new DownloadEngine().DownloadAsync(job.Source, job.Destination, [system], job.Options, progress, token);
            return;
        }
        var sessions = new List<NetworkPathSession>();
        try
        {
            var catalog = new NetworkPathCatalog();
            var available = catalog.Discover();
            foreach (var selected in job.Paths)
            {
                var address = IPAddress.Parse(selected.SourceAddress);
                var path = available.SingleOrDefault(p => p.AdapterId == selected.AdapterId && p.SourceAddress.Equals(address))
                    ?? throw new InvalidOperationException("A queued path is unavailable. Edit the job's connection selection.");
                sessions.Add(new(path, catalog));
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessions.Select(s => s.Invalidated).Append(token).ToArray());
            await new DownloadEngine().DownloadAsync(job.Source, job.Destination, sessions.Select(s => s.Client).ToArray(), job.Options, progress, linked.Token);
        }
        finally { foreach (var session in sessions) session.Dispose(); }
    }
}
