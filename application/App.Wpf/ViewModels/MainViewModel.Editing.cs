using CommunityToolkit.Mvvm.Input;
using DownloadManager.Core;
namespace DownloadManager.App.ViewModels;
public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task EditSelectedAsync()
    {
        if (!CanEditSelected || SelectedJob is null) return;
        var job = SelectedJob.Job;
        await RefreshCommand.ExecuteAsync(null);
        editingJob = job; plannedDestination = null;
        Url = job.Source.AbsoluteUri; Destination = job.Destination; ExpectedSha256 = job.Options.ExpectedSha256 ?? "";
        WorkerCount = job.Options.Workers.ToString(); SpeedLimitKiB = ((job.Options.BytesPerSecondLimit ?? 0) / 1024m).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Priority = job.Priority.ToString(); Category = job.Category; UseSystemRoute = job.UseSystemRoute;
        ScheduledStart = job.NotBefore?.ToString("yyyy-MM-dd'T'HH:mm:sszzz") ?? "";
        WindowStart = job.DailyWindow is { } w ? $"{w.StartMinute / 60:00}:{w.StartMinute % 60:00}" : "";
        WindowEnd = job.DailyWindow is { } e ? $"{e.EndMinute / 60:00}:{e.EndMinute % 60:00}" : "";
        if (job.DailyWindow is { } zone) WindowOffset = new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.FromMinutes(zone.OffsetMinutes)).ToString("zzz");
        foreach (var path in Paths)
        {
            var saved = job.Paths.FirstOrDefault(p => p.AdapterId == path.Path.AdapterId && p.SourceAddress == path.Path.SourceAddress.ToString());
            path.Selected = saved is not null;
            path.SpeedLimitKiB = ((saved?.BytesPerSecondLimit ?? 0) / 1024d).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        ReplaceEditedConnections = false; IsEditing = true; Status = Text("EditLoadedStatus");
    }
    [RelayCommand(CanExecute = nameof(CanSaveEdited))]
    private async Task SaveEditedAsync()
    {
        if (!CanSaveEdited || editingJob is null || queue is null) return;
        try
        {
            if (!int.TryParse(WorkerCount, out var workers) || !int.TryParse(Priority,out var priorityValue)
                || !decimal.TryParse(SpeedLimitKiB, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var speed) || speed is < 0 or > 1048576 || speed * 1024 != decimal.Truncate(speed * 1024)) throw new ArgumentException();
            var id = editingJob.Id;
            var paths = UseSystemRoute ? [] : ReplaceEditedConnections ? DownloadAdapters?.ToArray() ?? [] : editingJob.Paths;
            if (!UseSystemRoute && paths.Count == 0) { Status = SelectAdaptersMessage; return; }
            var settings = new QueueJobSettings
            {
                Workers = Math.Max(workers, paths.Count), BytesPerSecondLimit = speed == 0 ? null : (long)(speed * 1024),
                Category = Category, Priority = priorityValue, NotBefore = QueueScheduling.ParseStart(ScheduledStart),
                DailyWindow = DailyTransferWindow.Parse(WindowStart,WindowEnd,WindowOffset), UseSystemRoute = UseSystemRoute,
                Paths = paths
            };
            await Task.Run(() => queue.UpdateSettingsAsync(id, settings));
            editingJob = null; IsEditing = false; ReplaceEditedConnections = false;
            await RefreshJobsAsync(); Status = Text("EditSavedStatus");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { Status = Text("QueueInputError"); }
        catch (InvalidOperationException) { Status = Text("QueueActionError"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
    }
    [RelayCommand(CanExecute = nameof(CanCancelEdit))]
    private void CancelEdit()
    {
        editingJob = null; IsEditing = false; ReplaceEditedConnections = false;
        Url = ""; Destination = ""; ExpectedSha256 = ""; Status = Text("EditCancelledStatus");
    }
}
