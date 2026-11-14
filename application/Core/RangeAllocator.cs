namespace DownloadManager.Core;

/// <summary>Single-owner pending range pool. Access must be serialized by the caller.</summary>
public sealed class RangeAllocator
{
    private readonly Queue<ByteRange> pending = new();

    public RangeAllocator(long length, IEnumerable<ByteRange> completed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        long cursor = 0;
        foreach (var range in completed.OrderBy(r => r.Start))
        {
            if (range.Start < cursor || range.EndExclusive > length)
                throw new ArgumentException("Completed ranges overlap or exceed the resource.", nameof(completed));
            if (range.Start > cursor) pending.Enqueue(new(cursor, range.Start));
            cursor = range.EndExclusive;
        }
        if (cursor < length) pending.Enqueue(new(cursor, length));
    }

    public ByteRange? Take(long targetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetBytes);
        if (!pending.TryDequeue(out var range)) return null;
        if (range.Length <= targetBytes) return range;
        var (head, tail) = range.Split(range.Start + targetBytes);
        // Put the remaining extent at the back: all allocations remain disjoint.
        pending.Enqueue(tail);
        return head;
    }
}
