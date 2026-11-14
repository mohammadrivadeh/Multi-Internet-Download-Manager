namespace DownloadManager.Core;

public sealed record TransferCoverageSnapshot(long DurableBytes, long AcceptedBytes);

/// <summary>Tracks unique accepted prefixes of exclusively owned ranges, including whole-range retries.</summary>
public sealed class TransferCoverage
{
    private readonly object sync = new();
    private readonly long[] lengths;
    private readonly long[] prefixes;
    private readonly long total;
    private long durable;
    public TransferCoverage(long total, long durable, int workers)
    {
        if (total < 0 || durable < 0 || durable > total || workers is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(total));
        this.total = total; this.durable = durable; lengths = new long[workers]; prefixes = new long[workers];
    }
    public void Start(int worker, long length)
    {
        lock (sync)
        {
            if (length <= 0 || lengths[worker] != 0 || length > total - durable - lengths.Sum()) throw new InvalidOperationException("Invalid range ownership.");
            lengths[worker] = length;
        }
    }
    public TransferCoverageSnapshot Advance(int worker, long prefix)
    {
        lock (sync)
        {
            if (prefix < 0 || prefix > lengths[worker]) throw new ArgumentOutOfRangeException(nameof(prefix));
            prefixes[worker] = Math.Max(prefixes[worker], prefix);
            return Snapshot();
        }
    }
    public TransferCoverageSnapshot Commit(int worker)
    {
        lock (sync)
        {
            if (lengths[worker] == 0 || prefixes[worker] != lengths[worker]) throw new InvalidOperationException("Range not fully accepted.");
            durable += lengths[worker]; lengths[worker] = 0; prefixes[worker] = 0;
            return Snapshot();
        }
    }
    private TransferCoverageSnapshot Snapshot() => new(durable, durable + prefixes.Sum());
}

public sealed class TransferRateEstimator
{
    private long baseline;
    private TimeSpan last;
    private bool initialized;
    private double previousRate;
    public (double BytesPerSecond, TimeSpan? Remaining) Sample(long accepted, long? total, TimeSpan elapsed)
    {
        if (!initialized || accepted < baseline || elapsed <= last)
        { initialized = true; baseline = accepted; last = elapsed; previousRate = 0; return (0, null); }
        if ((elapsed - last).TotalSeconds < 0.25) return (previousRate, Estimate(accepted, total, previousRate));
        var rate = (accepted - baseline) / (elapsed - last).TotalSeconds;
        baseline = accepted; last = elapsed; previousRate = rate;
        return (rate, Estimate(accepted, total, rate));
    }
    private static TimeSpan? Estimate(long accepted, long? total, double rate)
    {
        var seconds = total is { } length && length >= accepted && rate > 0 ? (length - accepted) / rate : double.NaN;
        return double.IsFinite(seconds) && seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
    }
}
