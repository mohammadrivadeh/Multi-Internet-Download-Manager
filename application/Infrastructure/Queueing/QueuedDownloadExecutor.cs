using System.Net;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Downloads;
using DownloadManager.Infrastructure.Diagnostics;
using DownloadManager.Infrastructure.Networking;

namespace DownloadManager.Infrastructure.Queueing;

public sealed class QueuedDownloadExecutor : IQueuedDownloadExecutor
{
    public async Task ExecuteAsync(QueueJob job, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        job.Validate();
        if (job.DailyWindow is null) { await ExecuteCoreAsync(job, progress, token); return; }
        if (!job.DailyWindow.IsOpen(DateTimeOffset.UtcNow)) throw new TransferWindowClosedException();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(job.DailyWindow.Remaining(DateTimeOffset.UtcNow));
        try { await ExecuteCoreAsync(job, progress, window.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && window.IsCancellationRequested)
        { throw new TransferWindowClosedException(); }
    }

    private static async Task ExecuteCoreAsync(QueueJob job, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        job.Validate();
        using var system = job.UseSystemRoute ? SystemRouteTransport.Create() : null;
        if (system is not null)
        {
            await new DownloadEngine().DownloadAsync(job.Source, job.Destination, [system], job.Options, progress, token, jobId: job.Id);
            return;
        }
        var sessions = new List<NetworkPathSession>();
        var limits = new List<long?>();
        var interfaceIndices = new List<int>();
        try
        {
            var catalog = new NetworkPathCatalog();
            var available = catalog.Discover();
            foreach (var selected in job.Paths)
            {
                token.ThrowIfCancellationRequested();
                var address = IPAddress.Parse(selected.SourceAddress);
                var path = NetworkPathCatalog.ResolveSelected(available, selected.AdapterId, address);
                if (path is null)
                {
                    LocalDiagnostics.Trace("download-path-unavailable", $"job={job.Id:N} reason=source-not-found family={address.AddressFamily}");
                    continue;
                }
                try { sessions.Add(new(path, catalog)); }
                catch (NetworkPathChangedException error)
                {
                    LocalDiagnostics.Record("download-path-unavailable", error, $"job={job.Id:N} interface={path.InterfaceIndex}");
                    continue;
                }
                limits.Add(selected.BytesPerSecondLimit);
                interfaceIndices.Add(path.InterfaceIndex);
            }
            if (sessions.Count == 0)
                throw new HttpRequestException("No queued path is available. Check the selected adapters and addresses.");
            await new DownloadEngine().DownloadAsync(job.Source, job.Destination, sessions.Select(s => s.Client).ToArray(),
                job.Options, progress, token, limits, job.Id, interfaceIndices, sessions.Select(s => s.Invalidated).ToArray());
        }
        catch (NetworkPathChangedException error)
        {
            throw new HttpRequestException("A selected network path changed or went offline.", error);
        }
        finally { foreach (var session in sessions) session.Dispose(); }
    }
}
