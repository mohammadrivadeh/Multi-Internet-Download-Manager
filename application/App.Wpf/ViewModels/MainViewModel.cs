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
    private readonly Dictionary<string, CancellationTokenSource> queueStops = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> queueRunTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> startedQueues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> scheduleStops = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<Guid>> scheduledRequeueJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly IQueueSession session;
    private readonly IDesktopServices desktop;
    private QueueCoordinator? queue;
    private QueueJob? editingJob;
    private PlannedDestination? plannedDestination;
    private bool preferencesWritable;
    private TimeSpan lastTelemetryAt;
    private readonly Dictionary<Guid, TransferRateEstimator> jobEstimators = [];
    private readonly Stopwatch telemetryClock = Stopwatch.StartNew();
    private readonly NetworkPathCatalog catalog = new();
    private readonly IDesktopTimer timer;
    public ObservableCollection<PathSelection> Paths { get; } = [];
    public ObservableCollection<CategoryOption> Categories { get; } = [];
    public ObservableCollection<DownloadQueueSettings> Queues { get; } = [];
    public ObservableCollection<QueueRow> Jobs { get; } = [];
    public ListCollectionView JobsView { get; }
    [ObservableProperty] private int jobCount;
    [ObservableProperty] private int completedCount;
    public bool HasVisibleJobs => !JobsView.IsEmpty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanChangeIdentity))] private bool isEditing;
    [ObservableProperty] private bool replaceEditedConnections;
    public bool CanChangeIdentity => !IsEditing;
    [ObservableProperty] private string url = "";
    [ObservableProperty] private string destinationRoot = "";
    [ObservableProperty] private string destination = "";
    [ObservableProperty] private string workerCount = "4";
    [ObservableProperty] private string expectedSha256 = "";
    [ObservableProperty] private string speedLimitKiB = "0";
    [ObservableProperty] private string category = DownloadCategory.Default;
    [ObservableProperty] private string categoryFilter = "";
    [ObservableProperty] private string selectedQueueName = QueueJob.MainQueue;
    [ObservableProperty] private string newQueueName = "";
    [ObservableProperty] private string moveTargetQueueName = QueueJob.MainQueue;
    [ObservableProperty] private string queueConcurrency = "1";
    [ObservableProperty] private string queueStartTime = "";
    [ObservableProperty] private string queueStopTime = "";
    [ObservableProperty] private string priority = "0";
    [ObservableProperty] private string windowStart = "";
    [ObservableProperty] private string windowEnd = "";
    [ObservableProperty] private string windowOffset = DateTimeOffset.Now.ToString("zzz");
    [ObservableProperty] private string scheduledStart = "";
    [ObservableProperty] private bool useSystemRoute;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isReady;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string currentPage = "Network Balancer";
    public bool IsBalancerPage => CurrentPage == "Network Balancer";
    public bool IsDownloadsPage => CurrentPage == "Downloads";
    public bool IsQueuePage => CurrentPage == "Queue";
    public bool IsConnectionsPage => CurrentPage == "Connections";
    public bool IsPreferencesPage => CurrentPage == "Preferences";
    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsBalancerPage));
        OnPropertyChanged(nameof(IsDownloadsPage));
        OnPropertyChanged(nameof(IsQueuePage));
        OnPropertyChanged(nameof(IsConnectionsPage));
        OnPropertyChanged(nameof(IsPreferencesPage));
    }
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
        Destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Status = Text("ReadyStatus");
        SaveEditedCommand.PropertyChanged += (_, _) => RefreshActionAvailability();
        JobsView = new ListCollectionView(Jobs) { Filter = FilterJob };
        var liveView = (System.ComponentModel.ICollectionViewLiveShaping)JobsView;
        liveView.LiveFilteringProperties.Add(nameof(QueueRow.Job));
        liveView.IsLiveFiltering = true;
        ((System.Collections.Specialized.INotifyCollectionChanged)JobsView).CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVisibleJobs));
        timer = desktop.CreateTimer(async () =>
        {
            try { await CheckScheduleAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { startedQueues.Clear(); Status = Text("QueueLoadError"); }
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
                savedPreferences = saved;
                Presentation.AppMotion.Level = saved.MotionLevel;
                ReconcileQueues(saved.Queues);
                await RefreshJobsAsync();
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
        return string.Equals(SelectedQueueName, row.Job.QueueName, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(Search) || row.Filename.Contains(Search, StringComparison.CurrentCultureIgnoreCase))
            && (string.IsNullOrEmpty(CategoryFilter) || string.Equals(CategoryFilter, row.Job.Category, StringComparison.OrdinalIgnoreCase))
            && (StateFilter == "All" || StateFilter == row.Job.State.ToString());
    }
    partial void OnCategoryFilterChanged(string value) => JobsView.Refresh();
    partial void OnSelectedQueueNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsSelectedQueueRunning));
        JobsView.Refresh();
        LoadQueueSettings(value);
    }
    public bool IsSelectedQueueRunning => queueStops.ContainsKey(SelectedQueueName);
    partial void OnSearchChanged(string value) => JobsView.Refresh();
    partial void OnStateFilterChanged(string value) => JobsView.Refresh();

    private void UpdateProgress(Guid id, DownloadProgress value)
    {
        lastTelemetryAt = telemetryClock.Elapsed;
        if (!jobEstimators.TryGetValue(id, out var jobEstimator)) jobEstimators[id] = jobEstimator = new();
        var sample = jobEstimator.Sample(value.AcceptedBytes, value.TotalBytes, telemetryClock.Elapsed);
        SpeedText = value.Stage == DownloadStage.Downloading && sample.BytesPerSecond > 0 ? $"{sample.BytesPerSecond / 1000:N1} KB/s" : "—";
        RemainingText = value.Stage == DownloadStage.Downloading && sample.Remaining is { } remaining ? remaining.ToString(@"d\.hh\:mm\:ss") : "—";
        var existing = Jobs.FirstOrDefault(row => row.Job.Id == id);
        if (existing is not null && existing.Job.State is QueueJobState.Queued or QueueJobState.Running)
        {
            var becameRunning = existing.Job.State != QueueJobState.Running;
            existing.Update(existing.Job with { State = QueueJobState.Running, CompletedBytes = value.CompletedBytes, TotalBytes = value.TotalBytes });
            existing.LiveSpeed = SpeedText;
            if (becameRunning) OnPropertyChanged(nameof(QueueStatuses));
            RefreshActionAvailability();
        }
        IsIndeterminate = value.TotalBytes is null;
        Percentage = value.TotalBytes is > 0 ? (double)value.AcceptedBytes / value.TotalBytes.Value * 100 : value.Stage == DownloadStage.Completed ? 100 : 0;
        if (value.Stage != DownloadStage.Downloading) jobEstimators.Remove(id);
        Status = string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("ProgressFormat"), Text($"Stage{value.Stage}"), value.CompletedBytes, value.TotalBytes?.ToString("N0") ?? Text("UnknownTotal"));
    }

    public async Task ShutdownAsync()
    {
        IsReady = false; startedQueues.Clear(); timer.Stop(); foreach (var cancellation in queueStops.Values) cancellation.Cancel();
        var tasks = new[] { RunQueueCommand.ExecutionTask, AddCommand.ExecutionTask, StartSelectedCommand.ExecutionTask,
            ResumeSelectedCommand.ExecutionTask, PauseSelectedCommand.ExecutionTask, CancelSelectedCommand.ExecutionTask,
            RedownloadSelectedCommand.ExecutionTask, ClearHistoryCommand.ExecutionTask, RefreshCommand.ExecutionTask, AssignCategoryCommand.ExecutionTask,
            RetryQueueSaveCommand.ExecutionTask, SaveQueueSettingsCommand.ExecutionTask, CreateQueueCommand.ExecutionTask, RenameQueueCommand.ExecutionTask,
            DeleteQueueCommand.ExecutionTask, MoveSelectedQueueCommand.ExecutionTask,
            EditSelectedCommand.ExecutionTask, SaveEditedCommand.ExecutionTask };
        try { await Task.WhenAll(tasks.Where(t => t is not null).Cast<Task>().Concat(queueRunTasks.Values.ToArray())); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
        if (queue is not null)
        {
            try { await Task.Run(() => queue.RetryPersistenceAsync()); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
        }
    }

    public void Dispose()
    { startedQueues.Clear(); timer.Stop(); foreach (var cancellation in queueStops.Values) cancellation.Cancel(); session.Dispose(); }

    private void ReconcileQueues(IEnumerable<DownloadQueueSettings> desired)
    {
        var items = desired.OrderBy(q => string.Equals(q.Name, QueueJob.MainQueue, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
        DownloadManager.App.Presentation.StableCollection.Reconcile(Queues, items, q => q.Name, q => q.Name, q => q, (_, _) => { });
        if (!Queues.Any(q => string.Equals(q.Name, SelectedQueueName, StringComparison.OrdinalIgnoreCase))) SelectedQueueName = QueueJob.MainQueue;
        LoadQueueSettings(SelectedQueueName);
    }

    private void LoadQueueSettings(string name)
    {
        var settings = Queues.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)) ?? new DownloadQueueSettings();
        QueueConcurrency = settings.MaxConcurrentDownloads == 0 ? "All" : settings.MaxConcurrentDownloads.ToString(); QueueStartTime = settings.StartTime; QueueStopTime = settings.StopTime;
    }

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
