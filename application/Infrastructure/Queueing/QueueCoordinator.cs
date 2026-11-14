using DownloadManager.Core;
using DownloadManager.Infrastructure.Persistence;

namespace DownloadManager.Infrastructure.Queueing;

public interface IQueuedDownloadExecutor
{
    Task ExecuteAsync(QueueJob job, IProgress<DownloadProgress>? progress, CancellationToken token);
}

/// <summary>Runs one complete job at a time, preserving the application's worker bound.</summary>
public sealed class QueueCoordinator(IQueueStore store, IQueuedDownloadExecutor executor, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim runner = new(1);
    private readonly Dictionary<Guid, QueueJob> jobs = [];
    private bool initialized;

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
            RequireInitialized();
            if (jobs.Count >= 1000) throw new InvalidOperationException("Remove terminal history before adding more jobs.");
            var job = request with { Destination = Path.GetFullPath(request.Destination), CreatedAt = time.GetUtcNow(), State = QueueJobState.Queued,
                CompletedBytes = 0, TotalBytes = null, FailureCode = null, Category = DownloadCategory.Normalize(request.Category), Paths = Array.AsReadOnly(request.Paths.ToArray()) };
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

    public async Task ChangeStateAsync(Guid id, QueueJobState next, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireInitialized();
            var current = jobs[id];
            if (current.State == QueueJobState.Running || next == QueueJobState.Running)
                throw new InvalidOperationException("Active jobs must stop through the runner.");
            var changed = current.Transition(next);
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
            RequireInitialized();
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
            RequireInitialized();
            foreach (var job in jobs.Values.Where(j => j.State is QueueJobState.Completed or QueueJobState.Cancelled).ToArray())
            { store.Remove(job.Id); jobs.Remove(job.Id); }
        }
        finally { gate.Release(); }
    }

    public async Task RunReadyAsync(Action<Guid, DownloadProgress>? onProgress = null, CancellationToken token = default)
    {
        if (!await runner.WaitAsync(0, token)) throw new InvalidOperationException("The queue already has a runner.");
        try
        {
            while (!token.IsCancellationRequested)
            {
                QueueJob? job;
                await gate.WaitAsync(token);
                try
                {
                    RequireInitialized();
                    job = QueueScheduling.Next(jobs.Values, time.GetUtcNow());
                    if (job is null) return;
                    job = job.Transition(QueueJobState.Running);
                    store.Save(job); jobs[job.Id] = job;
                }
                finally { gate.Release(); }
                DownloadProgress? latest = null;
                var progressLock = new object();
                var reporter = new CallbackProgress(value =>
                {
                    lock (progressLock) latest = value;
                    onProgress?.Invoke(job.Id, value);
                });
                var terminal = QueueJobState.Completed;
                string? failure = null;
                try { await executor.ExecuteAsync(job, reporter, token); }
                catch (OperationCanceledException) { terminal = QueueJobState.Paused; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    terminal = QueueJobState.Failed;
                    // Persist only allowlisted categories, never exception text or URLs.
                    failure = ex switch
                    {
                        InvalidDataException => "Validation", HttpRequestException => "Network",
                        UnauthorizedAccessException or IOException => "Storage", TimeoutException => "Timeout",
                        _ => "Failure"
                    };
                }
                await gate.WaitAsync(CancellationToken.None); // Finish state persistence even after user cancellation.
                try
                {
                    lock (progressLock)
                    {
                        job = job.Transition(terminal) with
                        { CompletedBytes = latest?.CompletedBytes ?? job.CompletedBytes, TotalBytes = latest?.TotalBytes ?? job.TotalBytes, FailureCode = failure };
                    }
                    store.Save(job); jobs[job.Id] = job;
                }
                finally { gate.Release(); }
                if (terminal == QueueJobState.Paused) return;
                // A failed job never loops automatically; the next ready job may proceed.
            }
        }
        finally { runner.Release(); }
    }

    private void RequireInitialized()
    { if (!initialized) throw new InvalidOperationException("Initialize the queue first."); }

    private sealed class CallbackProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => report(value); }
}
