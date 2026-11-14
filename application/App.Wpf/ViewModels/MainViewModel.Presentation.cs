using DownloadManager.Core;
using DownloadManager.App.Models;
namespace DownloadManager.App.ViewModels;
public partial class MainViewModel
{
    public bool CanEditForm => IsReady && !SaveEditedCommand.IsRunning;
    private bool CanRunQueue => IsReady && !IsBusy && !IsEditing;
    partial void OnIsBusyChanged(bool value) => RefreshActionAvailability();
    private bool CanCancelEdit => IsEditing && !SaveEditedCommand.IsRunning;
    private bool CanEditSelected => IsReady && !IsBusy && !SaveEditedCommand.IsRunning && SelectedJob?.Job.State is QueueJobState.Queued or QueueJobState.Paused or QueueJobState.Failed;
    private bool CanSaveEdited => IsReady && IsEditing;
    private bool CanAdd => IsReady && !IsEditing;
    private bool CanPickIdentity => IsReady && !IsEditing;
    partial void OnIsEditingChanged(bool value) => RefreshActionAvailability();
    private bool CanResumeSelected => IsReady && SelectedJob?.Job.State is QueueJobState.Paused or QueueJobState.Failed;
    private bool CanCancelSelected => IsReady && SelectedJob?.Job.State is QueueJobState.Queued or QueueJobState.Paused or QueueJobState.Failed;
    private void RefreshActionAvailability()
    {
        ResumeSelectedCommand.NotifyCanExecuteChanged(); CancelSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditForm)); RunQueueCommand.NotifyCanExecuteChanged();
        CancelEditCommand.NotifyCanExecuteChanged(); EditSelectedCommand.NotifyCanExecuteChanged(); SaveEditedCommand.NotifyCanExecuteChanged(); AddCommand.NotifyCanExecuteChanged();
        BrowseCommand.NotifyCanExecuteChanged(); ChooseRootCommand.NotifyCanExecuteChanged(); SuggestDestinationCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedJobChanged(QueueRow? value) => RefreshActionAvailability();
    partial void OnIsReadyChanged(bool value) => RefreshActionAvailability();
    private void RefreshStallDisplay()
    {
        if (telemetryClock.Elapsed - lastTelemetryAt >= TimeSpan.FromSeconds(5))
        { SpeedText = "—"; RemainingText = "—"; estimator = new(); }
    }
}
