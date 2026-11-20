using System.Security.Cryptography;
using System.Text;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Persistence;
using DownloadManager.Infrastructure.Queueing;

namespace DownloadManager.Tests;

public class QueueTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
    private static QueueJob Job(string filename = "one.bin") => new()
    { Source = new("https://example.test/file?token=DO-NOT-PERSIST-CLEAR"), Destination = filename, UseSystemRoute = true, CreatedAt = Now };

    [Fact]
    public async Task StoreEncryptsPayloadAndReloadsHistoryWithoutPlaintextUrls()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var protector = new TestProtector();
            var path = Path.Combine(directory, "queue.sqlite");
            var job = Job() with { Category = "Documents", Paths = [new QueuedPath("adapter", "192.0.2.1", 2048)], Options = new DownloadOptions { BytesPerSecondLimit = 12345 } };
            using (var store = new QueueStore(path, protector))
            {
                store.Save(job);
                var loaded = Assert.Single(store.Load());
                Assert.Equal(job.Source, loaded.Source);
                Assert.Equal(job.Destination, loaded.Destination);
                Assert.Equal("Documents", loaded.Category);
                Assert.Equal(2048, Assert.Single(loaded.Paths).BytesPerSecondLimit);
                Assert.Equal(12345, loaded.Options.BytesPerSecondLimit);
                Assert.Throws<IOException>(() => new QueueStore(path, protector));
            }
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.DoesNotContain("DO-NOT-PERSIST-CLEAR", Encoding.UTF8.GetString(bytes));
            using var reopened = new QueueStore(path, protector);
            Assert.Equal(job.Id, Assert.Single(reopened.Load()).Id);
            var second = Job("two.bin") with { QueueName = "Custom queue" };
            reopened.SaveMany([job with { QueueName = "Archive" }, second]);
            Assert.Equal("Archive", reopened.Load().Single(j => j.Id == job.Id).QueueName);
            Assert.Equal("Custom queue", reopened.Load().Single(j => j.Id == second.Id).QueueName);
            reopened.Remove(job.Id);
            reopened.Remove(second.Id);
            Assert.Empty(reopened.Load());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RunnerPersistsCompletionAndProcessesJobsSequentially()
    {
        var store = new MemoryStore();
        var active = 0;
        var executor = new FakeExecutor(async (_, progress, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            await Task.Delay(5, token);
            progress?.Report(new(DownloadStage.Completed, 10, 10, false));
            Interlocked.Decrement(ref active);
        });
        var queue = new QueueCoordinator(store, executor, new TestClock(Now));
        await queue.InitializeAsync();
        var first = await queue.EnqueueAsync(Job("one.bin"));
        var second = await queue.EnqueueAsync(Job("two.bin") with { Priority = 5 });
        await queue.RunReadyAsync();
        Assert.Equal(new[] { second.Id, first.Id }, executor.Executed);
        Assert.All(await queue.SnapshotAsync(), j => { Assert.Equal(QueueJobState.Completed, j.State); Assert.Equal(10, j.CompletedBytes); });
        await queue.ClearTerminalHistoryAsync();
        Assert.Empty(store.Jobs);
    }

    [Fact]
    public async Task NamedQueuesRunIndependentlyAndHonorParallelDownloadLimit()
    {
        var store = new MemoryStore();
        var active = 0; var maximum = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new FakeExecutor(async (_, _, token) =>
        {
            var current = Interlocked.Increment(ref active);
            int observed;
            do { observed = maximum; if (current <= observed) break; }
            while (Interlocked.CompareExchange(ref maximum, current, observed) != observed);
            if (current >= 3) allStarted.TrySetResult();
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await Task.Delay(5, token);
            Interlocked.Decrement(ref active);
        });
        var queue = new QueueCoordinator(store, executor, new TestClock(Now));
        await queue.InitializeAsync();
        await queue.EnqueueAsync(Job("main.bin"));
        await queue.EnqueueAsync(Job("custom-one.bin") with { QueueName = "Custom" });
        await queue.EnqueueAsync(Job("custom-two.bin") with { QueueName = "Custom" });

        await Task.WhenAll(queue.RunReadyAsync("Custom", 2), queue.RunReadyAsync("Main", 1));

        Assert.Equal(3, maximum);
        Assert.Equal(3, executor.Executed.Count);
        Assert.All(await queue.SnapshotAsync(), item => Assert.Equal(QueueJobState.Completed, item.State));
    }

    [Fact]
    public async Task DownloadsCanMoveAndRenameQueuesWithoutChangingOtherJobs()
    {
        var store = new MemoryStore();
        var queue = new QueueCoordinator(store, new FakeExecutor((_, _, _) => Task.CompletedTask), new TestClock(Now));
        await queue.InitializeAsync();
        var custom = await queue.EnqueueAsync(Job("custom.bin") with { QueueName = "Work" });
        var bulk = await queue.EnqueueAsync(Job("bulk.bin") with { QueueName = "Work" });
        var main = await queue.EnqueueAsync(Job("main.bin"));

        await queue.RenameQueueAsync("Work", "Archive");
        Assert.Equal("Archive", store.Jobs[custom.Id].QueueName);
        Assert.Equal(QueueJob.MainQueue, store.Jobs[main.Id].QueueName);
        await queue.SetQueueNameAsync(custom.Id, QueueJob.MainQueue);
        await queue.MoveQueueJobsAsync("Archive", QueueJob.MainQueue);
        Assert.Equal(QueueJob.MainQueue, store.Jobs[bulk.Id].QueueName);
        Assert.All(await queue.SnapshotAsync(), item => Assert.Equal(QueueJob.MainQueue, item.QueueName));
    }

    [Fact]
    public void QueueSettingsSupportBoundedAllParallelismAndOvernightSchedules()
    {
        var settings = new DownloadQueueSettings { Name = "Night", MaxConcurrentDownloads = 0, StartTime = "22:00", StopTime = "06:00" };
        settings.Validate();
        Assert.True(settings.IsWithinSchedule(new DateTime(2026, 11, 14, 23, 0, 0)));
        Assert.True(settings.IsWithinSchedule(new DateTime(2026, 11, 15, 5, 59, 0)));
        Assert.False(settings.IsWithinSchedule(new DateTime(2026, 11, 15, 6, 0, 0)));
        Assert.Throws<ArgumentException>(() => (settings with { StartTime = "22:00", StopTime = "" }).Validate());
    }

    [Fact]
    public async Task FailureDoesNotLoopAndNextReadyJobCanRun()
    {
        var store = new MemoryStore();
        var executor = new FakeExecutor((job, _, _) => job.Destination.EndsWith("bad.bin", StringComparison.Ordinal)
            ? Task.FromException(new InvalidDataException("secret URL must not be persisted")) : Task.CompletedTask);
        var queue = new QueueCoordinator(store, executor, new TestClock(Now));
        await queue.InitializeAsync();
        var failed = await queue.EnqueueAsync(Job("bad.bin") with { Priority = 1 });
        var good = await queue.EnqueueAsync(Job("good.bin"));
        await queue.RunReadyAsync(); await queue.RunReadyAsync();
        Assert.Equal(2, executor.Executed.Count);
        Assert.Equal("Validation", store.Jobs[failed.Id].FailureCode);
        Assert.Equal(QueueJobState.Completed, store.Jobs[good.Id].State);
        await queue.ChangeStateAsync(failed.Id, QueueJobState.Queued);
        Assert.Equal(QueueJobState.Queued, store.Jobs[failed.Id].State);
    }

    [Fact]
    public async Task InterruptedRowsRecoverPausedAndFutureJobsWaitForClock()
    {
        var store = new MemoryStore();
        var interrupted = Job() with { State = QueueJobState.Running };
        store.Save(interrupted);
        var clock = new TestClock(Now);
        var executor = new FakeExecutor((_, _, _) => Task.CompletedTask);
        var queue = new QueueCoordinator(store, executor, clock);
        await queue.InitializeAsync();
        Assert.Equal(QueueJobState.Paused, store.Jobs[interrupted.Id].State);
        var future = await queue.EnqueueAsync(Job("future.bin") with { NotBefore = Now.AddHours(1) });
        await queue.RunReadyAsync(); Assert.Empty(executor.Executed);
        clock.Now = Now.AddHours(1);
        await queue.RunReadyAsync(); Assert.Equal(future.Id, Assert.Single(executor.Executed));
    }

    [Fact]
    public async Task CancellationWaitsForExecutorBeforePersistingPauseAndBlocksSecondRunner()
    {
        var store = new MemoryStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = false;
        var executor = new FakeExecutor(async (_, _, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { ended = true; }
        });
        var queue = new QueueCoordinator(store, executor, new TestClock(Now));
        await queue.InitializeAsync(); var job = await queue.EnqueueAsync(Job());
        using var stop = new CancellationTokenSource();
        var run = queue.RunReadyAsync(token: stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.SetQueueNameAsync(job.Id, "Other"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RunReadyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.SetCategoryAsync(job.Id, "Changed"));
        stop.Cancel(); await run;
        Assert.True(ended); Assert.Equal(QueueJobState.Paused, store.Jobs[job.Id].State);
    }

    [Theory]
    [InlineData(QueueJobState.Paused)]
    [InlineData(QueueJobState.Cancelled)]
    public async Task StoppingSelectedDownloadDoesNotCancelSiblingWorker(QueueJobState target)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = Job("selected.bin");
        var executor = new FakeExecutor(async (job, _, token) =>
        {
            if (job.Id == selected.Id)
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { stopping.TrySetResult(); }
            }
            else
            {
                siblingStarted.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        });
        var queue = new QueueCoordinator(new MemoryStore(), executor, new TestClock(Now));
        await queue.InitializeAsync();
        await queue.EnqueueAsync(selected);
        var sibling = await queue.EnqueueAsync(Job("sibling.bin"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = queue.RunReadyAsync(QueueJob.MainQueue, 2, token: deadline.Token);
        await Task.WhenAll(started.Task, siblingStarted.Task).WaitAsync(deadline.Token);
        await queue.ChangeStateAsync(selected.Id, target, deadline.Token);
        await stopping.Task.WaitAsync(deadline.Token);
        release.TrySetResult();
        await running;
        var jobs = (await queue.SnapshotAsync()).ToDictionary(job => job.Id);
        Assert.Equal(target, jobs[selected.Id].State);
        Assert.Equal(QueueJobState.Completed, jobs[sibling.Id].State);
    }

    [Fact]
    public async Task DuplicateDestinationIsRejectedAndHistoryClearDoesNotDeleteFiles()
    {
        var queue = new QueueCoordinator(new MemoryStore(), new FakeExecutor((_, _, _) => Task.CompletedTask), new TestClock(Now));
        await queue.InitializeAsync();
        var filename = Path.GetTempFileName();
        try
        {
            var job = await queue.EnqueueAsync(Job(filename));
            await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(Job(filename)));
            await queue.ChangeStateAsync(job.Id, QueueJobState.Cancelled);
            await queue.ClearTerminalHistoryAsync();
            Assert.True(File.Exists(filename));
        }
        finally { File.Delete(filename); }
    }

    [WindowsOnlyFact]
    public void WindowsDpapiPersistsAndAuthenticatesUserProtectedPayload()
    {
        var protector = new WindowsQueueProtector();
        var clear = Encoding.UTF8.GetBytes("signed URL secret");
        var encrypted = protector.Protect(clear);
        Assert.NotEqual(clear, encrypted);
        Assert.Equal(clear, new WindowsQueueProtector().Unprotect(encrypted));
        encrypted[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(encrypted));
    }

    [Fact]
    public void TamperedEncryptedDatabaseIsRejectedWithoutDeletingHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var protector = new TestProtector();
            var path = Path.Combine(directory, "queue.sqlite");
            using var store = new QueueStore(path, protector);
            store.Save(Job());
            using var sql = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            sql.Open();
            using var command = sql.CreateCommand();
            command.CommandText = "SELECT payload FROM jobs";
            var payload = (byte[])command.ExecuteScalar()!;
            payload[^1] ^= 1;
            command.CommandText = "UPDATE jobs SET payload=$payload";
            command.Parameters.AddWithValue("$payload", payload);
            command.ExecuteNonQuery();
            Assert.ThrowsAny<CryptographicException>(() => store.Load());
            command.CommandText = "SELECT COUNT(*) FROM jobs";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ScheduledProtectedQueueRunsRealHttpDownloadAndRestoresCompletedHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var payload = Enumerable.Range(0, 32768).Select(i => (byte)(i % 251)).ToArray();
        var connections = new System.Collections.Concurrent.ConcurrentBag<Task>();
        var accept = AcceptAsync();
        try
        {
            using var protector = new TestProtector();
            var path = Path.Combine(directory, "queue.sqlite");
            var target = Path.Combine(directory, "output.bin");
            var clock = new TestClock(Now);
            using (var store = new QueueStore(path, protector))
            {
                var queue = new QueueCoordinator(store, new QueuedDownloadExecutor(), clock);
                await queue.InitializeAsync(deadline.Token);
                await queue.EnqueueAsync(Job(target) with
                {
                    Source = new($"http://127.0.0.1:{port}/fixture?signature=NEVER-PLAINTEXT"),
                    NotBefore = Now.AddMinutes(1),
                    Options = new DownloadOptions { Workers = 2, MinimumRangeBytes = 4096, MaximumRangeBytes = 8192,
                        ExpectedSha256 = Convert.ToHexString(SHA256.HashData(payload)) }
                }, deadline.Token);
                await queue.RunReadyAsync(token: deadline.Token);
                Assert.Empty(connections);
                clock.Now = Now.AddMinutes(1);
                await queue.RunReadyAsync(token: deadline.Token);
                Assert.Equal(payload, await File.ReadAllBytesAsync(target, deadline.Token));
                Assert.Equal(QueueJobState.Completed, Assert.Single(await queue.SnapshotAsync()).State);
                Assert.False(File.Exists(target + ".part.sqlite"));
            }
            using var restored = new QueueStore(path, protector);
            var history = Assert.Single(restored.Load());
            Assert.Equal(QueueJobState.Completed, history.State);
            Assert.Equal(payload.Length, history.CompletedBytes);
            Assert.DoesNotContain("NEVER-PLAINTEXT", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, deadline.Token)));
        }
        finally
        {
            deadline.Cancel(); listener.Stop();
            try { await accept; await Task.WhenAll(connections); }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException or IOException) { }
            Directory.Delete(directory, true);
        }

        async Task AcceptAsync()
        {
            while (!deadline.IsCancellationRequested)
            {
                var peer = await listener.AcceptTcpClientAsync(deadline.Token);
                connections.Add(RespondAsync(peer));
            }
        }
        async Task RespondAsync(System.Net.Sockets.TcpClient peer)
        {
            using (peer)
            {
                await using var stream = peer.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                long from = 0, to = payload.Length - 1;
                while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } line)
                {
                    if (!line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) continue;
                    var bounds = line[13..].Split('-');
                    from = long.Parse(bounds[0], System.Globalization.CultureInfo.InvariantCulture);
                    to = long.Parse(bounds[1], System.Globalization.CultureInfo.InvariantCulture);
                }
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 206 Partial Content\r\nETag: \"queue-v1\"\r\nContent-Range: bytes {from}-{to}/{payload.Length}\r\nContent-Length: {to - from + 1}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, deadline.Token);
                await stream.WriteAsync(payload.AsMemory((int)from, (int)(to - from + 1)), deadline.Token);
            }
        }
    }

    [Fact]
    public async Task CategoriesSurvivePersistenceAndCanChangeWithoutChangingStateOrFiles()
    {
        var memory = new MemoryStore();
        var queue = new QueueCoordinator(memory, new FakeExecutor((_, _, _) => Task.CompletedTask), new TestClock(Now));
        await queue.InitializeAsync();
        var job = await queue.EnqueueAsync(Job() with { Category = "  Documents  " });
        Assert.Equal("Documents", job.Category);
        await queue.SetCategoryAsync(job.Id, "Archives");
        Assert.Equal(QueueJobState.Queued, memory.Jobs[job.Id].State);
        await queue.RunReadyAsync();
        await queue.SetCategoryAsync(job.Id, "History");
        Assert.Equal(QueueJobState.Completed, memory.Jobs[job.Id].State);
        Assert.Equal("History", memory.Jobs[job.Id].Category);
        var restored = new QueueCoordinator(memory, new FakeExecutor((_, _, _) => Task.CompletedTask), new TestClock(Now));
        await restored.InitializeAsync();
        Assert.Equal("History", Assert.Single(await restored.SnapshotAsync()).Category);
    }

    [Fact]
    public void ProtectedDefaultsRoundTripAndFailuresPreservePreviousFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var protector = new TestProtector();
            var filename = Path.Combine(directory, "preferences.bin");
            var store = new PreferencesStore(filename, protector);
            Assert.Equal(4, store.Load().Workers);
            Assert.Equal(QueueJob.MainQueue, Assert.Single(store.Load().Queues).Name);
            store.Save(new DesktopPreferences { Workers = 16, SpeedLimitKiB = 128, Priority = 2, Category = " PrivateCategory ", DestinationRoot = directory,
                Queues = [new DownloadQueueSettings(), new DownloadQueueSettings { Name = "Night shift", MaxConcurrentDownloads = 3, StartTime = "22:00", StopTime = "06:00" }] });
            var saved = store.Load();
            Assert.Equal(directory, saved.DestinationRoot);
            Assert.Equal(16, saved.Workers); Assert.Equal(128, saved.SpeedLimitKiB); Assert.Equal("PrivateCategory", saved.Category);
            Assert.Equal(2, saved.Queues.Count); Assert.Equal("Night shift", saved.Queues[1].Name); Assert.Equal(3, saved.Queues[1].MaxConcurrentDownloads);
            Assert.Equal(16, saved.DefaultsFor("PrivateCategory").Workers);
            Assert.Equal(4, saved.DefaultsFor("General").Workers);
            store.Save(saved with { CategoryDefaults = [new("PrivateCategory", 8, 3, directory), new("General", 2, -1)] });
            var withCategoryDefaults = store.Load();
            Assert.Equal(new CategoryDownloadSettings("PrivateCategory", 8, 3, directory), withCategoryDefaults.DefaultsFor("privatecategory"));
            Assert.Equal(new CategoryDownloadSettings("General", 2, -1), withCategoryDefaults.DefaultsFor("General"));
            Assert.Equal(saved.Queues, withCategoryDefaults.Queues);
            var original = File.ReadAllBytes(filename);
            Assert.DoesNotContain("PrivateCategory", Encoding.UTF8.GetString(original));
            Assert.Throws<InvalidOperationException>(() => new PreferencesStore(filename, new FailingProtector()).Save(new()));
            Assert.Equal(original, File.ReadAllBytes(filename));
            Assert.Single(Directory.GetFiles(directory));
            var tampered = original.ToArray(); tampered[^1] ^= 1; File.WriteAllBytes(filename, tampered);
            Assert.ThrowsAny<CryptographicException>(() => store.Load());
            Assert.Equal(tampered, File.ReadAllBytes(filename));
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class FailingProtector : IQueuePayloadProtector
    {
        public byte[] Protect(byte[] data) => throw new InvalidOperationException("Simulated encryption failure");
        public byte[] Unprotect(byte[] data) => throw new InvalidOperationException();
    }

    [Theory]
    [InlineData(QueueJobState.Completed)]
    [InlineData(QueueJobState.Paused)]
    [InlineData(QueueJobState.Failed)]
    public async Task TerminalSaveFailureCanBeRetriedWithoutGhostRunnerOrRepeatedExecution(QueueJobState expected)
    {
        var memory = new MemoryStore { FailTerminalWrites = true };
        var executor = new FakeExecutor((_, _, _) => expected switch
        {
            QueueJobState.Paused => Task.FromException(new OperationCanceledException()),
            QueueJobState.Failed => Task.FromException(new InvalidDataException()),
            _ => Task.CompletedTask
        });
        var queue = new QueueCoordinator(memory, executor, new TestClock(Now));
        await queue.InitializeAsync();
        var job = await queue.EnqueueAsync(Job());
        await Assert.ThrowsAsync<IOException>(() => queue.RunReadyAsync());
        Assert.Equal(expected, Assert.Single(await queue.SnapshotAsync()).State);
        await Assert.ThrowsAsync<IOException>(() => queue.RetryPersistenceAsync());
        await Assert.ThrowsAsync<IOException>(() => queue.ClearTerminalHistoryAsync());
        Assert.Equal(QueueJobState.Running, memory.Jobs[job.Id].State);
        memory.FailTerminalWrites = false;
        await queue.RetryPersistenceAsync();
        Assert.Equal(expected, memory.Jobs[job.Id].State);
        await queue.RunReadyAsync();
        Assert.Single(executor.Executed);
        if (expected != QueueJobState.Completed)
        { await queue.ChangeStateAsync(job.Id, QueueJobState.Queued); Assert.Equal(QueueJobState.Queued, memory.Jobs[job.Id].State); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TruncatedAndHttpBodyErrorsAreNetworkFailures(bool truncated)
    {
        var memory = new MemoryStore();
        var executor = new FakeExecutor((_, _, _) => Task.FromException(truncated
            ? new EndOfStreamException() : new System.Net.Http.HttpIOException(System.Net.Http.HttpRequestError.ResponseEnded)));
        var queue = new QueueCoordinator(memory, executor, new TestClock(Now));
        await queue.InitializeAsync(); var job = await queue.EnqueueAsync(Job()); await queue.RunReadyAsync();
        Assert.Equal("Network", memory.Jobs[job.Id].FailureCode);
    }

    [Fact]
    public async Task WindowBoundaryRequeuesOnceAndResumesOnNextRunWithoutBusyLoop()
    {
        var memory = new MemoryStore(); var calls = 0;
        var executor = new FakeExecutor((_, _, _) => ++calls == 1 ? Task.FromException(new TransferWindowClosedException()) : Task.CompletedTask);
        var clock = new TestClock(Now);
        var queue = new QueueCoordinator(memory, executor, clock);
        await queue.InitializeAsync();
        var job = await queue.EnqueueAsync(Job() with { DailyWindow = new DailyTransferWindow(12 * 60, 13 * 60, 0) });
        await queue.RunReadyAsync();
        Assert.Equal(1, calls); Assert.Equal(QueueJobState.Queued, memory.Jobs[job.Id].State);
        clock.Now = Now.AddHours(2);
        await queue.RunReadyAsync(); Assert.Equal(1, calls);
        clock.Now = Now.AddDays(1);
        await queue.RunReadyAsync(); Assert.Equal(2, calls);
        Assert.Equal(QueueJobState.Completed, memory.Jobs[job.Id].State);
    }

    private sealed class MemoryStore : IQueueStore
    {
        public Dictionary<Guid, QueueJob> Jobs { get; } = [];
        public IReadOnlyList<QueueJob> Load() => Jobs.Values.ToArray();
        public bool FailTerminalWrites { get; set; }
        public void Save(QueueJob job)
        {
            if (FailTerminalWrites && job.State is not (QueueJobState.Queued or QueueJobState.Running)) throw new IOException("Injected terminal save failure");
            Jobs[job.Id] = job;
        }
        public void Remove(Guid id) => Jobs.Remove(id);
    }
    private sealed class FakeExecutor(Func<QueueJob, IProgress<DownloadProgress>?, CancellationToken, Task> execute) : IQueuedDownloadExecutor
    {
        private readonly object sync = new();
        public List<Guid> Executed { get; } = [];
        public async Task ExecuteAsync(QueueJob job, IProgress<DownloadProgress>? progress, CancellationToken token)
        { lock (sync) Executed.Add(job.Id); await execute(job, progress, token); }
    }
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
    // Real authenticated encryption for portable persistence tests; production always uses Windows DPAPI.
    private sealed class TestProtector : IQueuePayloadProtector, IDisposable
    {
        private readonly AesGcm cipher = new(RandomNumberGenerator.GetBytes(32), 16);
        public byte[] Protect(byte[] clear)
        {
            var result = new byte[28 + clear.Length];
            RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            cipher.Encrypt(result.AsSpan(0, 12), clear, result.AsSpan(28), result.AsSpan(12, 16));
            return result;
        }
        public byte[] Unprotect(byte[] data)
        {
            var result = new byte[data.Length - 28];
            cipher.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), result);
            return result;
        }
        public void Dispose() => cipher.Dispose();
    }
}

public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    { if (!OperatingSystem.IsWindows()) Skip = "Windows DPAPI requires a Windows user profile; run in Windows CI or desktop acceptance."; }
}
