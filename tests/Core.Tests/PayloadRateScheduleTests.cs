using DownloadManager.Core;
namespace DownloadManager.Tests;
public class PayloadRateScheduleTests
{
    [Fact]
    public void SharedReservationsDoNotMultiplyTheCapByWorkerCount()
    {
        var schedule = new PayloadRateSchedule(1000);
        Assert.Equal(TimeSpan.FromSeconds(0.5), schedule.Reserve(500, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(1), schedule.Reserve(500, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(1.1), schedule.Reserve(100, TimeSpan.Zero));
    }
    [Fact]
    public void IdleTimeDoesNotAccumulateAnUnboundedBurstCredit()
    {
        var schedule = new PayloadRateSchedule(100);
        Assert.Equal(TimeSpan.FromSeconds(1), schedule.Reserve(100, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(1), schedule.Reserve(100, TimeSpan.FromHours(1)));
    }
    [Fact]
    public async Task ConcurrentWorkersReceiveDistinctDeliverySlots()
    {
        var schedule = new PayloadRateSchedule(1000);
        var reservations = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => schedule.Reserve(1000, TimeSpan.Zero))));
        Assert.Equal(64, reservations.Distinct().Count());
        Assert.Equal(TimeSpan.FromSeconds(64), reservations.Max());
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1073741825)]
    public void InvalidCapsAreRejected(long cap) => Assert.Throws<ArgumentOutOfRangeException>(() => new PayloadRateSchedule(cap));
}
