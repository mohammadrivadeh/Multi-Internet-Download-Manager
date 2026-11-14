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

public partial class MainViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? stop;
    private readonly IQueueSession session;
    private readonly IDesktopServices desktop;
    private QueueCoordinator? queue;
    private PlannedDestination? plannedDestination;
    private bool autoRun;
    private bool preferencesWritable;
    private long runGeneration;
    private TimeSpan lastTelemetryAt;
    private Guid? telemetryJob;
    private TransferRateEstimator estimator = new();
    private readonly Stopwatch telemetryClock = Stopwatch.StartNew();
    private readonly NetworkPathCatalog catalog = new();
    private readonly IDesktopTimer timer;
    public ObservableCollection<PathSelection> Paths { get; } = [];
    public ObservableCollection<CategoryOption> Categories { get; } = [];
    public ObservableCollection<QueueRow> Jobs { get; } = [];
    public ListCollectionView JobsView { get; }
    [ObservableProperty] private int jobCount;
    [ObservableProperty] private int completedCount;
    public bool HasVisibleJobs => !JobsView.IsEmpty;
    [ObservableProperty] private string url = "";
    [ObservableProperty] private string destinationRoot = "";
    [ObservableProperty] private string destination = "";
    [ObservableProperty] private string workerCount = "4";
    [ObservableProperty] private string expectedSha256 = "";
    [ObservableProperty] private string speedLimitKiB = "0";
    [ObservableProperty] private string category = DownloadCategory.Default;
    [ObservableProperty] private string categoryFilter = "";
    [ObservableProperty] private string priority = "0";
    [ObservableProperty] private string windowStart = "";
    [ObservableProperty] private string windowEnd = "";
    [ObservableProperty] private string windowOffset = DateTimeOffset.Now.ToString("zzz");
    [ObservableProperty] private string scheduledStart = "";
    [ObservableProperty] private bool useSystemRoute;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isReady;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private double percentage;
    [ObservableProperty] private bool isIndeterminate;
    [ObservableProperty] private string speedText = "—";
    [ObservableProperty] private string remainingText = "—";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string stateFilter = "All";
    [ObservableProperty] private QueueRow? selectedJob;
    private string Text(string key) => desktop.Text(key);

    public MainViewModel(IDesktopServices desktop, IQueueSession session)
    {
        this.desktop = desktop; this.session = session;
        Status = Text("ReadyStatus");
        JobsView = new ListCollectionView(Jobs) { Filter = FilterJob };
        var liveView = (System.ComponentModel.ICollectionViewLiveShaping)JobsView;
        liveView.LiveFilteringProperties.Add(nameof(QueueRow.Job));
        liveView.IsLiveFiltering = true;
        ((System.Collections.Specialized.INotifyCollectionChanged)JobsView).CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVisibleJobs));
        timer = desktop.CreateTimer(async () =>
        {
            try { await CheckScheduleAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { autoRun = false; Status = Text("QueueLoadError"); }
        });
    }

    public async Task InitializeAsync()
    {
        try
        {
            queue = await session.InitializeAsync();
            await RefreshJobsAsync();
            try
            {
                var saved = await session.LoadPreferencesAsync();
                WorkerCount = saved.Workers.ToString(); SpeedLimitKiB = saved.SpeedLimitKiB.ToString();
                Priority = saved.Priority.ToString(); Category = saved.Category; DestinationRoot = saved.DestinationRoot ?? "";
                preferencesWritable = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("PreferencesLoadError"); }
            IsReady = true;
            timer.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            session.Dispose(); queue = null;
            Status = Text("QueueLoadError");
        }
    }

    private bool FilterJob(object item)
    {
        if (item is not QueueRow row) return false;
        return (string.IsNullOrWhiteSpace(Search) || row.Filename.Contains(Search, StringComparison.CurrentCultureIgnoreCase))
            && (string.IsNullOrEmpty(CategoryFilter) || string.Equals(CategoryFilter, row.Job.Category, StringComparison.OrdinalIgnoreCase))
            && (StateFilter == "All" || StateFilter == "Active" && row.Job.State is QueueJobState.Queued or QueueJobState.Running or QueueJobState.Paused
                || StateFilter == row.Job.State.ToString());
    }
    partial void OnCategoryFilterChanged(string value) => JobsView.Refresh();
    partial void OnSearchChanged(string value) => JobsView.Refresh();
    partial void OnStateFilterChanged(string value) => JobsView.Refresh();

    private void UpdateProgress(Guid id, DownloadProgress value)
    {
        if (telemetryJob != id || value.Stage != DownloadStage.Downloading) { telemetryJob = id; estimator = new(); }
        lastTelemetryAt = telemetryClock.Elapsed;
        var sample = estimator.Sample(value.AcceptedBytes, value.TotalBytes, telemetryClock.Elapsed);
        SpeedText = value.Stage == DownloadStage.Downloading && sample.BytesPerSecond > 0 ? $"{sample.BytesPerSecond / 1024:N1} KiB/s" : "—";
        RemainingText = value.Stage == DownloadStage.Downloading && sample.Remaining is { } remaining ? remaining.ToString(@"d\.hh\:mm\:ss") : "—";
        var existing = Jobs.FirstOrDefault(row => row.Job.Id == id);
        if (existing is not null && existing.Job.State is QueueJobState.Queued or QueueJobState.Running)
        {
            existing.Update(existing.Job with { State = QueueJobState.Running, CompletedBytes = value.CompletedBytes, TotalBytes = value.TotalBytes });
            RefreshActionAvailability();
        }
        IsIndeterminate = value.TotalBytes is null;
        Percentage = value.TotalBytes is > 0 ? (double)value.AcceptedBytes / value.TotalBytes.Value * 100 : value.Stage == DownloadStage.Completed ? 100 : 0;
        if (value.Stage != DownloadStage.Downloading) estimator = new();
        Status = string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("ProgressFormat"), Text($"Stage{value.Stage}"), value.CompletedBytes, value.TotalBytes?.ToString("N0") ?? Text("UnknownTotal"));
    }

    public async Task ShutdownAsync()
    {
        IsReady = false; autoRun = false; timer.Stop(); stop?.Cancel();
        var tasks = new[] { RunQueueCommand.ExecutionTask, AddCommand.ExecutionTask, ResumeSelectedCommand.ExecutionTask,
            CancelSelectedCommand.ExecutionTask, ClearHistoryCommand.ExecutionTask, RefreshCommand.ExecutionTask, SaveDefaultsCommand.ExecutionTask, AssignCategoryCommand.ExecutionTask, RetryQueueSaveCommand.ExecutionTask };
        try { await Task.WhenAll(tasks.Where(t => t is not null).Cast<Task>()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
        if (queue is not null)
        {
            try { await Task.Run(() => queue.RetryPersistenceAsync()); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
        }
    }

    public void Dispose()
    { autoRun = false; timer.Stop(); session.Dispose(); }

    private sealed class UiProgress(IDesktopServices desktop, Action<Guid, DownloadProgress> update)
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly object sync = new();
        private (Guid, DownloadStage)? previous;
        public void Report(Guid id, DownloadProgress value)
        {
            lock (sync)
            {
                if (previous == (id, value.Stage) && watch.ElapsedMilliseconds < 100) return;
                previous = (id, value.Stage); watch.Restart();
                desktop.Post(() => update(id, value));
            }
        }
    }
}
