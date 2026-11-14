using System.Net;
using System.Net.Sockets;
using System.Text;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Http;
using DownloadManager.Infrastructure.Networking;

namespace DownloadManager.Tests;

public class RangeTransferTests
{
    [Fact]
    public async Task WritesOnlyTheRequestedExtentAndSendsIdentityValidator()
    {
        await WithServerAsync("206 Partial Content", ValidHeaders + "Content-Length: 8\r\n", "abcdefgh", async (client, file, request, token) =>
        {
            Assert.Equal(8, await RangeTransfer.WriteAsync(client, new Uri("http://fixture.invalid/file"), new ByteRange(4, 12), 20, "\"v1\"", file.SafeFileHandle, token));
            var bytes = new byte[20];
            Assert.Equal(20, await RandomAccess.ReadAsync(file.SafeFileHandle, bytes, 0, token));
            Assert.Equal(Enumerable.Repeat((byte)0xcc, 4), bytes.Take(4));
            Assert.Equal("abcdefgh", Encoding.ASCII.GetString(bytes, 4, 8));
            Assert.Equal(Enumerable.Repeat((byte)0xcc, 8), bytes.Skip(12));
            var headers = await request;
            Assert.Contains("Range: bytes=4-11", headers);
            Assert.Contains("If-Range: \"v1\"", headers);
            Assert.Contains("Accept-Encoding: identity", headers);
        });
    }

