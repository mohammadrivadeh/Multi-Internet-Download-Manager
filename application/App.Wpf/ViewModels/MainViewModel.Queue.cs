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
    [RelayCommand]
    private void Pause()
    { autoRun = false; stop?.Cancel(); }

    [RelayCommand]
    private async Task RunQueueAsync()
    {
        if (!IsReady || queue is null || IsBusy) return;
        IsBusy = true; autoRun = true;
        Percentage = 0; IsIndeterminate = true;
        Status = Text("PreparingStatus");
        stop = new();
        try
        {
            var generation = ++runGeneration;
            var reporter = new UiProgress(desktop, (id, value) => { if (IsBusy && generation == runGeneration) UpdateProgress(id, value); });
            await Task.Run(() => queue.RunReadyAsync(reporter.Report, stop.Token));
            Status = stop.IsCancellationRequested ? Text("PausedStatus") : Text("QueueWaitingStatus");
        }
        catch (OperationCanceledException) { Status = Text("PausedStatus"); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { autoRun = false; Status = Text("QueueSaveError"); }
        finally
        {
            stop.Dispose(); stop = null; IsBusy = false; IsIndeterminate = false;
            await RefreshJobsAsync();
        }
    }

    [RelayCommand]
    private async Task ResumeSelectedAsync() => await ChangeSelectedAsync(QueueJobState.Queued);
    [RelayCommand]
    private async Task CancelSelectedAsync() => await ChangeSelectedAsync(QueueJobState.Cancelled);
    private async Task ChangeSelectedAsync(QueueJobState state)
    {
        if (queue is null || SelectedJob is null) return;
        var id = SelectedJob.Job.Id;
        try { await Task.Run(() => queue.ChangeStateAsync(id, state)); await RefreshJobsAsync(); }
        catch (InvalidOperationException) { Status = Text("QueueActionError"); }
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
            Jobs.Clear();
            foreach (var job in snapshot.OrderByDescending(j => j.CreatedAt)) Jobs.Add(new(job, desktop));
            SelectedJob = Jobs.FirstOrDefault(j => j.Job.Id == selected);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueLoadError"); }
    }

    private async Task CheckScheduleAsync()
    {
        if (!IsReady || queue is null) return;
        if (IsBusy) { await RefreshJobsAsync(); return; }
        if (!autoRun) return;
        var jobs = await queue.SnapshotAsync();
        if (autoRun && IsReady && QueueScheduling.Next(jobs, DateTimeOffset.UtcNow) is not null)
            await RunQueueCommand.ExecuteAsync(null);
    }

}
