using DownloadManager.Core;
using DownloadManager.Infrastructure.Persistence;

namespace DownloadManager.Infrastructure.Queueing;

public interface IQueuedDownloadExecutor
{
    Task ExecuteAsync(QueueJob job, IProgress<DownloadProgress>? progress, CancellationToken token);
}

/// <summary>Runs one complete job at a time within each named queue.</summary>
public sealed class QueueCoordinator(IQueueStore store, IQueuedDownloadExecutor executor, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> runners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, QueueJob> jobs = [];
    private bool initialized;
    private readonly Dictionary<Guid, QueueJob> pendingWrites = [];

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            if (initialized) return;
            var loaded = store.Load();
            var restored = new Dictionary<Guid, QueueJob>();
            foreach (var saved in loaded)
            {
                var job = saved;
                // A previous process cannot still own these writers in this queue instance.
                // Never automatically restart interrupted or failed work.
                if (job.State == QueueJobState.Running)
                { job = job.Transition(QueueJobState.Paused); store.Save(job); }
                restored.Add(job.Id, job);
            }
            foreach (var row in restored) jobs.Add(row.Key, row.Value);
            initialized = true;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<QueueJob>> SnapshotAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { RequireInitialized(); return jobs.Values.OrderBy(j => j.CreatedAt).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task<QueueJob> EnqueueAsync(QueueJob request, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireWritable();
            if (jobs.Count >= 1000) throw new InvalidOperationException("Remove terminal history before adding more jobs.");
            var job = request with { Destination = Path.GetFullPath(request.Destination), CreatedAt = time.GetUtcNow(), State = QueueJobState.Queued,
                CompletedBytes = 0, TotalBytes = null, FailureCode = null, ResumeDifferences = CheckpointDifferences.None,
                Category = DownloadCategory.Normalize(request.Category), Paths = Array.AsReadOnly(request.Paths.ToArray()) };
            job.Validate();
            if (jobs.ContainsKey(job.Id)) throw new InvalidOperationException("Job identity already exists.");
            if (jobs.Values.Any(j => j.State is not (QueueJobState.Cancelled or QueueJobState.Completed)
                && string.Equals(j.Destination, job.Destination, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Another unfinished job owns this destination.");
            store.Save(job);
            jobs.Add(job.Id, job);
            return job;
        }
        finally { gate.Release(); }
    }

    public async Task SetQueueNameAsync(Guid id, string queueName, CancellationToken token = default)
    {
        queueName = ValidateQueueName(queueName);
        await gate.WaitAsync(token);
        try
        {
            RequireWritable();
            var current = jobs[id];
            if (current.State == QueueJobState.Running) throw new InvalidOperationException("Stop the queue before moving a running download.");
            var changed = current with { QueueName = queueName };
            store.Save(changed); jobs[id] = changed;
        }
        finally { gate.Release(); }
    }

    public async Task RenameQueueAsync(string oldName, string newName, CancellationToken token = default)
    {
        oldName = ValidateQueueName(oldName); newName = ValidateQueueName(newName);
        await gate.WaitAsync(token);
        try
        {
            RequireWritable();
            var affected = jobs.Values.Where(j => string.Equals(j.QueueName, oldName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (affected.Any(j => j.State == QueueJobState.Running)) throw new InvalidOperationException("Stop this queue before renaming it.");
            var changed = affected.Select(j => j with { QueueName = newName }).ToArray();
            store.SaveMany(changed);
            foreach (var job in changed) jobs[job.Id] = job;
        }
        finally { gate.Release(); }
    }

    public async Task MoveQueueJobsAsync(string oldName, string newName, CancellationToken token = default)
    {
        oldName = ValidateQueueName(oldName); newName = ValidateQueueName(newName);
        await gate.WaitAsync(token);
        try
        {
            RequireWritable();
            var affected = jobs.Values.Where(j => string.Equals(j.QueueName, oldName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (affected.Any(j => j.State == QueueJobState.Running)) throw new InvalidOperationException("Stop this queue before moving its downloads.");
            var changed = affected.Select(j => j with { QueueName = newName }).ToArray();
            store.SaveMany(changed);
            foreach (var job in changed) jobs[job.Id] = job;
        }
        finally { gate.Release(); }
    }

    public async Task ChangeStateAsync(Guid id, QueueJobState next, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireWritable();
            var current = jobs[id];
            if (current.State == QueueJobState.Running && next is QueueJobState.Paused or QueueJobState.Cancelled &&
                activeJobs.TryGetValue(id, out var active))
            {
                active.Stop(next);
                return;
            }
            if (current.State == QueueJobState.Running || next == QueueJobState.Running)
                throw new InvalidOperationException("Active jobs must stop through the runner.");
            var changed = current.Transition(next);
            store.Save(changed);
            jobs[id] = changed;
        }
        finally { gate.Release(); }
    }

    private readonly Dictionary<Guid, ActiveJob> activeJobs = new();

    private sealed class ActiveJob(CancellationToken queueToken) : IDisposable
    {
        private readonly CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(queueToken);
        public CancellationToken Token => source.Token;
        public QueueJobState? RequestedState { get; private set; }
        public Task Cancellation { get; private set; } = Task.CompletedTask;

        public void Stop(QueueJobState state)
        {
            if (RequestedState == QueueJobState.Cancelled)
                return;
            RequestedState = state;
            if (!source.IsCancellationRequested)
                Cancellation = source.CancelAsync();
        }

        public void Dispose() => source.Dispose();
    }

    public async Task UpdateSettingsAsync(Guid id, QueueJobSettings settings, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireWritable();
            var changed = settings.Apply(jobs[id]);
            store.Save(changed);
            jobs[id] = changed;
        }
        finally { gate.Release(); }
    }

    public async Task SetCategoryAsync(Guid id, string category, CancellationToken token = default)
    {
        category = DownloadCategory.Normalize(category);
        await gate.WaitAsync(token);
        try
        {
            RequireWritable();
            var current = jobs[id];
            if (current.State == QueueJobState.Running) throw new InvalidOperationException("Stop the active job before changing its category.");
            var changed = current with { Category = category };
            store.Save(changed); jobs[id] = changed;
        }
        finally { gate.Release(); }
    }

    public async Task ClearTerminalHistoryAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireWritable();
            foreach (var job in jobs.Values.Where(j => j.State is QueueJobState.Completed or QueueJobState.Cancelled).ToArray())
            { store.Remove(job.Id); jobs.Remove(job.Id); }
        }
        finally { gate.Release(); }
    }

    public async Task RunReadyAsync(Action<Guid, DownloadProgress>? onProgress = null, CancellationToken token = default)
        => await RunReadyAsync(QueueJob.MainQueue, onProgress, token);

    public async Task RunReadyAsync(string queueName, Action<Guid, DownloadProgress>? onProgress = null, CancellationToken token = default)
        => await RunReadyAsync(queueName, 1, onProgress, token);

    public async Task RunReadyAsync(string queueName, int maxConcurrentDownloads, Action<Guid, DownloadProgress>? onProgress = null, CancellationToken token = default)
    {
        queueName = ValidateQueueName(queueName);
        if (maxConcurrentDownloads is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxConcurrentDownloads));
        var runner = runners.GetOrAdd(queueName, _ => new SemaphoreSlim(1));
        if (!await runner.WaitAsync(0, token)) throw new InvalidOperationException("This queue already has a runner.");
        try
        {
            await Task.WhenAll(Enumerable.Range(0, maxConcurrentDownloads).Select(_ => RunQueueWorkerAsync(queueName, onProgress, token)));
        }
        finally { runner.Release(); }
    }

    private async Task RunQueueWorkerAsync(string queueName, Action<Guid, DownloadProgress>? onProgress, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            QueueJob? job;
            ActiveJob active;
            await gate.WaitAsync(token);
            try
            {
                RequireWritable();
                job = QueueScheduling.Next(jobs.Values.Where(j => string.Equals(j.QueueName, queueName, StringComparison.OrdinalIgnoreCase)), time.GetUtcNow());
                if (job is null) return;
                job = job.Transition(QueueJobState.Running);
                store.Save(job); jobs[job.Id] = job;
                active = new ActiveJob(token);
                activeJobs.Add(job.Id, active);
            }
            finally { gate.Release(); }
            DownloadProgress? latest = null;
            DownloadManager.Infrastructure.Diagnostics.LocalDiagnostics.Trace("download-start",
                $"job={job.Id:N} route={(job.UseSystemRoute ? "system" : "bound")} paths={job.Paths.Count} workers={job.Options.Workers}");
            var progressLock = new object();
            var reporter = new CallbackProgress(value =>
            {
                lock (progressLock)
                {
                    if (latest?.Stage != value.Stage)
                        DownloadManager.Infrastructure.Diagnostics.LocalDiagnostics.Trace("download-stage",
                            $"job={job.Id:N} stage={value.Stage} bytes={value.CompletedBytes} total={value.TotalBytes}");
                    latest = value;
                }
                onProgress?.Invoke(job.Id, value);
            });
            var terminal = QueueJobState.Completed;
            string? failure = null;
            var resumeDifferences = CheckpointDifferences.None;
            try { await executor.ExecuteAsync(job, reporter, active.Token); }
            catch (TransferWindowClosedException) { terminal = active.Token.IsCancellationRequested ? QueueJobState.Paused : QueueJobState.Queued; }
            catch (OperationCanceledException) { terminal = QueueJobState.Paused; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                terminal = QueueJobState.Failed;
                if (ex is InvalidDataException && ex.Data[nameof(CheckpointDifferences)] is CheckpointDifferences changed)
                    resumeDifferences = changed;
                var reason = resumeDifferences == CheckpointDifferences.None ? "" : $" checkpoint_mismatch={resumeDifferences.ToString().Replace(", ", "|")}";
                DownloadManager.Infrastructure.Diagnostics.LocalDiagnostics.Record("download-failed", ex, $"job={job.Id:N} stage={latest?.Stage}{reason}");
                failure = ex switch
                {
                    InvalidDataException => "Validation", HttpRequestException or HttpIOException or EndOfStreamException => "Network",
                    UnauthorizedAccessException or IOException => "Storage", TimeoutException => "Timeout",
                    _ => "Failure"
                };
            }
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                activeJobs.Remove(job.Id);
                if (terminal == QueueJobState.Paused && active.RequestedState is { } requested)
                    terminal = requested;
                lock (progressLock)
                    job = job.Transition(terminal) with { CompletedBytes = latest?.CompletedBytes ?? job.CompletedBytes, TotalBytes = latest?.TotalBytes ?? job.TotalBytes,
                        CheckpointCleanupPending = latest?.CheckpointCleanupPending ?? false, FailureCode = failure, ResumeDifferences = resumeDifferences };
                jobs[job.Id] = job; pendingWrites[job.Id] = job;
                store.Save(job); pendingWrites.Remove(job.Id);
            }
            finally
            {
                gate.Release();
                try { await active.Cancellation; }
                finally { active.Dispose(); }
            }
            if (terminal == QueueJobState.Queued || terminal == QueueJobState.Paused && active.RequestedState is null) return;
        }
    }

    public async Task RetryPersistenceAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { RequireWritable(); }
        finally { gate.Release(); }
    }

    private void RequireWritable()
    {
        RequireInitialized();
        foreach (var pending in pendingWrites.Values.ToArray())
        { store.Save(pending); pendingWrites.Remove(pending.Id); }
    }

    private void RequireInitialized()
    { if (!initialized) throw new InvalidOperationException("Initialize the queue first."); }

    private static string ValidateQueueName(string value)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length is < 1 or > 48 || value.Any(char.IsControl)) throw new ArgumentException("Queue names must contain 1–48 printable characters.");
        return value;
    }

    private sealed class CallbackProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }
}
