namespace DownloadManager.Core;

/// <summary>A nonempty half-open byte interval [Start, EndExclusive).</summary>
public sealed record ByteRange
{
    public long Start { get; }
    public long EndExclusive { get; }
    public long Length => EndExclusive - Start;

    public ByteRange(long start, long endExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (endExclusive <= start)
            throw new ArgumentOutOfRangeException(nameof(endExclusive), "The end must exceed the start.");
        Start = start;
        EndExclusive = endExclusive;
    }

    /// <summary>Inclusive endpoints for the HTTP Range header.</summary>
    public (long From, long To) ToHttpRange() => (Start, EndExclusive - 1);

    public (ByteRange Prefix, ByteRange Tail) Split(long offset)
    {
        if (offset <= Start || offset >= EndExclusive)
            throw new ArgumentOutOfRangeException(nameof(offset), "Split must be strictly inside the range.");
        return (new(Start, offset), new(offset, EndExclusive));
    }

    public bool Overlaps(ByteRange other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start < other.EndExclusive && other.Start < EndExclusive;
    }
}
