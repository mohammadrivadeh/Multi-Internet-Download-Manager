using DownloadManager.Core;
namespace DownloadManager.Tests;
public class TransferCoverageTests
{
    [Fact]
    public void RetryPrefixesNeverDoubleCountAndOnlyCommitsBecomeDurable()
    {
        var state = new TransferCoverage(100, 20, 2);
        state.Start(0, 40); state.Start(1, 40);
        Assert.Equal(new TransferCoverageSnapshot(20, 50), state.Advance(0, 30));
        Assert.Equal(new TransferCoverageSnapshot(20, 50), state.Advance(0, 10));
        Assert.Equal(new TransferCoverageSnapshot(20, 70), state.Advance(1, 20));
        state.Advance(0, 40);
        Assert.Equal(new TransferCoverageSnapshot(60, 80), state.Commit(0));
        state.Advance(1, 40);
        Assert.Equal(new TransferCoverageSnapshot(100, 100), state.Commit(1));
    }
    [Fact]
    public void IncompleteRangesAndExcessCoverageAreRejected()
    {
        var state = new TransferCoverage(10, 0, 2);
        state.Start(0, 8);
        Assert.Throws<InvalidOperationException>(() => state.Start(1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Advance(0, 9));
        Assert.Throws<InvalidOperationException>(() => state.Commit(0));
    }
    [Fact]
    public void ResumeBaselineDoesNotCountAsTransferredSpeedAndUnknownEtaStaysUnknown()
    {
        var rate = new TransferRateEstimator();
        Assert.Null(rate.Sample(1000, 2000, TimeSpan.Zero).Remaining);
        var sample = rate.Sample(1500, 2000, TimeSpan.FromSeconds(2));
        Assert.Equal(250, sample.BytesPerSecond); Assert.Equal(TimeSpan.FromSeconds(2), sample.Remaining);
        Assert.Null(rate.Sample(1600, null, TimeSpan.FromSeconds(3)).Remaining);
        Assert.Equal(0, rate.Sample(0, 2000, TimeSpan.FromSeconds(4)).BytesPerSecond);
    }
    [Fact]
    public void SubintervalSamplesRetainEtaInsteadOfFlickeringUnknown()
    {
        var rate = new TransferRateEstimator();
        rate.Sample(0, 10000, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(9), rate.Sample(1000, 10000, TimeSpan.FromSeconds(1)).Remaining);
        var next = rate.Sample(1100, 10000, TimeSpan.FromSeconds(1.1));
        Assert.Equal(1000, next.BytesPerSecond); Assert.Equal(TimeSpan.FromSeconds(8.9), next.Remaining);
        Assert.Null(rate.Sample(1150, null, TimeSpan.FromSeconds(1.15)).Remaining);
    }

    [Fact]
    public void StalledTransferResetsIndependentlyAndResumesWithAFreshBaseline()
    {
        var stalled = new TransferRateEstimator();
        var active = new TransferRateEstimator();
        stalled.Sample(0, 10000, TimeSpan.Zero);
        active.Sample(0, 10000, TimeSpan.Zero);
        Assert.Equal(1000, stalled.Sample(1000, 10000, TimeSpan.FromSeconds(1)).BytesPerSecond);
        active.Sample(4000, 10000, TimeSpan.FromSeconds(4));

        var now = TimeSpan.FromSeconds(6);
        Assert.True(stalled.ResetIfStale(now));
        Assert.False(active.ResetIfStale(now));
        Assert.Equal(1000, active.Sample(6000, 10000, now).BytesPerSecond);
        Assert.Equal(0, stalled.Sample(2500, 10000, now).BytesPerSecond);
        Assert.Equal(500, stalled.Sample(3000, 10000, TimeSpan.FromSeconds(7)).BytesPerSecond);
        Assert.Equal(0, active.Sample(8000, 10000, TimeSpan.FromSeconds(11)).BytesPerSecond);
    }
}
