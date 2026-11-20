using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using DownloadManager.App.Services;
using DownloadManager.App.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Networking;
using DownloadManager.Infrastructure.Persistence;
using DownloadManager.Infrastructure.Queueing;


namespace DownloadManager.App.ViewModels;

public partial class MainViewModel
{
    public IReadOnlyDictionary<string, string> QueueStatuses
    {
        get
        {
            var statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in Jobs.GroupBy(row => row.Job.QueueName, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Any(row => row.Job.State == QueueJobState.Running))
                    statuses[group.Key] = "Running";
                else if (!startedQueues.Contains(group.Key) &&
                         group.Any(row => row.Job.State == QueueJobState.Paused))
                    statuses[group.Key] = "Paused";
            }
            foreach (var name in pausedQueues)
                statuses.TryAdd(name, "Paused");
            return statuses;
        }
    }

    private readonly HashSet<string> pausedQueues = new(StringComparer.OrdinalIgnoreCase);
    [RelayCommand]
    private void Pause() => PauseQueue(SelectedQueueName);

    internal void PauseQueue(string name)
    {
        startedQueues.Remove(name);
        pausedQueues.Add(name);
        scheduleStops.Remove(name); scheduledRequeueJobs.Remove(name);
        if (queueStops.TryGetValue(name, out var cancellation)) cancellation.Cancel();
        OnPropertyChanged(nameof(IsSelectedQueueRunning));
        OnPropertyChanged(nameof(QueueStatuses));
    }

    [RelayCommand(CanExecute = nameof(CanRunQueue))]
    private async Task RunQueueAsync()
        => await StartQueueAsync(SelectedQueueName, waitForCompletion: true, resumePaused: true);

    internal async Task StartQueueAsync(string name, bool waitForCompletion = false, bool resumePaused = false)
    {
        if (!IsReady || queue is null) return;
        var settings = Queues.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));
        if (settings is null) return;
        startedQueues.Add(settings.Name);
        pausedQueues.Remove(settings.Name);
        OnPropertyChanged(nameof(QueueStatuses));
        if (resumePaused)
        {
            if (queueStops.TryGetValue(settings.Name, out var stopping) && stopping.IsCancellationRequested &&
                queueRunTasks.TryGetValue(settings.Name, out var finishing))
                await finishing;
            if (!startedQueues.Contains(settings.Name)) return;
            try
            {
                foreach (var job in await queue.SnapshotAsync())
                {
                    if (!startedQueues.Contains(settings.Name)) return;
                    if (job.State == QueueJobState.Paused && string.Equals(job.QueueName, settings.Name, StringComparison.OrdinalIgnoreCase))
                        await queue.ChangeStateAsync(job.Id, QueueJobState.Queued);
                }
                await RefreshJobsAsync();
                if (!startedQueues.Contains(settings.Name)) return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Status = Text("QueueSaveError");
                return;
            }
        }
        if (!settings.IsWithinSchedule(DateTime.Now))
        { Status = string.Format(Text("QueueScheduledStatus"), settings.StartTime, settings.StopTime); return; }
        var running = StartQueueRunnerAsync(settings);
        if (waitForCompletion) await running;
    }

    private Task StartQueueRunnerAsync(DownloadQueueSettings settings)
    {
        if (queueRunTasks.ContainsKey(settings.Name)) return queueRunTasks[settings.Name];
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queueRunTasks[settings.Name] = completion.Task;
        _ = RunAsync();
        return completion.Task;

        async Task RunAsync()
        {
            try { await RunQueueForAsync(settings); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
            finally { queueRunTasks.Remove(settings.Name); completion.TrySetResult(); }
        }
    }

    private async Task RunQueueForAsync(DownloadQueueSettings settings)
    {
        var name = settings.Name;
        if (!IsReady || queue is null || queueStops.ContainsKey(name)) return;
        var cancellation = new CancellationTokenSource();
        queueStops[name] = cancellation;
        IsBusy = true; OnPropertyChanged(nameof(IsSelectedQueueRunning));
        Percentage = 0; IsIndeterminate = true; Status = string.Format(Text("PreparingQueueStatus"), name);
        try
        {
            var reporter = new UiProgress(desktop, UpdateProgress);
            var parallelism = settings.MaxConcurrentDownloads;
            if (parallelism == 0)
            {
                var snapshot = await queue.SnapshotAsync(cancellation.Token);
                parallelism = Math.Clamp(snapshot.Count(j => string.Equals(j.QueueName, name, StringComparison.OrdinalIgnoreCase) && j.State == QueueJobState.Queued), 1, 32);
            }
            await Task.Run(() => queue.RunReadyAsync(name, parallelism, reporter.Report, cancellation.Token));
            Status = cancellation.IsCancellationRequested ? string.Format(Text("QueuePausedStatus"), name) : string.Format(Text("QueueWaitingNamedStatus"), name);
        }
        catch (OperationCanceledException) { Status = Text("PausedStatus"); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { startedQueues.Remove(name); Status = Text("QueueSaveError"); }
        finally
        {
            queueStops.Remove(name); cancellation.Dispose(); IsBusy = queueStops.Count > 0; IsIndeterminate = queueStops.Count > 0;
            OnPropertyChanged(nameof(IsSelectedQueueRunning));
            if (queueStops.Count == 0) { SpeedText = "—"; RemainingText = "—"; }
            await RefreshJobsAsync();
            if (scheduleStops.Remove(name) && queue is not null)
            {
                try
                {
                    HashSet<Guid> requeueIds = scheduledRequeueJobs.Remove(name, out var captured) ? captured : [];
                    var paused = (await queue.SnapshotAsync()).Where(j => requeueIds.Contains(j.Id) && j.State == QueueJobState.Paused).ToArray();
                    foreach (var job in paused) await Task.Run(() => queue.ChangeStateAsync(job.Id, QueueJobState.Queued));
                    if (paused.Length > 0) await RefreshJobsAsync();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanResumeSelected))]
    private async Task ResumeSelectedAsync()
    {
        var name = SelectedJob?.Job.QueueName;
        if (name is not null && await ChangeSelectedAsync(QueueJobState.Queued)) await StartQueueAsync(name);
    }
    [RelayCommand(CanExecute = nameof(CanStartSelected))]
    private async Task StartSelectedAsync()
    {
        if (SelectedJob?.Job.QueueName is { } name) await StartQueueAsync(name);
    }
    [RelayCommand(CanExecute = nameof(CanPauseSelected))]
    private async Task PauseSelectedAsync() => await ChangeSelectedAsync(QueueJobState.Paused);
    [RelayCommand(CanExecute = nameof(CanCancelSelected))]
    private async Task CancelSelectedAsync() => await ChangeSelectedAsync(QueueJobState.Cancelled);
    private async Task<bool> ChangeSelectedAsync(QueueJobState state)
    {
        if (queue is null || SelectedJob is null) return false;
        var id = SelectedJob.Job.Id;
        try { await Task.Run(() => queue.ChangeStateAsync(id, state)); await RefreshJobsAsync(); return true; }
        catch (InvalidOperationException) { Status = Text("QueueActionError"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanRedownloadSelected))]
    private async Task RedownloadSelectedAsync()
    {
        if (queue is null || SelectedJob?.Job is not { } previous || desktop.ChooseDestination() is not { } destination) return;
        try
        {
            destination = Path.GetFullPath(destination);
            if (string.Equals(destination, Path.GetFullPath(previous.Destination), StringComparison.OrdinalIgnoreCase) ||
                File.Exists(destination) || File.Exists(destination + ".part") || File.Exists(destination + ".part.sqlite"))
            {
                Status = "Choose a new destination for a fresh download.";
                return;
            }
            var job = previous with
            {
                Id = Guid.NewGuid(), Destination = destination, State = QueueJobState.Queued,
                CompletedBytes = 0, TotalBytes = null, FailureCode = null, CheckpointCleanupPending = false, NotBefore = null,
                UseSystemRoute = true, Paths = [], Options = previous.Options with { BytesPerSecondLimit = null }
            };
            await queue.EnqueueAsync(job);
            await RefreshJobsAsync();
            await StartQueueAsync(job.QueueName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
    }

    [RelayCommand]
    private async Task RetryQueueSaveAsync()
    {
        if (!IsReady || queue is null) return;
        try { await Task.Run(() => queue.RetryPersistenceAsync()); await RefreshJobsAsync(); Status = Text("QueueSavedStatus"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (queue is null) return;
        try { await Task.Run(() => queue.ClearTerminalHistoryAsync()); await RefreshJobsAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
    }

    private async Task RefreshJobsAsync()
    {
        if (queue is null) return;
        try
        {
            var snapshot = await queue.SnapshotAsync();
            var selected = SelectedJob?.Job.Id;
            var selectedCategory = CategoryFilter;
            var desiredCategories = new[] { new CategoryOption("", Text("AllCategoriesLabel")) }
                .Concat(snapshot.Select(j => j.Category).Concat(savedPreferences.CategoryDefaults.Select(item => item.Category))
                    .Append(savedPreferences.Category).Append(DownloadCategory.Default)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).Select(n => new CategoryOption(n, n))).ToArray();
            if (!Categories.SequenceEqual(desiredCategories))
            {
                DownloadManager.App.Presentation.StableCollection.Reconcile(Categories, desiredCategories, c => c.Key, c => c.Key, c => c, (_, _) => { });
                OnPropertyChanged(nameof(DownloadCategories));
            }
            CategoryFilter = Categories.Any(c => c.Key == selectedCategory) ? selectedCategory : "";
            DownloadManager.App.Presentation.StableCollection.Reconcile(Jobs, snapshot.OrderByDescending(j => j.CreatedAt).ToArray(),
                row => row.Job.Id, job => job.Id, job => new QueueRow(job, desktop), (row, job) => row.Update(job));
            JobCount = Jobs.Count; CompletedCount = Jobs.Count(row => row.Job.State == QueueJobState.Completed);
            OnPropertyChanged(nameof(QueueStatuses));
            RefreshActionAvailability();
            SelectedJob = Jobs.FirstOrDefault(j => j.Job.Id == selected);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueLoadError"); }
    }

    private async Task CheckScheduleAsync()
    {
        if (!IsReady || queue is null) return;
        if (IsBusy) RefreshStallDisplay();
        var jobs = await queue.SnapshotAsync();
        foreach (var name in startedQueues.ToArray())
        {
            var settings = Queues.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));
            if (settings is null) continue;
            var inWindow = settings.IsWithinSchedule(DateTime.Now);
            if (!inWindow)
            {
                if (queueStops.TryGetValue(name, out var active))
                {
                    scheduleStops.Add(name);
                    scheduledRequeueJobs[name] = jobs.Where(j => string.Equals(j.QueueName, name, StringComparison.OrdinalIgnoreCase) && j.State == QueueJobState.Running).Select(j => j.Id).ToHashSet();
                    active.Cancel();
                }
                continue;
            }
            if (queueStops.ContainsKey(name) || IsEditing) continue;
            if (QueueScheduling.Next(jobs.Where(j => string.Equals(j.QueueName, name, StringComparison.OrdinalIgnoreCase)), DateTimeOffset.UtcNow) is not null)
                _ = StartQueueRunnerAsync(settings);
        }
        await RefreshJobsAsync();
    }

}
