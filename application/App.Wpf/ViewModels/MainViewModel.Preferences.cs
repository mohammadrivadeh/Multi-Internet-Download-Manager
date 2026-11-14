using CommunityToolkit.Mvvm.Input;
using DownloadManager.Core;
namespace DownloadManager.App.ViewModels;
public partial class MainViewModel
{
    [RelayCommand]
    private async Task SaveDefaultsAsync()
    {
        if (!IsReady) return;
        if (!preferencesWritable) { Status = Text("PreferencesLoadError"); return; }
        try
        {
            var settings = new DesktopPreferences { Workers = int.Parse(WorkerCount), SpeedLimitKiB = long.Parse(SpeedLimitKiB), Priority = int.Parse(Priority), Category = Category, DestinationRoot = string.IsNullOrWhiteSpace(DestinationRoot) ? null : DestinationRoot };
            settings.Validate(); await session.SavePreferencesAsync(settings);
            Status = Text("PreferencesSavedStatus");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) { Status = Text("QueueInputError"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("PreferencesSaveError"); }
    }
    [RelayCommand]
    private async Task AssignCategoryAsync()
    {
        if (!IsReady || queue is null || SelectedJob is null) return;
        var id = SelectedJob.Job.Id;
        var chosen = Category;
        try { await Task.Run(() => queue.SetCategoryAsync(id, chosen)); await RefreshJobsAsync(); }
        catch (ArgumentException) { Status = Text("CategoryInputError"); }
        catch (InvalidOperationException) { Status = Text("QueueActionError"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
    }
}
