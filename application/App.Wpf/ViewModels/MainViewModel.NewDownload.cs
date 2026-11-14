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
    private void Browse()
    {
        if (desktop.ChooseDestination() is { } chosen) Destination = chosen;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (!IsReady || queue is null) return;
        try
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var source) || string.IsNullOrWhiteSpace(Destination)
                || !int.TryParse(WorkerCount, out var workers) || !int.TryParse(Priority, out var priorityValue)
                || !long.TryParse(SpeedLimitKiB, out var speedKiB) || speedKiB is < 0 or > 1048576)
                throw new ArgumentException();
            if (File.Exists(Destination)) { Status = Text("DestinationExistsStatus"); return; }
            var job = new QueueJob
            {
                Source = source, Destination = Destination, UseSystemRoute = UseSystemRoute,
                Paths = Paths.Where(p => p.Selected).Select(p => new QueuedPath(p.Path.AdapterId, p.Path.SourceAddress.ToString())).ToArray(),
                Options = new DownloadOptions { Workers = workers, BytesPerSecondLimit = speedKiB == 0 ? null : speedKiB * 1024, ExpectedSha256 = string.IsNullOrWhiteSpace(ExpectedSha256) ? null : ExpectedSha256.Trim() },
                Priority = priorityValue, NotBefore = QueueScheduling.ParseStart(ScheduledStart)
            };
            job.Validate();
            await Task.Run(() => queue.EnqueueAsync(job));
            await RefreshJobsAsync();
            Status = Text("AddedStatus");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { Status = Text("QueueInputError"); }
        catch (InvalidOperationException) { Status = Text("QueueConflictError"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
    }

}
