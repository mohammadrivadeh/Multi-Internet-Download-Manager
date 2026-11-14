using System.Net;
using System.Net.Sockets;

namespace DownloadManager.Infrastructure.Networking;

internal sealed class WindowsPathConnector(NetworkPath path, INetworkPathCatalog catalog)
{
    internal static int GetInterfaceOptionValue(AddressFamily family, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);
        return family switch
        {
            // Windows IP_UNICAST_IF takes a DWORD in network byte order.
            AddressFamily.InterNetwork => IPAddress.HostToNetworkOrder(index),
            AddressFamily.InterNetworkV6 => index,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
    }

    internal void EnsureCurrent()
    {
        if (!catalog.Discover().Contains(path))
            throw new InvalidOperationException("Selected path changed or is offline. Rediscover and create a new probe.");
    }

    internal async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Explicit interface binding requires Windows.");
        EnsureCurrent();
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        Exception? lastFailure = null;
        foreach (var destination in addresses.Where(a => a.AddressFamily == path.SourceAddress.AddressFamily).Take(16))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrent();
            var socket = new Socket(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                var level = destination.AddressFamily == AddressFamily.InterNetwork ? SocketOptionLevel.IP : SocketOptionLevel.IPv6;
                // Windows SDK: IP_UNICAST_IF and IPV6_UNICAST_IF are both 31.
                // .NET has no named enum member for this Windows-specific option.
                socket.SetSocketOption(level, (SocketOptionName)31,
                    GetInterfaceOptionValue(destination.AddressFamily, path.InterfaceIndex));
                socket.Bind(new IPEndPoint(path.SourceAddress, 0));
                await socket.ConnectAsync(new IPEndPoint(destination, context.DnsEndPoint.Port), cancellationToken);
                EnsureCurrent();
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastFailure = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("No destination reachable on the selected address family and interface.", lastFailure);
    }
}
