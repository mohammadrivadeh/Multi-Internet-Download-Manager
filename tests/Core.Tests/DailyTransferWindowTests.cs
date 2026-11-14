using DownloadManager.Core;
namespace DownloadManager.Tests;
public class DailyTransferWindowTests
{
    [Fact]
    public void OffsetAndBoundariesAreExplicit()
    {
        var window = DailyTransferWindow.Parse("09:00", "17:00", "+03:30")!;
        Assert.False(window.IsOpen(DateTimeOffset.Parse("2026-11-15T05:29:59Z")));
        Assert.True(window.IsOpen(DateTimeOffset.Parse("2026-11-15T05:30:00Z")));
        Assert.False(window.IsOpen(DateTimeOffset.Parse("2026-11-15T13:30:00Z")));
        Assert.Equal(TimeSpan.FromHours(8), window.Remaining(DateTimeOffset.Parse("2026-11-15T05:30:00Z")));
    }
    [Fact]
    public void OvernightWindowCrossesMidnightAndNeverRunsOutsideWindow()
    {
        var window = new DailyTransferWindow(22 * 60, 6 * 60, 0);
        Assert.True(window.IsOpen(DateTimeOffset.Parse("2026-11-15T23:00:00Z")));
        Assert.Equal(TimeSpan.FromHours(7), window.Remaining(DateTimeOffset.Parse("2026-11-15T23:00:00Z")));
        Assert.True(window.IsOpen(DateTimeOffset.Parse("2026-11-16T05:00:00Z")));
        Assert.False(window.IsOpen(DateTimeOffset.Parse("2026-11-16T06:00:00Z")));
        Assert.Null(QueueScheduling.Next([new QueueJob { Source = new("https://example.test/f"), Destination = "f", UseSystemRoute = true, DailyWindow = window }], DateTimeOffset.Parse("2026-11-16T12:00:00Z")));
    }
    [Fact]
    public void EmptyMeansUnrestrictedAndEqualEndpointsAreRejected()
    {
        Assert.Null(DailyTransferWindow.Parse("", "", "+03:30"));
        Assert.Throws<ArgumentException>(() => DailyTransferWindow.Parse("12:00", "12:00", "+03:30"));
    }
    [Fact]
    public void InvalidQueuedPathCapIsRejected()
    {
        var job = new QueueJob { Source = new("https://example.test/f"), Destination = "f", Paths = [new("adapter", "192.0.2.1", 0)] };
        Assert.Throws<ArgumentException>(job.Validate);
    }
}
