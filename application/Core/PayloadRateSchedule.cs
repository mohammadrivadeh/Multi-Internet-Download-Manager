namespace DownloadManager.Core;

/// <summary>Shared delivery schedule; caller supplies elapsed monotonic time.</summary>
public sealed class PayloadRateSchedule
{
    private readonly long bytesPerSecond;
    private readonly object sync = new();
    private TimeSpan next;
    public PayloadRateSchedule(long bytesPerSecond)
    {
        if (bytesPerSecond is < 1 or > 1024L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        this.bytesPerSecond = bytesPerSecond;
    }
    public TimeSpan Reserve(int bytes, TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        var duration = TimeSpan.FromTicks(Math.Max(1, (long)Math.Ceiling((double)bytes * TimeSpan.TicksPerSecond / bytesPerSecond)));
        lock (sync)
        {
            next = (next > elapsed ? next : elapsed) + duration;
            return next - elapsed;
        }
    }
}
