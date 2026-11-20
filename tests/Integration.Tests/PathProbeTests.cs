using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DownloadManager.Infrastructure.Networking;

namespace DownloadManager.Tests;

public class PathProbeTests
{
    [Theory]
    [InlineData("file:///tmp/test")]
    [InlineData("ftp://example.com/test")]
    [InlineData("https://user:secret@example.com/test")]
    [InlineData("relative")]
    public void RejectsUnsupportedOrCredentialBearingDestinations(string url) =>
        Assert.Throws<ArgumentException>(() => PathProbe.ValidateDestination(new Uri(url, UriKind.RelativeOrAbsolute)));

    [Fact]
    public void WindowsInterfaceOptionUsesFamilySpecificByteOrder()
    {
        Assert.Equal(IPAddress.HostToNetworkOrder(42), WindowsPathConnector.GetInterfaceOptionValue(AddressFamily.InterNetwork, 42));
        Assert.Equal(42, WindowsPathConnector.GetInterfaceOptionValue(AddressFamily.InterNetworkV6, 42));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPathConnector.GetInterfaceOptionValue(AddressFamily.Unix, 42));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPathConnector.GetInterfaceOptionValue(AddressFamily.InterNetwork, 0));
    }

    [Fact]
    public void ChangedOrMissingSnapshotIsRejectedBeforeConnecting()
    {
        var path = new NetworkPath("adapter", "test", IPAddress.Parse("192.0.2.1"), 42, "old");
        var catalog = new FakeCatalog([path]);
        var connector = new WindowsPathConnector(path, catalog);
        connector.EnsureCurrent();
        catalog.Paths = [new("adapter", "test", IPAddress.Parse("192.0.2.1"), 42, "new")];
        Assert.Throws<NetworkPathChangedException>(connector.EnsureCurrent);
        catalog.Paths = [];
        Assert.Throws<NetworkPathChangedException>(connector.EnsureCurrent);
    }

    [Fact]
    public void PathOwnsSourceAddressAndRejectsWildcard()
    {
        var address = IPAddress.Parse("fe80::1%4");
        var path = new NetworkPath("adapter", "test", address, 4, "generation");
        address.ScopeId = 5;
        path.SourceAddress.ScopeId = 6;
        Assert.Equal(4, path.SourceAddress.ScopeId);
        Assert.Throws<ArgumentException>(() => new NetworkPath("adapter", "test", IPAddress.Any, 4, "generation"));
    }

    [Theory]
    [InlineData(200, 100000)]
    [InlineData(206, 7)]
    [InlineData(302, 0)]
    public async Task RealHttpExchangeIsBoundedAndDoesNotFollowRedirects(int status, int length)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var payload = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
        var server = ServeAsync();
        var connections = 0;
        using var handler = PathProbe.CreateHandler(async (_, token) =>
        {
            Interlocked.Increment(ref connections);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(endpoint, token); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        });
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        using var client = new HttpClient(handler);
        // The test connector routes only to loopback. It does not test Windows routing.
        var result = await PathProbe.ExecuteAsync(client, new Uri("http://fixture.invalid/fixture"), deadline.Token);
        var request = await server;
        Assert.Contains("GET /fixture HTTP/1.1", request);
        Assert.Contains("Range: bytes=0-65535", request);
        Assert.Contains("Accept-Encoding: identity", request);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(Math.Min(length, PathProbe.SampleLimit), result.SampleBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload.AsSpan(0, result.SampleBytes))), result.SampleSha256);
        Assert.Equal(length >= PathProbe.SampleLimit, result.ReachedSampleLimit);
        Assert.Equal(1, connections);

        async Task<string> ServeAsync()
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            var lines = new List<string>();
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } line) lines.Add(line);
            var headers = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: {length}\r\nLocation: http://redirect.invalid/\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, deadline.Token);
            await stream.WriteAsync(payload, deadline.Token);
            return string.Join('\n', lines);
        }
    }

    [Fact]
    public async Task CancellationInterruptsAPendingConnection()
    {
        using var deadline = new CancellationTokenSource();
        using var handler = PathProbe.CreateHandler(async (_, token) =>
        {
            deadline.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PathProbe.ExecuteAsync(client, new Uri("http://fixture.invalid/"), deadline.Token));
    }

    [Fact]
    public async Task TruncatedBodyIsAnErrorRatherThanASuccessfulSample()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = ServeAsync();
        using var handler = PathProbe.CreateHandler(async (_, token) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<IOException>(() => PathProbe.ExecuteAsync(client, new Uri("http://fixture.invalid/"), deadline.Token));
        await server;

        async Task ServeAsync()
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 10\r\nConnection: close\r\n\r\nabc"), deadline.Token);
        }
    }

    private sealed class FakeCatalog(IReadOnlyList<NetworkPath> paths) : INetworkPathCatalog
    {
        public IReadOnlyList<NetworkPath> Paths { get; set; } = paths;
        public IReadOnlyList<NetworkPath> Discover() => Paths;
    }
}
