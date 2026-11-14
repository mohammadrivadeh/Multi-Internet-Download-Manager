using DownloadManager.Core;
using DownloadManager.App.Models;
namespace DownloadManager.App.ViewModels;
public partial class MainViewModel
{
    private bool CanResumeSelected => IsReady && SelectedJob?.Job.State is QueueJobState.Paused or QueueJobState.Failed;
    private bool CanCancelSelected => IsReady && SelectedJob?.Job.State is QueueJobState.Queued or QueueJobState.Paused or QueueJobState.Failed;
    private void RefreshActionAvailability()
    { ResumeSelectedCommand.NotifyCanExecuteChanged(); CancelSelectedCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedJobChanged(QueueRow? value) => RefreshActionAvailability();
    partial void OnIsReadyChanged(bool value) => RefreshActionAvailability();
    private void RefreshStallDisplay()
    {
        if (telemetryClock.Elapsed - lastTelemetryAt >= TimeSpan.FromSeconds(5))
        { SpeedText = "—"; RemainingText = "—"; estimator = new(); }
    }
}
