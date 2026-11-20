using System.Net.Sockets;
using DownloadManager.NetworkBalancer;

namespace DownloadManager.Tests;

public sealed class FlowSchedulerTests
{
    private static readonly EgressKey First = new(new Guid(1, 0, 0, new byte[8]), AddressFamily.InterNetwork);
    private static readonly EgressKey Second = new(new Guid(2, 0, 0, new byte[8]), AddressFamily.InterNetwork);

    [Fact]
    public void ConcurrentReservationsDistributeInProportionToWeights()
    {
        var scheduler = new FlowScheduler();
        scheduler.Update([new(First, 3, LinkHealth.Reachable), new(Second, 1, LinkHealth.Reachable)]);
        var leases = new FlowLease[40];
        Parallel.For(0, leases.Length, i => leases[i] = scheduler.TryAcquire(First.Family)!);
        try
        {
            Assert.Equal(30, leases.Count(lease => lease.Key == First));
            Assert.Equal(10, leases.Count(lease => lease.Key == Second));
        }
        finally
        {
            foreach (var lease in leases)
                lease?.Dispose();
        }
    }

    [Fact]
    public void AcquisitionRespectsHealthFamilyAndRetryExclusion()
    {
        var scheduler = new FlowScheduler();
        var ipv6 = First with { Family = AddressFamily.InterNetworkV6 };
        scheduler.Update([new(First, 100, LinkHealth.Unknown), new(Second, 1, LinkHealth.Reachable),
            new(ipv6, 1, LinkHealth.Unreachable)]);

        using var healthy = scheduler.TryAcquire(First.Family);
        using var fallback = scheduler.TryAcquire(First.Family, Second.AdapterId);
        Assert.Equal(Second, healthy?.Key);
        Assert.Equal(First, fallback?.Key);
        Assert.Null(scheduler.TryAcquire(ipv6.Family));

        scheduler.Update([new(First, 100, LinkHealth.Unreachable), new(ipv6, 1, LinkHealth.Reachable)]);
        using var otherFamily = scheduler.TryAcquire(ipv6.Family);
        Assert.Equal(ipv6, otherFamily?.Key);
        Assert.Null(scheduler.TryAcquire(First.Family));
    }

    [Fact]
    public void MissingAdapterRetainsItsLeaseAndReleaseIsIdempotent()
    {
        var scheduler = new FlowScheduler();
        EgressState first = new(First, 1, LinkHealth.Reachable);
        EgressState second = new(Second, 1, LinkHealth.Reachable);
        scheduler.Update([first, second]);
        using var original = scheduler.TryAcquire(First.Family);
        Assert.Equal(First, original?.Key);

        scheduler.Update([second]);
        Assert.Null(scheduler.TryAcquire(First.Family, Second.AdapterId));
        scheduler.Update([first, second]);
        using (var next = scheduler.TryAcquire(First.Family))
            Assert.Equal(Second, next?.Key);

        original!.Dispose();
        original.Dispose();
        using var restored = scheduler.TryAcquire(First.Family);
        using var balanced = scheduler.TryAcquire(First.Family);
        Assert.Equal(First, restored?.Key);
        Assert.Equal(Second, balanced?.Key);
    }

    [Fact]
    public void InvalidInventoryLeavesPreviousInventoryAvailable()
    {
        var scheduler = new FlowScheduler();
        scheduler.Update([new(First, 1, LinkHealth.Reachable)]);

        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Update([new(Second, 0, LinkHealth.Reachable)]));
        using var lease = scheduler.TryAcquire(First.Family);
        Assert.Equal(First, lease?.Key);
    }
}
