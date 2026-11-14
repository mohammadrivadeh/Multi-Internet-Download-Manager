using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Downloads;
using DownloadManager.Infrastructure.Http;

namespace DownloadManager.Tests;

public class RedirectTests
{
    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task ResolvesRelativeAndCrossOriginRedirectsWithoutForwardingCredentials(int status)
    {
        var calls = 0;
        using var handler = new CallbackHandler(request =>
        {
            calls++;
            if (calls == 1) return Redirect((HttpStatusCode)status, "../step?token=one");
            Assert.Equal("bytes=4-11", request.Headers.Range?.ToString());
            Assert.Equal("\"v1\"", request.Headers.IfRange?.ToString());
            Assert.Contains("identity", request.Headers.AcceptEncoding.Select(v => v.Value));
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("X-Secret"));
            Assert.Equal(HttpVersion.Version11, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            if (calls == 2)
            {
                Assert.Equal(new Uri("https://start.test/step?token=one"), request.RequestUri);
                return Redirect(HttpStatusCode.Found, "https://cdn.test/final");
            }
            Assert.Equal(new Uri("https://cdn.test/final"), request.RequestUri);
            return new(HttpStatusCode.OK) { Content = new StringContent("done") };
        });
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://start.test/path/file")
        { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.Range = new(4, 11); request.Headers.IfRange = new(new EntityTagHeaderValue("\"v1\""));
        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.Authorization = new("Bearer", "secret");
        request.Headers.TryAddWithoutValidation("Cookie", "secret=one");
        request.Headers.TryAddWithoutValidation("X-Secret", "secret");
        using var response = await HttpTransferPolicy.SendAsync(client, request, CancellationToken.None);
        Assert.Equal(new Uri("https://cdn.test/final"), HttpTransferPolicy.ResponseUri(response));
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("http://start.test/unsafe")]
    [InlineData("ftp://start.test/file")]
    [InlineData("file:///tmp/file")]
    [InlineData("https://user:secret@cdn.test/file")]
    [InlineData("#fragment-only-loop")]
    public async Task RejectsDowngradesInvalidSchemesEmbeddedCredentialsAndLoops(string location)
    {
        var calls = 0;
        using var client = new HttpClient(new CallbackHandler(_ => { calls++; return Redirect(HttpStatusCode.Found, location); }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://start.test/file");
        await Assert.ThrowsAsync<InvalidDataException>(() => HttpTransferPolicy.SendAsync(client, request, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EnforcesHopLimitAndMissingLocationAndDisposesIntermediateBodies()
    {
        var bodies = new List<TrackingContent>();
        using var client = new HttpClient(new CallbackHandler(_ =>
        {
            var body = new TrackingContent(); bodies.Add(body);
            var result = Redirect(HttpStatusCode.Found, $"/hop/{bodies.Count}"); result.Content = body; return result;
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://start.test/file");
        await Assert.ThrowsAsync<InvalidDataException>(() => HttpTransferPolicy.SendAsync(client, request, CancellationToken.None));
        Assert.Equal(HttpTransferPolicy.MaximumRedirects + 1, bodies.Count);
        Assert.All(bodies, b => Assert.True(b.WasDisposed));
        using var missing = new HttpClient(new CallbackHandler(_ => new(HttpStatusCode.Found)));
        using var another = new HttpRequestMessage(HttpMethod.Get, "http://start.test/file");
        await Assert.ThrowsAsync<InvalidDataException>(() => HttpTransferPolicy.SendAsync(missing, another, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsDefaultHeadersAndUnexpectedAutomaticRedirects()
    {
        using var client = new HttpClient(new CallbackHandler(_ => throw new InvalidOperationException("Must not be sent")));
        client.DefaultRequestHeaders.Authorization = new("Bearer", "secret");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://start.test/file");
        await Assert.ThrowsAsync<InvalidOperationException>(() => HttpTransferPolicy.SendAsync(client, request, CancellationToken.None));
        using var automatic = new HttpClient(new CallbackHandler(_ => new(HttpStatusCode.OK)
        { RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://other.test/file") }));
        using var otherRequest = new HttpRequestMessage(HttpMethod.Get, "https://start.test/file");
        await Assert.ThrowsAsync<InvalidDataException>(() => HttpTransferPolicy.SendAsync(automatic, otherRequest, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompleteDownloadFollowsRedirectsForRangedAndSequentialRepresentations(bool ranged)
    {
        var data = Enumerable.Range(0, 32768).Select(i => (byte)(i % 251)).ToArray();
        using var client = new HttpClient(new CallbackHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/start") return Redirect(HttpStatusCode.Found, "/final");
            return Fixture(request, data, ranged);
        }));
        await InDirectoryAsync(async target =>
        {
            await new DownloadEngine().DownloadAsync(new("http://fixture.test/start"), target, [client], Options(data));
            Assert.Equal(data, await File.ReadAllBytesAsync(target));
        });
    }

    [Fact]
    public async Task RedirectTargetChangesAreRejectedEvenWhenSizeAndEtagMatch()
    {
        var data = new byte[32768];
        var targetPath = "/first";
        using var client = new HttpClient(new CallbackHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/start")
            {
                var to = targetPath;
                targetPath = "/different";
                return Redirect(HttpStatusCode.Found, to);
            }
            return Fixture(request, data, true);
        }));
        await InDirectoryAsync(async target =>
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new DownloadEngine().DownloadAsync(new("http://fixture.test/start"), target, [client], Options(data)));
            Assert.False(File.Exists(target));
            Assert.Equal(0, new FileInfo(target + ".part").Length);
        });
    }

    [Fact]
    public async Task ResumeRequiresTheSameResolvedResourceAndCannotMixEtagsFromDifferentUris()
    {
        var data = new byte[32768];
        var targetPath = "/first";
        using var client = new HttpClient(new CallbackHandler(request => request.RequestUri!.AbsolutePath == "/start"
            ? Redirect(HttpStatusCode.Found, targetPath) : Fixture(request, data, true)));
        await InDirectoryAsync(async target =>
        {
            using var stop = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DownloadEngine().DownloadAsync(new("http://fixture.test/start"), target, [client], Options(data) with { Workers = 1 },
                new InlineProgress(p => { if (p.Stage == DownloadStage.Downloading && p.CompletedBytes > 0) stop.Cancel(); }), stop.Token));
            var bytes = await File.ReadAllBytesAsync(target + ".part");
            targetPath = "/second";
            await Assert.ThrowsAsync<InvalidDataException>(() => new DownloadEngine().DownloadAsync(new("http://fixture.test/start"), target, [client], Options(data)));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(target + ".part"));
            Assert.False(File.Exists(target));
        });
    }

    [Fact]
    public async Task CancellationStopsAnInProgressRedirectChain()
    {
        using var stop = new CancellationTokenSource();
        using var body = new TrackingContent();
        using var client = new HttpClient(new StalledRedirectHandler(body));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://start.test/file");
        var pending = HttpTransferPolicy.SendAsync(client, request, stop.Token);
        Assert.False(pending.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(body.WasDisposed);
    }

    [Fact]
    public async Task PathsResolvingToDifferentUrisAreRejectedEvenWithIdenticalEtags()
    {
        var data = new byte[32768];
        HttpClient Client(string final) => new(new CallbackHandler(request => request.RequestUri!.AbsolutePath == "/start"
            ? Redirect(HttpStatusCode.Found, final) : Fixture(request, data, true)));
        using var first = Client("/one"); using var second = Client("/two");
        await InDirectoryAsync(async target =>
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new DownloadEngine().DownloadAsync(new("http://fixture.test/start"), target, [first, second], Options(data)));
            Assert.False(File.Exists(target + ".part"));
        });
    }

    private sealed class StalledRedirectHandler(TrackingContent body) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/file")
            { var response = Redirect(HttpStatusCode.Found, "/wait"); response.Content = body; return response; }
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private static DownloadOptions Options(byte[] data) => new() { Workers = 2, MinimumRangeBytes = 4096, MaximumRangeBytes = 8192,
        ExpectedSha256 = Convert.ToHexString(SHA256.HashData(data)), RetryLimit = 0 };
    private static HttpResponseMessage Fixture(HttpRequestMessage request, byte[] data, bool ranged)
    {
        var selected = ranged ? request.Headers.Range?.Ranges.Single() : null;
        var from = (int)(selected?.From ?? 0); var to = (int)(selected?.To ?? data.Length - 1);
        var result = new HttpResponseMessage(selected is not null ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        { Content = new ByteArrayContent(data.AsSpan(from, to - from + 1).ToArray()) };
        result.Headers.ETag = new("\"same-tag\"");
        if (selected is not null) result.Content.Headers.ContentRange = new(from, to, data.Length);
        return result;
    }
    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var result = new HttpResponseMessage(status);
        result.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute); return result;
    }
    private static async Task InDirectoryAsync(Func<string, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { await test(Path.Combine(directory, "output.bin")); }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(send(request)); } }
    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }
    private sealed class TrackingContent() : ByteArrayContent([1, 2, 3])
    { public bool WasDisposed { get; private set; } protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); } }
}
