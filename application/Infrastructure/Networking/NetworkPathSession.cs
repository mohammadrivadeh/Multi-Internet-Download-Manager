using System.Net;
using System.Net.NetworkInformation;

namespace DownloadManager.Infrastructure.Networking;

/// <summary>Isolated allowed-path pool. Link Invalidated to every engine operation using Client.</summary>
public sealed class NetworkPathSession : IDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource invalidated = new();
    private readonly NetworkAddressChangedEventHandler changed;
    private readonly NetworkAvailabilityChangedEventHandler availability;
    private bool disposed;
    public HttpClient Client { get; }
    public CancellationToken Invalidated => invalidated.Token;

    public NetworkPathSession(NetworkPath path, INetworkPathCatalog catalog)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Selected interface transports require Windows.");
        var connector = new WindowsPathConnector(path, catalog);
        connector.EnsureCurrent();
        var sockets = PathProbe.CreateHandler(connector.ConnectAsync);
        sockets.MaxConnectionsPerServer = 128;
        Client = new HttpClient(new CurrentPathHandler(connector) { InnerHandler = sockets }) { Timeout = Timeout.InfiniteTimeSpan };
        changed = (_, _) => CheckPath(connector);
        availability = (_, _) => CheckPath(connector);
        NetworkChange.NetworkAddressChanged += changed;
        NetworkChange.NetworkAvailabilityChanged += availability;
    }

    private void CheckPath(WindowsPathConnector connector)
    {
        bool current;
        try { current = connector.IsCurrent(); }
        catch (NetworkInformationException) { return; } // A failed inventory does not establish that this adapter disappeared.
        if (current) return;
        lock (sync)
        {
            if (disposed || invalidated.IsCancellationRequested) return;
            invalidated.Cancel();
            Client.CancelPendingRequests();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            NetworkChange.NetworkAddressChanged -= changed;
            NetworkChange.NetworkAvailabilityChanged -= availability;
            Client.Dispose();
            invalidated.Dispose();
        }
    }

    private sealed class CurrentPathHandler(WindowsPathConnector connector) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            connector.EnsureCurrent();
            return base.SendAsync(request, cancellationToken);
        }
    }
}

public static class SystemRouteTransport
{
    /// <summary>Explicit opt-in to OS routing; this is not a bound interface transport.</summary>
    public static HttpClient Create() => new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(8), MaxConnectionsPerServer = 128,
        MaxResponseHeadersLength = 32
    }) { Timeout = Timeout.InfiniteTimeSpan };
}
