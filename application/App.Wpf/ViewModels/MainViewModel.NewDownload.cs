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
    [RelayCommand(CanExecute = nameof(CanPickIdentity))]
    private void Browse()
    {
        if (desktop.ChooseDestination() is { } chosen) Destination = chosen;
    }

    [RelayCommand(CanExecute = nameof(CanPickIdentity))]
    private void ChooseRoot()
    { if (desktop.ChooseFolder() is { } root) DestinationRoot = root; }

    [RelayCommand(CanExecute = nameof(CanPickIdentity))]
    private void SuggestDestination()
    {
        try
        {
            plannedDestination = DestinationRules.Plan(DestinationRoot, Category, new Uri(Url, UriKind.Absolute));
            Destination = plannedDestination.FullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.IO.IOException)
        { plannedDestination = null; Status = Text("DestinationRuleError"); }
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        if (!IsReady || IsEditing || queue is null) return;
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
                Paths = UseSystemRoute ? [] : Paths.Where(p => p.Selected).Select(p => p.Capture()).ToArray(),
                Options = new DownloadOptions { Workers = workers, BytesPerSecondLimit = speedKiB == 0 ? null : speedKiB * 1024, ExpectedSha256 = string.IsNullOrWhiteSpace(ExpectedSha256) ? null : ExpectedSha256.Trim() },
                DailyWindow = DailyTransferWindow.Parse(WindowStart, WindowEnd, WindowOffset), Category = DownloadCategory.Normalize(Category), Priority = priorityValue, NotBefore = QueueScheduling.ParseStart(ScheduledStart)
            };
            job.Validate();
            var plan = plannedDestination;
            if (plan is not null && string.Equals(System.IO.Path.GetFullPath(job.Destination), plan.FullPath, StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => DestinationDirectory.CreateFor(plan));
            await Task.Run(() => queue.EnqueueAsync(job));
            await RefreshJobsAsync();
            Status = Text("AddedStatus");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { Status = Text("QueueInputError"); }
        catch (InvalidOperationException) { Status = Text("QueueConflictError"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
    }

}
