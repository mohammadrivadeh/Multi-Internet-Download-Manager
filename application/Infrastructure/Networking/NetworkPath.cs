using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DownloadManager.Infrastructure.Networking;

/// <summary>A selected adapter/address snapshot, not a promise of internet access.</summary>
public sealed record NetworkPath
{
    public string AdapterId { get; }
    public string Name { get; }
    private readonly IPAddress sourceAddress;
    public IPAddress SourceAddress => IPAddress.Parse(sourceAddress.ToString());
    public int InterfaceIndex { get; }
    public string Generation { get; }

    public NetworkPath(string adapterId, string name, IPAddress sourceAddress, int interfaceIndex, string generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(sourceAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interfaceIndex);
        if (sourceAddress.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            || sourceAddress.Equals(IPAddress.Any) || sourceAddress.Equals(IPAddress.IPv6Any)
            || sourceAddress.IsIPv6Multicast || sourceAddress.GetAddressBytes()[0] is >= 224 and <= 239 && sourceAddress.AddressFamily == AddressFamily.InterNetwork)
            throw new ArgumentException("A concrete unicast source address is required.", nameof(sourceAddress));
        AdapterId = adapterId;
        Name = name;
        // Own a copy: IPAddress.ScopeId can be mutated by a caller.
        this.sourceAddress = IPAddress.Parse(sourceAddress.ToString());
        InterfaceIndex = interfaceIndex;
        Generation = generation;
    }
}

public interface INetworkPathCatalog
{
    IReadOnlyList<NetworkPath> Discover();
}

public sealed class NetworkPathCatalog : INetworkPathCatalog
{
    public IReadOnlyList<NetworkPath> Discover()
    {
        var paths = new List<NetworkPath>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var properties = adapter.GetIPProperties();
            var ipv4 = adapter.Supports(NetworkInterfaceComponent.IPv4) ? properties.GetIPv4Properties()?.Index : null;
            var ipv6 = adapter.Supports(NetworkInterfaceComponent.IPv6) ? properties.GetIPv6Properties()?.Index : null;
            var addresses = properties.UnicastAddresses.Select(a => a.Address).OrderBy(a => a.ToString(), StringComparer.Ordinal).ToArray();
            // An immutable configuration fingerprint; not a monotonic reconnect counter.
            var generation = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{adapter.Id}|{ipv4}|{ipv6}|{string.Join(',', addresses.Select(a => a.ToString()))}")));
            foreach (var address in addresses)
            {
                var index = address.AddressFamily == AddressFamily.InterNetwork ? ipv4 : ipv6;
                if (index is > 0 && !IPAddress.IsLoopback(address))
                    paths.Add(new(adapter.Id, adapter.Name, address, index.Value, generation));
            }
        }
        return paths.AsReadOnly();
    }
}
