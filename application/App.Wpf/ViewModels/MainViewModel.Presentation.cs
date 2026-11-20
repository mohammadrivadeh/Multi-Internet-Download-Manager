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
    public bool CanOpenNewDownload => IsReady && !IsEditing;
    private bool CanAdd => CanOpenNewDownload;
    private bool CanPickIdentity => IsReady && !IsEditing;
    partial void OnIsEditingChanged(bool value) => RefreshActionAvailability();
    private bool CanResumeSelected => IsReady && SelectedJob?.Job.State is QueueJobState.Paused or QueueJobState.Failed;
    private bool CanCancelSelected => IsReady && SelectedJob?.Job.State is QueueJobState.Queued or QueueJobState.Paused or QueueJobState.Failed or QueueJobState.Running;
    private bool CanStartSelected => IsReady && SelectedJob?.Job.State is QueueJobState.Queued;
    private bool CanPauseSelected => IsReady && SelectedJob?.Job.State is QueueJobState.Queued or QueueJobState.Running;
    private bool CanRedownloadSelected => IsReady && SelectedJob?.Job.State is QueueJobState.Completed or QueueJobState.Cancelled or QueueJobState.Paused or QueueJobState.Failed;
    private void RefreshActionAvailability()
    {
        OnPropertyChanged(nameof(CanOpenNewDownload));
        ResumeSelectedCommand.NotifyCanExecuteChanged(); CancelSelectedCommand.NotifyCanExecuteChanged();
        StartSelectedCommand.NotifyCanExecuteChanged(); PauseSelectedCommand.NotifyCanExecuteChanged();
        RedownloadSelectedCommand.NotifyCanExecuteChanged();
        RunQueueCommand.NotifyCanExecuteChanged();
        EditSelectedCommand.NotifyCanExecuteChanged();
        SaveEditedCommand.NotifyCanExecuteChanged();
        CancelEditCommand.NotifyCanExecuteChanged();
        AddCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedJobChanged(QueueRow? value) => RefreshActionAvailability();
    partial void OnIsReadyChanged(bool value) => RefreshActionAvailability();
    private void RefreshStallDisplay()
    {
        if (telemetryClock.Elapsed - lastTelemetryAt >= TimeSpan.FromSeconds(5))
        { SpeedText = "—"; RemainingText = "—"; jobEstimators.Clear(); }
    }
}
