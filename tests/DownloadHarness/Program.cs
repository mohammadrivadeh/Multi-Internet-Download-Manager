using DownloadManager.Core;
using DownloadManager.Infrastructure.Downloads;
using DownloadManager.Infrastructure.Networking;

if (args is not ["system", var url, var destination, var workers])
{
    Console.Error.WriteLine("Usage: DownloadHarness system <http(s)-url> <destination> <workers:1..128>");
    return 2;
}
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    using var client = SystemRouteTransport.Create();
    await new DownloadEngine().DownloadAsync(new Uri(url, UriKind.Absolute), destination, [client], new DownloadOptions { Workers = int.Parse(workers) }, cancellationToken: stop.Token);
    Console.WriteLine("Completed and finalized.");
    return 0;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine($"Download did not complete ({ex.GetType().Name}). Existing resumable state was preserved where supported.");
    return 1;
}
