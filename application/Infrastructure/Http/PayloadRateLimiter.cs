using DownloadManager.Core;
namespace DownloadManager.Infrastructure.Http;

/// <summary>Per-download accepted payload cap shared across all workers; not a wire-traffic meter.</summary>
public sealed class PayloadRateLimiter
{
    private readonly TimeProvider clock;
    private readonly long origin;
    private readonly PayloadRateSchedule schedule;
    public int ReadSize { get; }
    public PayloadRateLimiter(long bytesPerSecond, TimeProvider? clock = null)
    {
        schedule = new(bytesPerSecond);
        this.clock = clock ?? TimeProvider.System;
        origin = this.clock.GetTimestamp();
        ReadSize = (int)Math.Clamp(bytesPerSecond / 10, 1, 65536);
    }
    internal static async Task WaitBothAsync(PayloadRateLimiter? download, PayloadRateLimiter? path, int bytes, CancellationToken token)
    {
        if (download is not null && path is not null)
            await Task.WhenAll(download.WaitAsync(bytes, token).AsTask(), path.WaitAsync(bytes, token).AsTask());
        else if (download is not null) await download.WaitAsync(bytes, token);
        else if (path is not null) await path.WaitAsync(bytes, token);
    }
    public async ValueTask WaitAsync(int receivedBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var elapsed = clock.GetElapsedTime(origin);
        var wait = schedule.Reserve(receivedBytes, elapsed);
        await Task.Delay(wait, clock, token);
    }
}
