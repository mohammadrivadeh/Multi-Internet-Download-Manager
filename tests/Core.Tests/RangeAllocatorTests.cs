using DownloadManager.Core;
namespace DownloadManager.Tests;
public class RangeAllocatorTests
{
    [Fact]
    public void AllocationsCoverOnlyMissingBytesWithoutOverlap()
    {
        var random = new Random(42);
        var pool = new RangeAllocator(10000, [new(100, 200), new(500, 1000)]);
        var bytes = new HashSet<long>();
        ByteRange? next;
        while ((next = pool.Take(random.Next(1, 300))) is not null)
            for (var i = next.Start; i < next.EndExclusive; i++) Assert.True(bytes.Add(i));
        Assert.Equal(9400, bytes.Count);
        Assert.DoesNotContain(bytes, i => i is >= 100 and < 200 or >= 500 and < 1000);
    }
    [Fact]
    public void RejectsOverlappingAndOutOfBoundsRecovery()
    {
        Assert.Throws<ArgumentException>(() => new RangeAllocator(100, [new(0, 60), new(50, 80)]));
        Assert.Throws<ArgumentException>(() => new RangeAllocator(100, [new(0, 101)]));
        Assert.Null(new RangeAllocator(0, []).Take(1));
    }
}
