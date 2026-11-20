using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DownloadManager.NetworkBalancer;

public sealed record DetectedAdapter(
    string Name,
    int Index,
    string IPv4,
    string IPv6,
    string Reason)
{
    public string DiscoveryStatus => string.IsNullOrEmpty(Reason) ? "Eligible" : Reason;
}

public static class AdapterDiagnostics
{
    public static Task<IReadOnlyList<DetectedAdapter>> ScanAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DetectedAdapter>>(() => ScanManaged(), cancellationToken);

    private static DetectedAdapter[] ScanManaged() => NetworkInterface.GetAllNetworkInterfaces().Select(ScanAdapter).ToArray();

    private static DetectedAdapter ScanAdapter(NetworkInterface adapter)
    {
        try { return ReadAdapter(adapter); }
        catch (NetworkInformationException)
        {
            return new DetectedAdapter(adapter.Name, 0, "", "", "Adapter configuration unavailable");
        }
    }

    private static DetectedAdapter ReadAdapter(NetworkInterface adapter)
    {
        var properties = adapter.GetIPProperties();
        var addresses = properties.UnicastAddresses.Select(entry => entry.Address).Where(address =>
            !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal
            && !(address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, _, _])).ToArray();
        var gateways = properties.GatewayAddresses.Select(item => item.Address).ToArray();
        var ipv4 = gateways.Any(address => address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any))
            ? addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork) : null;
        var ipv6 = gateways.Any(address => address.AddressFamily == AddressFamily.InterNetworkV6 && !address.Equals(IPAddress.IPv6Any))
            ? addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6UniqueLocal) : null;
        var index = TryReadIndex(adapter, properties, NetworkInterfaceComponent.IPv4)
            ?? TryReadIndex(adapter, properties, NetworkInterfaceComponent.IPv6) ?? 0;
        var reason = adapter.OperationalStatus != OperationalStatus.Up ? "Disconnected"
            : adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ? "Loopback or tunnel"
            : adapter.Name == "Multi-Internet Balancer" ? "Capture adapter"
            : ipv4 is null && ipv6 is null ? "No usable IP address with a default gateway"
            : index <= 0 ? "Interface index unavailable" : "";
        return new DetectedAdapter(adapter.Name, index, ipv4?.ToString() ?? "", ipv6?.ToString() ?? "", reason);
    }

    private static int? TryReadIndex(NetworkInterface adapter, IPInterfaceProperties properties, NetworkInterfaceComponent component)
    {
        if (!adapter.Supports(component)) return null;
        try
        {
            var index = component == NetworkInterfaceComponent.IPv4
                ? properties.GetIPv4Properties()?.Index
                : properties.GetIPv6Properties()?.Index;
            return index is > 0 ? index : null;
        }
        catch (NetworkInformationException)
        {
            // The protocol can become unavailable while Windows is updating the adapter.
            return null;
        }
    }

}
