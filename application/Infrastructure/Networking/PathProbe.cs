using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace DownloadManager.Infrastructure.Networking;

public sealed record ProbeResult(int StatusCode, string HttpVersion, int SampleBytes, string SampleSha256, bool ReachedSampleLimit);

/// <summary>One bounded request using a fresh isolated pool. Does not implement a downloader.</summary>
public static class PathProbe
{
    public const int SampleLimit = 65536;

    public static async Task<ProbeResult> RunAsync(NetworkPath path, Uri destination, INetworkPathCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(catalog);
        ValidateDestination(destination);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Run the bound probe on Windows.");
        var connector = new WindowsPathConnector(path, catalog);
        connector.EnsureCurrent();
        using var handler = CreateHandler(connector.ConnectAsync);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        // Cancel this probe on any network change, even an unrelated adapter change.
        // Every subsequent invocation discovers a new snapshot and uses a new pool.
        var eventLock = new object();
        var finished = false;
        System.Net.NetworkInformation.NetworkAddressChangedEventHandler changed = (_, _) =>
        {
            lock (eventLock)
                if (!finished) deadline.Cancel();
        };
        System.Net.NetworkInformation.NetworkAvailabilityChangedEventHandler availability = (_, _) => changed(null, EventArgs.Empty);
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += changed;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += availability;
        try
        {
            connector.EnsureCurrent();
            return await ExecuteAsync(client, destination, deadline.Token);
        }
        finally
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= changed;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= availability;
            lock (eventLock) finished = true;
        }
    }

    internal static void ValidateDestination(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.IsAbsoluteUri || destination.Scheme is not ("http" or "https") || destination.UserInfo.Length != 0)
            throw new ArgumentException("Use an absolute HTTP(S) URL without embedded credentials.", nameof(destination));
    }

    internal static SocketsHttpHandler CreateHandler(Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> connect) => new()
    {
        ConnectCallback = connect,
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        MaxConnectionsPerServer = 1,
        MaxResponseHeadersLength = 32
    };

    internal static async Task<ProbeResult> ExecuteAsync(HttpClient client, Uri destination, CancellationToken cancellationToken)
    {
        ValidateDestination(destination);
        using var request = new HttpRequestMessage(HttpMethod.Get, destination)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.Range = new RangeHeaderValue(0, SampleLimit - 1);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        var total = 0;
        while (total < SampleLimit)
        {
            var count = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, SampleLimit - total)), cancellationToken);
            if (count == 0) break;
            hash.AppendData(buffer, 0, count);
            total += count;
        }
        return new((int)response.StatusCode, response.Version.ToString(), total, Convert.ToHexString(hash.GetHashAndReset()), total == SampleLimit);
    }
}
