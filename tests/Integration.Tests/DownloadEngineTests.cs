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

    [Fact]
    public async Task ValidatedPathsTransferConcurrentDisjointRanges()
    {
        await InDirectoryAsync(async target =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            static async Task WaitForPeerAsync(TaskCompletionSource entered, TaskCompletionSource peer, CancellationToken token)
            {
                entered.TrySetResult();
                await peer.Task.WaitAsync(token);
            }
            using var first = new FixtureHandler { BeforeRange = (_, _, token) => WaitForPeerAsync(firstEntered, secondEntered, token) };
            using var second = new FixtureHandler { BeforeRange = (_, _, token) => WaitForPeerAsync(secondEntered, firstEntered, token) };
            using var firstClient = new HttpClient(first);
            using var secondClient = new HttpClient(second);

            await new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [firstClient, secondClient],
                Options with { Workers = 2 }, cancellationToken: deadline.Token);

            Assert.Contains(first.Ranges, range => range.To > range.From);
            Assert.Contains(second.Ranges, range => range.To > range.From);
            long nextStart = 0;
            foreach (var range in first.Ranges.Concat(second.Ranges).Where(range => range.To > range.From).OrderBy(range => range.From))
            {
                Assert.Equal(nextStart, range.From);
                nextStart = range.To + 1;
            }
            Assert.Equal(Fixture.Length, nextStart);
            Assert.Equal(Fixture, await File.ReadAllBytesAsync(target, deadline.Token));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidatingOnePathReplaysItsRangeButCancellingTheJobStopsAllPaths(bool cancelJob)
    {
        await InDirectoryAsync(async target =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            using var pathInvalidated = new CancellationTokenSource();
            var primaryEntered = new TaskCompletionSource<(long From, long To)>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var first = new FixtureHandler
            {
                BeforeRange = async (from, to, token) =>
                {
                    primaryEntered.TrySetResult((from, to));
                    await Task.Delay(Timeout.Infinite, token);
                }
            };
            using var second = new FixtureHandler();
            using var firstClient = new HttpClient(first);
            using var secondClient = new HttpClient(second);

            var download = new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [firstClient, secondClient],
                Options with { Workers = 2 }, cancellationToken: stop.Token, transportInvalidations: [pathInvalidated.Token, CancellationToken.None]);
            var interruptedRange = await primaryEntered.Task.WaitAsync(deadline.Token);
            if (cancelJob)
            {
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
                Assert.DoesNotContain(interruptedRange, second.Ranges);
                Assert.False(File.Exists(target));
            }
            else
            {
                pathInvalidated.Cancel();
                await download;
                Assert.Contains(interruptedRange, second.Ranges);
                Assert.Equal(Fixture, await File.ReadAllBytesAsync(target, deadline.Token));
            }
            Assert.False(deadline.IsCancellationRequested);
        });
    }

    [Fact]
    public async Task PartialRangeIsReplayedOnHealthyPathWhenPrimaryStaysDown()
    {
        await InDirectoryAsync(async target =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var lostPath = 0;
            (long From, long To) attempted = default;
            using var first = new FixtureHandler
            {
                BeforeRequest = () =>
                {
                    if (Volatile.Read(ref lostPath) != 0)
                        throw new HttpRequestException(HttpRequestError.ConnectionError, "Injected persistent adapter loss");
                },
                RangeResponse = (response, from, to) =>
                {
                    attempted = (from, to);
                    response.Content.Dispose();
                    response.Content = new ByteArrayContent(Fixture.AsSpan((int)from, 512).ToArray());
                    response.Content.Headers.ContentRange = new(from, to, Fixture.Length);
                    response.Content.Headers.ContentLength = to - from + 1;
                    Volatile.Write(ref lostPath, 1);
                }
            };
            using var second = new FixtureHandler();
            using var firstClient = new HttpClient(first);
            using var secondClient = new HttpClient(second);
            var samples = new List<DownloadProgress>();

            await new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [firstClient, secondClient],
                Options with { Workers = 2 }, new InlineProgress(samples.Add), deadline.Token,
                jobId: Guid.NewGuid(), interfaceIndices: [71, 82]);

            Assert.True(attempted.To > attempted.From);
            Assert.Contains(attempted, second.Ranges);
            Assert.Equal(Fixture, await File.ReadAllBytesAsync(target, deadline.Token));
            var completed = samples.Last();
            Assert.Equal(DownloadStage.Completed, completed.Stage);
            Assert.Equal(Fixture.Length, completed.CompletedBytes);
            Assert.Collection(completed.Paths, primary =>
            {
                Assert.Equal(1, primary.Ordinal);
                Assert.Equal(71, primary.InterfaceIndex);
                Assert.Equal(DownloadPathState.Retired, primary.State);
                Assert.Equal(512, primary.ReceivedBytes);
                Assert.Equal(0, primary.CheckpointedBytes);
                Assert.Equal(0, primary.ActiveOperations);
            }, surviving =>
            {
                Assert.Equal(2, surviving.Ordinal);
                Assert.Equal(82, surviving.InterfaceIndex);
                Assert.Equal(DownloadPathState.Available, surviving.State);
                Assert.Equal(Fixture.Length, surviving.ReceivedBytes);
                Assert.Equal(Fixture.Length, surviving.CheckpointedBytes);
                Assert.Equal(0, surviving.ActiveOperations);
            });
            Assert.All(samples, sample => Assert.InRange(sample.AcceptedBytes, sample.CompletedBytes, Fixture.Length));
            Assert.False(File.Exists(target + ".part.sqlite"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerRejectionOrChangedValidatorNeverReplaysRangeOnAnotherPath(bool changedValidator)
    {
        await InDirectoryAsync(async target =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            (long From, long To) rejected = default;
            using var first = new FixtureHandler
            {
                RangeResponse = (response, from, to) =>
                {
                    rejected = (from, to);
                    if (changedValidator)
                        response.Headers.ETag = new("\"changed\"");
                    else
                        response.StatusCode = HttpStatusCode.Forbidden;
                }
            };
            using var second = new FixtureHandler();
            using var firstClient = new HttpClient(first);
            using var secondClient = new HttpClient(second);

            var download = new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [firstClient, secondClient],
                Options with { Workers = 2 }, cancellationToken: deadline.Token);
            if (changedValidator)
                await Assert.ThrowsAsync<InvalidDataException>(() => download);
            else
                Assert.Equal(HttpStatusCode.Forbidden,
                    (await Assert.ThrowsAsync<DownloadManager.Infrastructure.Http.HttpStatusFailure>(() => download)).StatusCode);

            Assert.True(rejected.To > rejected.From);
            Assert.DoesNotContain(rejected, second.Ranges);
            Assert.False(File.Exists(target));
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

    [Fact]
    public async Task LiveAcceptedProgressPrecedesDurableCheckpointWithoutInflatingCompletion()
    {
        await InDirectoryAsync(async target =>
        {
            using var client = new HttpClient(new FixtureHandler());
            var samples = new List<DownloadProgress>();
            await new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options with
            { Workers = 1, MinimumRangeBytes = Fixture.Length, MaximumRangeBytes = Fixture.Length, BytesPerSecondLimit = Fixture.Length },
                new InlineProgress(p => samples.Add(p)));
            Assert.Contains(samples, p => p.Stage == DownloadStage.Downloading && p.AcceptedBytes > p.CompletedBytes && p.AcceptedBytes < Fixture.Length);
            Assert.All(samples, p => Assert.True(p.AcceptedBytes >= p.CompletedBytes));
            Assert.Equal(Fixture.Length, samples.Last().CompletedBytes);
            Assert.Equal(Fixture, await File.ReadAllBytesAsync(target));
        });
    }

    [Fact]
    public async Task SidecarDeletionFailureDoesNotFailOrOverwriteFinalizedDownload()
    {
        await InDirectoryAsync(async target =>
        {
            using var client = new HttpClient(new FixtureHandler());
            DownloadProgress? completed = null;
            var engine = new DownloadEngine(_ => throw new IOException("Injected sidecar sharing failure"));
            await engine.DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options,
                new InlineProgress(p => { if (p.Stage == DownloadStage.Completed) completed = p; }));
            Assert.NotNull(completed); Assert.True(completed.CheckpointCleanupPending);
            Assert.Equal(Fixture, await File.ReadAllBytesAsync(target));
            Assert.True(File.Exists(target + ".part.sqlite"));
            await Assert.ThrowsAsync<IOException>(() => engine.DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options));
            Assert.Equal(Fixture, await File.ReadAllBytesAsync(target));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PathCapIsSharedByItsWorkersAndWorksWithSequentialFallback(bool ranged)
    {
        await InDirectoryAsync(async target =>
        {
            using var client = new HttpClient(new FixtureHandler { SupportsRanges = ranged });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await new DownloadEngine().DownloadAsync(new("http://fixture.invalid/file"), target, [client], Options,
                cancellationToken: timeout.Token, transportSpeedLimits: new long?[] { Fixture.Length });
            Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(0.98));
            Assert.Equal(Fixture, await File.ReadAllBytesAsync(target));
        });
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
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);
        public TimeSpan RetryDelay { get; init; } = TimeSpan.Zero;
        public bool FailOnce { get; set; }
        public Action? BeforeRequest { get; init; }
        public Func<long, long, CancellationToken, Task>? BeforeRange { get; init; }
        public Action<HttpResponseMessage, long, long>? RangeResponse { get; init; }
        public List<(long From, long To)> Ranges { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            BeforeRequest?.Invoke();
            Interlocked.Increment(ref requestCount);
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
            var isDataRange = request.Headers.IfRange is not null;
            if (isDataRange && BeforeRange is not null) await BeforeRange(from, to, cancellationToken);
            lock (Ranges) Ranges.Add((from, to));
            var result = new HttpResponseMessage(isRange ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = new ByteArrayContent(Fixture.AsSpan((int)from, (int)(to - from + 1)).ToArray()) };
            if (ETag is not null) result.Headers.ETag = new(ETag);
            if (isRange) result.Content.Headers.ContentRange = new(from, to, Fixture.Length);
            if (isDataRange) RangeResponse?.Invoke(result, from, to);
            return result;
        }
    }
}
