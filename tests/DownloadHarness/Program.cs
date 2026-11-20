using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Diagnostics;
using DownloadManager.Infrastructure.Queueing;

var useSystemRoute = args.Length == 4 && args[0] == "system";
var useBoundPaths = args.Length >= 6 && args.Length % 2 == 0 && args[0] == "paths";
if ((!useSystemRoute && !useBoundPaths) ||
    !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var workers) ||
    workers is < 1 or > 128 || (!useSystemRoute && (args.Length - 4) / 2 > workers))
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  DownloadHarness system <http(s)-url> <destination> <workers:1..128>");
    Console.Error.WriteLine("  DownloadHarness paths <http(s)-url> <destination> <workers:1..128> <adapter-id> <source-address> [<adapter-id> <source-address> ...]");
    Console.Error.WriteLine("Use NetworkProbe list to find adapter IDs and source addresses.");
    return 2;
}

using var stop = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
Console.CancelKeyPress += cancel;

try
{
    var job = new QueueJob
    {
        Source = new Uri(args[1], UriKind.Absolute),
        Destination = Path.GetFullPath(args[2]),
        Options = new DownloadOptions { Workers = workers },
        UseSystemRoute = useSystemRoute,
        Paths = args.Skip(4).Chunk(2).Select(pair => new QueuedPath(pair[0], pair[1])).ToArray(),
        CreatedAt = DateTimeOffset.UtcNow
    };
    var stopwatch = Stopwatch.StartNew();
    var progress = new StageReporter();
    Console.WriteLine($"Job {job.Id:N} with {job.Paths.Count} explicitly selected paths.");
    await new QueuedDownloadExecutor().ExecuteAsync(job, progress, stop.Token);
    stopwatch.Stop();

    await using var file = File.OpenRead(job.Destination);
    var hash = Convert.ToHexString(await SHA256.HashDataAsync(file));
    Console.WriteLine("Completed and finalized.");
    Console.WriteLine(FormattableString.Invariant($"Size: {file.Length} bytes, elapsed: {stopwatch.Elapsed.TotalSeconds:F2}s"));
    Console.WriteLine($"SHA-256: {hash}");
    Console.WriteLine($"Diagnostics: {LocalDiagnostics.FilePath}");
    return 0;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine($"Download did not complete ({ex.GetType().Name}). Existing resumable state was preserved where supported.");
    Console.Error.WriteLine($"Diagnostics: {LocalDiagnostics.FilePath}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancel;
}

sealed class StageReporter : IProgress<DownloadProgress>
{
    private DownloadStage? lastStage;

    public void Report(DownloadProgress progress)
    {
        if (lastStage == progress.Stage)
            return;
        lastStage = progress.Stage;
        Console.WriteLine($"{progress.Stage}: {progress.CompletedBytes} durable bytes, resumed={progress.Resumed}");
    }
}
