using System.Net.Sockets;

namespace DownloadManager.NetworkBalancer;

public enum LinkHealth
{
    Unknown,
    Reachable,
    Unreachable
}

public readonly record struct EgressKey(Guid AdapterId, AddressFamily Family);

public sealed record EgressState(EgressKey Key, int Weight, LinkHealth Health);

/// <summary>Assigns new flows by address family using weighted least-open-flows.</summary>
public sealed class FlowScheduler
{
    private readonly object gate = new();
    private readonly Dictionary<EgressKey, Slot> slots = new();

    /// <summary>Replaces the eligible inventory while retaining existing reservations.</summary>
    public void Update(IEnumerable<EgressState> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var incoming = new Dictionary<EgressKey, EgressState>();
        foreach (var path in paths)
        {
            ArgumentNullException.ThrowIfNull(path);
            ValidateFamily(path.Key.Family);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(path.Weight);
            if (!Enum.IsDefined(path.Health))
                throw new ArgumentOutOfRangeException(nameof(paths), "Invalid link health.");
            incoming.Add(path.Key, path);
        }

        lock (gate)
        {
            // Keep absent slots only while a lease is outstanding, so a returning adapter
            // retains its load even if one or more scans missed it.
            foreach (var (key, slot) in slots)
                slot.IsPresent = incoming.ContainsKey(key);

            foreach (var (key, state) in incoming)
            {
                if (!slots.TryGetValue(key, out var slot))
                {
                    slot = new Slot(state);
                    slots.Add(key, slot);
                }
                slot.State = state;
                slot.IsPresent = true;
            }

            foreach (var key in slots.Where(entry => !entry.Value.IsPresent && entry.Value.Active == 0)
                         .Select(entry => entry.Key).ToArray())
                slots.Remove(key);
        }
    }

    /// <summary>Reserves an egress for the lifetime of a flow, including its connect attempt.</summary>
    public FlowLease? TryAcquire(AddressFamily family, Guid? excludedAdapter = null)
    {
        ValidateFamily(family);
        lock (gate)
        {
            Slot? best = null;
            foreach (var slot in slots.Values)
            {
                var state = slot.State;
                if (!slot.IsPresent || state.Key.Family != family || state.Key.AdapterId == excludedAdapter ||
                    state.Health == LinkHealth.Unreachable)
                    continue;

                if (best is null || IsPreferred(slot, best))
                    best = slot;
            }

            if (best is null)
                return null;

            checked { best.Active++; }
            return new FlowLease(best.State.Key, () => Release(best));
        }
    }

    private static bool IsPreferred(Slot candidate, Slot current)
    {
        if (candidate.State.Health != current.State.Health)
            return candidate.State.Health == LinkHealth.Reachable;

        // Compare (active + 1) / weight without floating-point tie rounding.
        var candidateLoad = (candidate.Active + 1L) * current.State.Weight;
        var currentLoad = (current.Active + 1L) * candidate.State.Weight;
        return candidateLoad < currentLoad ||
               (candidateLoad == currentLoad &&
                candidate.State.Key.AdapterId.CompareTo(current.State.Key.AdapterId) < 0);
    }

    private void Release(Slot slot)
    {
        lock (gate)
        {
            slot.Active--;
            if (!slot.IsPresent && slot.Active == 0)
                slots.Remove(slot.State.Key);
        }
    }

    private static void ValidateFamily(AddressFamily family)
    {
        if (family is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentOutOfRangeException(nameof(family));
    }

    private sealed class Slot(EgressState state)
    {
        public EgressState State { get; set; } = state;
        public bool IsPresent { get; set; }
        public int Active { get; set; }
    }
}

public sealed class FlowLease : IDisposable
{
    private Action? release;

    internal FlowLease(EgressKey key, Action release)
    {
        Key = key;
        this.release = release;
    }

    public EgressKey Key { get; }

    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}
