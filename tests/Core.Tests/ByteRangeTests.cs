using DownloadManager.Core;

namespace DownloadManager.Tests;

public class ByteRangeTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(10, 9)]
    [InlineData(0, -1)]
    public void RejectsInvalidIntervals(long start, long end) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ByteRange(start, end));

    [Fact]
    public void SingleByteHasInclusiveHttpEndpoints() =>
        Assert.Equal((42L, 42L), new ByteRange(42, 43).ToHttpRange());

    [Fact]
    public void SupportsOffsetsBeyondFourGiBAndLargestEnd()
    {
        var range = new ByteRange(1L << 33, long.MaxValue);
        Assert.Equal(long.MaxValue - (1L << 33), range.Length);
        Assert.Equal((1L << 33, long.MaxValue - 1), range.ToHttpRange());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(-1)]
    [InlineData(11)]
    public void RejectsSplitsThatCreateEmptyOrExternalRanges(long offset) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ByteRange(0, 10).Split(offset));

    [Fact]
    public void SplittingPreservesExactCoverageWithoutOverlap()
    {
        var random = new Random(1701);
        for (var i = 0; i < 2000; i++)
        {
            var start = random.NextInt64(0, long.MaxValue - 1000000);
            var length = random.NextInt64(2, 1000000);
            var range = new ByteRange(start, start + length);
            var (prefix, tail) = range.Split(start + random.NextInt64(1, length));
            Assert.Equal(range.Start, prefix.Start);
            Assert.Equal(prefix.EndExclusive, tail.Start);
            Assert.Equal(range.EndExclusive, tail.EndExclusive);
            Assert.Equal(range.Length, prefix.Length + tail.Length);
            Assert.False(prefix.Overlaps(tail));
            Assert.True(range.Overlaps(prefix));
            Assert.True(range.Overlaps(tail));
        }
    }

    [Fact]
    public void DistinguishesOverlapFromAdjacencyAndSeparation()
    {
        var range = new ByteRange(10, 20);
        Assert.True(range.Overlaps(new(19, 21)));
        Assert.True(range.Overlaps(new(0, 30)));
        Assert.False(range.Overlaps(new(20, 30)));
        Assert.False(range.Overlaps(new(0, 10)));
        Assert.False(range.Overlaps(new(30, 40)));
    }
}
