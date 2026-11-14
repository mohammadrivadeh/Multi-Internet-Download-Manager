using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Downloads;

namespace DownloadManager.Tests;

public class DownloadEngineTests
{
    private static readonly byte[] Fixture = Enumerable.Range(0, 131072).Select(i => (byte)(i % 251)).ToArray();
    private static DownloadOptions Options => new() { Workers = 4, MinimumRangeBytes = 4096, MaximumRangeBytes = 8192, RetryLimit = 0,
        ExpectedSha256 = Convert.ToHexString(SHA256.HashData(Fixture)) };

    [Fact]
    public async Task ParallelTransferProducesExactFileAndCleansCheckpoint()
    {
        await InDirectoryAsync(async target =>
        {
            using var handler = new FixtureHandler();
            using var client = new HttpClient(handler);
            await new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options);
            Assert.Equal(Fixture, await File.ReadAllBytesAsync(target));
            Assert.False(File.Exists(target + ".part.sqlite"));
            Assert.True(handler.Ranges.Count > 4);
        });
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public async Task SupportsMoreThanEightConcurrentRequests(int workers)
    {
        await InDirectoryAsync(async target =>
        {
            using var handler = new FixtureHandler { DelayMilliseconds = 50 };
            using var client = new HttpClient(handler);
            await new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options with
            { Workers = workers, MinimumRangeBytes = 4096, MaximumRangeBytes = 4096 });
            Assert.True(handler.MaxConcurrent > 8);
            Assert.Equal(Fixture, await File.ReadAllBytesAsync(target));
        });
    }

    [Fact]
    public async Task CancellationResumesOnlyVerifiedChunksAndRepairsModifiedData()
    {
        await InDirectoryAsync(async target =>
        {
            using var handler = new FixtureHandler();
            using var client = new HttpClient(handler);
            using var stop = new CancellationTokenSource();
            var progress = new InlineProgress(p => { if (p.Stage == DownloadStage.Downloading && p.CompletedBytes >= 8192) stop.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options with { Workers = 1 }, progress, stop.Token));
            Assert.False(File.Exists(target));
            Assert.True(File.Exists(target + ".part.sqlite"));
            var originalRequests = handler.Ranges.Count;
            // Damage a checkpointed byte. Resume must hash and redownload its entire extent.
            using (var file = File.OpenWrite(target + ".part")) file.WriteByte(255);
            var wasResumed = false;
            await new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options, new InlineProgress(p => wasResumed |= p.Resumed));
            Assert.True(wasResumed);
            Assert.Equal(Fixture, await File.ReadAllBytesAsync(target));
            Assert.Contains(handler.Ranges.Skip(originalRequests), r => r.From == 0 && r.To > 0);
        });
    }

    [Fact]
    public async Task ChangedIdentityDoesNotOverwritePartialData()
    {
        await InDirectoryAsync(async target =>
        {
            using var handler = new FixtureHandler();
            using var client = new HttpClient(handler);
            using var stop = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options with { Workers = 1 },
                new InlineProgress(p => { if (p.CompletedBytes >= 4096) stop.Cancel(); }), stop.Token));
            var before = await File.ReadAllBytesAsync(target + ".part");
            handler.ETag = "\"changed\"";
            await Assert.ThrowsAsync<InvalidDataException>(() => new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options));
            Assert.Equal(before, await File.ReadAllBytesAsync(target + ".part"));
            Assert.False(File.Exists(target));
        });
    }

    [Fact]
    public async Task DifferentRepresentationsAcrossPathsAreRejected()
    {
        await InDirectoryAsync(async target =>
        {
            using var first = new HttpClient(new FixtureHandler());
            using var second = new HttpClient(new FixtureHandler { ETag = "\"other\"" });
            await Assert.ThrowsAsync<InvalidDataException>(() => new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [first, second], Options));
            Assert.False(File.Exists(target + ".part"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SequentialFallbackHandlesMissingRangesAndMissingValidators(bool supportsRanges)
    {
        await InDirectoryAsync(async target =>
        {
            using var client = new HttpClient(new FixtureHandler { SupportsRanges = supportsRanges, ETag = null });
            await new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options);
            Assert.Equal(Fixture, await File.ReadAllBytesAsync(target));
        });
    }

    [Fact]
    public async Task HashMismatchAndExistingDestinationCannotFinalizeOrOverwrite()
    {
        await InDirectoryAsync(async target =>
        {
            using var client = new HttpClient(new FixtureHandler());
            await Assert.ThrowsAsync<InvalidDataException>(() => new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options with { ExpectedSha256 = new string('0', 64) }));
            Assert.False(File.Exists(target));
            await File.WriteAllTextAsync(target, "user file");
            await Assert.ThrowsAsync<IOException>(() => new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options));
            Assert.Equal("user file", await File.ReadAllTextAsync(target));
        });
    }

    [Fact]
    public async Task TransientStatusIsRetriedWithinBudget()
    {
        await InDirectoryAsync(async target =>
        {
            using var handler = new FixtureHandler { FailOnce = true };
            using var client = new HttpClient(handler);
            await new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options with { RetryLimit = 1 });
            Assert.Equal(Fixture, await File.ReadAllBytesAsync(target));
        });
    }

    [Fact]
    public async Task DestinationCreatedDuringTransferIsPreservedAtFinalization()
    {
        await InDirectoryAsync(async target =>
        {
            using var client = new HttpClient(new FixtureHandler());
            var created = false;
            var progress = new InlineProgress(p =>
            {
                if (!created && p.Stage == DownloadStage.Downloading && p.CompletedBytes > 0)
                { File.WriteAllText(target, "another process"); created = true; }
            });
            await Assert.ThrowsAsync<IOException>(() => new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options, progress));
            Assert.Equal("another process", await File.ReadAllTextAsync(target));
            Assert.True(File.Exists(target + ".part"));
        });
    }

    [Fact]
    public async Task LongRetryAfterStopsWithoutRetryingEarly()
    {
        await InDirectoryAsync(async target =>
        {
            using var handler = new FixtureHandler { FailOnce = true, RetryDelay = TimeSpan.FromHours(1) };
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync<DownloadManager.Infrastructure.Http.HttpStatusFailure>(() => new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options with { RetryLimit = 2 }));
            Assert.Equal(1, handler.RequestCount);
            Assert.False(File.Exists(target + ".part"));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadCapIsSharedByAllWorkersAndAppliesToSequentialFallback(bool ranged)
    {
        await InDirectoryAsync(async target =>
        {
            using var client = new HttpClient(new FixtureHandler { SupportsRanges = ranged });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client],
                Options with { Workers = 4, BytesPerSecondLimit = Fixture.Length }, cancellationToken: deadline.Token);
            Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(0.98), "The complete payload must not be delivered faster than its shared one-second budget.");
            Assert.Equal(Fixture, await File.ReadAllBytesAsync(target));
        });
    }

    [Fact]
    public async Task BandwidthWaitRespondsImmediatelyToCancellation()
    {
        var limiter = new DownloadManager.Infrastructure.Http.PayloadRateLimiter(1);
        using var stop = new CancellationTokenSource();
        var wait = limiter.WaitAsync(1, stop.Token).AsTask();
        Assert.False(wait.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    private static async Task InDirectoryAsync(Func<string, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await test(Path.Combine(directory, "fixture.bin")); }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public string? ETag { get; set; } = "\"v1\"";
        public bool SupportsRanges { get; init; } = true;
        private int active;
        private int maximum;
        public int MaxConcurrent => maximum;
        public int DelayMilliseconds { get; init; } = 2;
        public int RequestCount { get; private set; }
        public TimeSpan RetryDelay { get; init; } = TimeSpan.Zero;
        public bool FailOnce { get; set; }
        public List<(long From, long To)> Ranges { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var now = Interlocked.Increment(ref active);
            int observed;
            do { observed = maximum; if (now <= observed) break; }
            while (Interlocked.CompareExchange(ref maximum, now, observed) != observed);
            try { await Task.Delay(DelayMilliseconds, cancellationToken); }
            finally { Interlocked.Decrement(ref active); }
            if (FailOnce)
            {
                FailOnce = false;
                var pressure = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                pressure.Headers.RetryAfter = new RetryConditionHeaderValue(RetryDelay);
                return pressure;
            }
            var selected = request.Headers.Range?.Ranges.Single();
            var from = selected?.From ?? 0;
            var to = selected?.To ?? Fixture.Length - 1;
            var isRange = selected is not null && SupportsRanges;
            if (!isRange) { from = 0; to = Fixture.Length - 1; }
            lock (Ranges) Ranges.Add((from, to));
            var result = new HttpResponseMessage(isRange ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = new ByteArrayContent(Fixture.AsSpan((int)from, (int)(to - from + 1)).ToArray()) };
            if (ETag is not null) result.Headers.ETag = new(ETag);
            if (isRange) result.Content.Headers.ContentRange = new(from, to, Fixture.Length);
            return result;
        }
    }
}