    [Theory]
    [InlineData("200 OK", "Content-Length: 8\r\n")]
    [InlineData("416 Range Not Satisfiable", "Content-Length: 8\r\n")]
    [InlineData("429 Too Many Requests", "Content-Length: 8\r\n")]
    [InlineData("206 Partial Content", "ETag: \"v1\"\r\nContent-Length: 8\r\n")]
    [InlineData("206 Partial Content", "ETag: \"v2\"\r\nContent-Range: bytes 4-11/20\r\nContent-Length: 8\r\n")]
    [InlineData("206 Partial Content", "ETag: W/\"v1\"\r\nContent-Range: bytes 4-11/20\r\nContent-Length: 8\r\n")]
    [InlineData("206 Partial Content", "Content-Range: bytes 4-11/20\r\nContent-Length: 8\r\n")]
    [InlineData("206 Partial Content", "ETag: \"v1\"\r\nContent-Range: bytes 5-12/20\r\nContent-Length: 8\r\n")]
    [InlineData("206 Partial Content", "ETag: \"v1\"\r\nContent-Range: bytes 4-11/21\r\nContent-Length: 8\r\n")]
    [InlineData("206 Partial Content", "ETag: \"v1\"\r\nContent-Range: bytes 4-11/*\r\nContent-Length: 8\r\n")]
    [InlineData("206 Partial Content", "ETag: \"v1\"\r\nContent-Range: bytes 4-11/20\r\nContent-Length: 7\r\n")]
    [InlineData("206 Partial Content", "ETag: \"v1\"\r\nContent-Range: bytes 4-11/20\r\nContent-Encoding: gzip\r\nContent-Length: 8\r\n")]
    [InlineData("206 Partial Content", "ETag: \"v1\"\r\nContent-Range: bytes 4-11/20\r\nContent-Type: multipart/byteranges; boundary=x\r\nContent-Length: 8\r\n")]
    public async Task InvalidHeadersAreRejectedBeforeAnyFileWrite(string status, string headers)
    {
        await WithServerAsync(status, headers, "abcdefgh", async (client, file, _, token) =>
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => RangeTransfer.WriteAsync(client, new Uri("http://fixture.invalid/file"), new ByteRange(4, 12), 20, "\"v1\"", file.SafeFileHandle, token));
            var bytes = new byte[20];
            Assert.Equal(20, await RandomAccess.ReadAsync(file.SafeFileHandle, bytes, 0, token));
            Assert.All(bytes, b => Assert.Equal(0xcc, b));
        });
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("abcdefghij")]
    public async Task WrongBodyLengthNeverReportsSuccessOrWritesOutsideRange(string payload)
    {
        await WithServerAsync("206 Partial Content", ValidHeaders, payload, async (client, file, _, token) =>
        {
            var transfer = () => RangeTransfer.WriteAsync(client, new Uri("http://fixture.invalid/file"), new ByteRange(4, 12), 20, "\"v1\"", file.SafeFileHandle, token);
            if (payload.Length < 8) await Assert.ThrowsAsync<EndOfStreamException>(transfer);
            else await Assert.ThrowsAsync<InvalidDataException>(transfer);
            var bytes = new byte[20];
            await RandomAccess.ReadAsync(file.SafeFileHandle, bytes, 0, token);
            Assert.All(bytes.Take(4).Concat(bytes.Skip(12)), b => Assert.Equal(0xcc, b));
        });
    }

    [Theory]
    [InlineData("W/\"v1\"")]
    [InlineData("*")]
    [InlineData("malformed")]
    public async Task RejectsWeakOrWildcardIdentityBeforeNetworkUse(string etag)
    {
        var filename = Path.GetTempFileName();
        try
        {
            await using var file = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
            using var client = new HttpClient(new NeverSend());
            await Assert.ThrowsAsync<ArgumentException>(() => RangeTransfer.WriteAsync(client, new Uri("https://fixture.invalid/file"), new ByteRange(0, 1), 1, etag, file.SafeFileHandle, CancellationToken.None));
        }
        finally { File.Delete(filename); }
    }

    [Fact]
    public async Task WritesAtAnOffsetBeyondFourGiBWithoutWholeFileBuffering()
    {
        const long start = (1L << 32) + 17;
        var filename = Path.GetTempFileName();
        try
        {
            await using var file = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
            using var client = new HttpClient(new LargeOffsetResponse(start));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Equal(3, await RangeTransfer.WriteAsync(client, new Uri("http://fixture.invalid/file"), new ByteRange(start, start + 3), start + 3, "\"v1\"", file.SafeFileHandle, deadline.Token));
            Assert.Equal(start + 3, RandomAccess.GetLength(file.SafeFileHandle));
            var bytes = new byte[3];
            Assert.Equal(3, await RandomAccess.ReadAsync(file.SafeFileHandle, bytes, start, deadline.Token));
            Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        }
        finally { File.Delete(filename); }
    }

    private sealed class LargeOffsetResponse(long start) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Headers.ETag = new("\"v1\"");
            response.Content.Headers.ContentRange = new(start, start + 2, start + 3);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task CancellationStopsAStalledBodyWithoutReportingCompletion()
    {
        await WithServerAsync("206 Partial Content", ValidHeaders + "Content-Length: 8\r\n", "abc", async (client, file, request, token) =>
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var transfer = RangeTransfer.WriteAsync(client, new Uri("http://fixture.invalid/file"), new ByteRange(4, 12), 20, "\"v1\"", file.SafeFileHandle, stop.Token);
            await request.WaitAsync(token);
            stop.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
            var bytes = new byte[20];
            await RandomAccess.ReadAsync(file.SafeFileHandle, bytes, 0, token);
            Assert.All(bytes.Take(4).Concat(bytes.Skip(12)), b => Assert.Equal(0xcc, b));
        }, stall: true);
    }

    private const string ValidHeaders = "ETag: \"v1\"\r\nContent-Range: bytes 4-11/20\r\n";

    private static async Task WithServerAsync(string status, string headers, string payload,
        Func<HttpClient, FileStream, Task<string>, CancellationToken, Task> test, bool stall = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeAsync();
        using var handler = PathProbe.CreateHandler(async (_, token) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        });
        using var client = new HttpClient(handler);
        var filename = Path.GetTempFileName();
        try
        {
            await using var file = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
            await RandomAccess.WriteAsync(file.SafeFileHandle, Enumerable.Repeat((byte)0xcc, 20).ToArray(), 0, deadline.Token);
            await test(client, file, received.Task, deadline.Token);
            if (!stall) await server;
        }
        finally
        {
            deadline.Cancel();
            listener.Stop();
            try { await server; } catch (OperationCanceledException) { }
            File.Delete(filename);
        }

        async Task ServeAsync()
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            var lines = new List<string>();
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } line) lines.Add(line);
            received.SetResult(string.Join('\n', lines));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{headers}Connection: close\r\n\r\n{payload}"), deadline.Token);
            if (stall) await Task.Delay(Timeout.Infinite, deadline.Token);
        }
    }

    private sealed class NeverSend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid identity must not reach HTTP.");
    }
}
