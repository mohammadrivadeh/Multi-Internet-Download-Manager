using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using DownloadManager.Infrastructure.Diagnostics;

namespace DownloadManager.Infrastructure.Networking;

public sealed record AdapterSample(TimeSpan Latency, double? MegabitsPerSecond, int Bytes)
{
    public const int SampleSize = 1024 * 1024;
    private static readonly Uri LatencyUrl = new("https://speed.cloudflare.com/__down?bytes=0");
    private static readonly Uri SampleUrl = new($"https://speed.cloudflare.com/__down?bytes={SampleSize}");

    public static async Task<AdapterSample?> MeasureAsync(NetworkPath path, INetworkPathCatalog catalog, CancellationToken cancellationToken)
    {
        TimeSpan? latency = null;
        var route = $"interface={path.InterfaceIndex} family={path.SourceAddress.AddressFamily}";
        LocalDiagnostics.Trace("adapter-test-start", route);
        try
        {
            var connector = new WindowsPathConnector(path, catalog);
            using var client = new HttpClient(PathProbe.CreateHandler(connector.ConnectAsync)) { Timeout = Timeout.InfiniteTimeSpan };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var started = Stopwatch.GetTimestamp();
            using (var response = await SendAsync(client, LatencyUrl, deadline.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                latency = Stopwatch.GetElapsedTime(started);
                LocalDiagnostics.Trace("adapter-https-response", $"{route} status={(int)response.StatusCode} latency_ms={latency.Value.TotalMilliseconds:F0}");
            }

            connector.EnsureCurrent();
            using var sample = await SendAsync(client, SampleUrl, deadline.Token).ConfigureAwait(false);
            sample.EnsureSuccessStatusCode();
            if (sample.Content.Headers.ContentEncoding.Any(encoding => encoding != "identity"))
                return new(latency.Value, null, 0);
            await using var stream = await sample.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var buffer = new byte[32768];
            var bytes = 0;
            started = Stopwatch.GetTimestamp();
            while (bytes < SampleSize)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, SampleSize - bytes)), deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                bytes += read;
            }
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            LocalDiagnostics.Trace("adapter-speed-result", $"{route} status={(int)sample.StatusCode} bytes={bytes} seconds={elapsed:F3}");
            return new(latency.Value, bytes == SampleSize && elapsed > 0 ? bytes * 8 / elapsed / 1_000_000 : null, bytes);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException
            or System.Net.Sockets.SocketException or System.Net.NetworkInformation.NetworkInformationException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalDiagnostics.Record("adapter-test", error,
                $"interface={path.InterfaceIndex} family={path.SourceAddress.AddressFamily} phase={(latency is null ? "reachability" : "speed")}");
            return latency is { } measured ? new(measured, null, 0) : null;
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Uri endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.AcceptEncoding.ParseAdd("identity");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }
}
