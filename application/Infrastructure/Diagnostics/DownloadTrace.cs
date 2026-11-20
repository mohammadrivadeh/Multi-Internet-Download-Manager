using System.Diagnostics;

namespace DownloadManager.Infrastructure.Diagnostics;

internal sealed class DownloadTrace : IAsyncDisposable
{
    private readonly DownloadPathTrace[] paths;
    private readonly Timer timer;

    internal DownloadTrace(Guid job, int pathCount, IReadOnlyList<int>? indices)
    {
        paths = Enumerable.Range(0, pathCount).Select(i => new DownloadPathTrace(job, indices?[i])).ToArray();
        timer = new Timer(_ => Sample(false), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    internal DownloadPathTrace this[int index] => paths[index];

    private void Sample(bool final)
    {
        foreach (var path in paths) path.Sample(final);
    }

    public async ValueTask DisposeAsync()
    {
        await timer.DisposeAsync();
        Sample(true);
    }
}

internal sealed class DownloadPathTrace(Guid job, int? index)
{
    private static readonly AsyncLocal<DownloadPathTrace?> Context = new();
    internal static DownloadPathTrace? Current => Context.Value;
    internal static readonly HttpRequestOptionsKey<string> RequestContext = new("DownloadManager.JobPath");
    internal string Detail { get; } = $"job={job:N} path={(index.HasValue ? $"interface-{index}" : "system")}";
    private long received;
    private int retries;
    private int active;
    private readonly Stopwatch sampleClock = Stopwatch.StartNew();
    private long previousBytes;

    internal void Receive(int count) => Interlocked.Add(ref received, count);

    internal IDisposable Enter()
    {
        var scope = new Scope(this, Context.Value);
        Context.Value = this;
        Interlocked.Increment(ref active);
        return scope;
    }

    private sealed class Scope(DownloadPathTrace path, DownloadPathTrace? previous) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Decrement(ref path.active);
            Context.Value = previous;
        }
    }

    internal void Retry(Exception error, int attempt)
    {
        Interlocked.Increment(ref retries);
        LocalDiagnostics.Record("download-retry", error, $"{Detail} attempt={attempt}");
    }

    internal void Sample(bool final)
    {
        lock (sampleClock)
        {
            long bytes = Interlocked.Read(ref received);
            var rate = (long)((bytes - previousBytes) / Math.Max(sampleClock.Elapsed.TotalSeconds, .001));
            LocalDiagnostics.Trace("download-path-sample", $"{Detail} payload_bytes={bytes} bytes_per_second={rate} active_operations={Volatile.Read(ref active)} retries={Volatile.Read(ref retries)} final={final}");
            previousBytes = bytes;
            sampleClock.Restart();
        }
    }
}
