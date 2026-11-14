using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Networking;
using DownloadManager.Infrastructure.Persistence;
using DownloadManager.Infrastructure.Queueing;
using Microsoft.Win32;

namespace DownloadManager.App;

public partial class PathSelection(NetworkPath path) : ObservableObject
{
    public NetworkPath Path { get; } = path;
    public string Label => $"{Path.Name} · {Path.SourceAddress}";
    [ObservableProperty] private bool selected;
}

public sealed record QueueRow(QueueJob Job)
{
    public string Filename => Path.GetFileName(Job.Destination);
    public string State => (string)System.Windows.Application.Current.FindResource($"QueueState{Job.State}");
    public string Schedule => Job.NotBefore?.ToString("yyyy-MM-dd HH:mm zzz") ?? "";
    public string Progress => Job.TotalBytes is { } total ? $"{Job.CompletedBytes:N0} / {total:N0}" : $"{Job.CompletedBytes:N0}";
    public string Failure => Job.FailureCode is { } code ? (string)System.Windows.Application.Current.FindResource($"QueueFailure{code}") : "";
    public int Priority => Job.Priority;
}

public partial class MainViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? stop;
    private QueueStore? store;
    private QueueCoordinator? queue;
    private bool autoRun;
    private long runGeneration;
    private readonly NetworkPathCatalog catalog = new();
    private readonly DispatcherTimer timer;
    public ObservableCollection<PathSelection> Paths { get; } = [];
    public ObservableCollection<QueueRow> Jobs { get; } = [];
    public ListCollectionView JobsView { get; }
    [ObservableProperty] private string url = "";
    [ObservableProperty] private string destination = "";
    [ObservableProperty] private string workerCount = "4";
    [ObservableProperty] private string expectedSha256 = "";
    [ObservableProperty] private string priority = "0";
    [ObservableProperty] private string scheduledStart = "";
    [ObservableProperty] private bool useSystemRoute;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isReady;
    [ObservableProperty] private string status = Text("ReadyStatus");
    [ObservableProperty] private double percentage;
    [ObservableProperty] private bool isIndeterminate;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string stateFilter = "All";
    [ObservableProperty] private QueueRow? selectedJob;
    private static string Text(string key) => (string)System.Windows.Application.Current.FindResource(key);

    public MainViewModel()
    {
        JobsView = new ListCollectionView(Jobs) { Filter = FilterJob };
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) => await CheckScheduleAsync();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await Task.Run(async () =>
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiInternetDownloadManager");
                store = new QueueStore(Path.Combine(directory, "queue.sqlite"), new WindowsQueueProtector());
                queue = new QueueCoordinator(store, new QueuedDownloadExecutor());
                await queue.InitializeAsync();
            });
            await RefreshJobsAsync();
            IsReady = true;
            timer.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            store?.Dispose(); store = null; queue = null;
            Status = Text("QueueLoadError");
        }
    }

    private bool FilterJob(object item)
    {
        if (item is not QueueRow row) return false;
        return (string.IsNullOrWhiteSpace(Search) || row.Filename.Contains(Search, StringComparison.CurrentCultureIgnoreCase))
            && (StateFilter == "All" || StateFilter == "Active" && row.Job.State is QueueJobState.Queued or QueueJobState.Running or QueueJobState.Paused
                || StateFilter == row.Job.State.ToString());
    }
    partial void OnSearchChanged(string value) => JobsView.Refresh();
    partial void OnStateFilterChanged(string value) => JobsView.Refresh();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var paths = await Task.Run(catalog.Discover);
            var selection = Paths.Where(p => p.Selected).Select(p => (p.Path.AdapterId, p.Path.SourceAddress)).ToHashSet();
            Paths.Clear();
            foreach (var path in paths) Paths.Add(new(path) { Selected = selection.Contains((path.AdapterId, path.SourceAddress)) });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("DiscoveryError"); }
    }

    [RelayCommand]
    private void Browse()
    {
        var picker = new SaveFileDialog { OverwritePrompt = false };
        if (picker.ShowDialog() == true) Destination = picker.FileName;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (!IsReady || queue is null) return;
        try
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var source) || string.IsNullOrWhiteSpace(Destination)
                || !int.TryParse(WorkerCount, out var workers) || !int.TryParse(Priority, out var priorityValue))
                throw new ArgumentException();
            if (File.Exists(Destination)) { Status = Text("DestinationExistsStatus"); return; }
            var job = new QueueJob
            {
                Source = source, Destination = Destination, UseSystemRoute = UseSystemRoute,
                Paths = Paths.Where(p => p.Selected).Select(p => new QueuedPath(p.Path.AdapterId, p.Path.SourceAddress.ToString())).ToArray(),
                Options = new DownloadOptions { Workers = workers, ExpectedSha256 = string.IsNullOrWhiteSpace(ExpectedSha256) ? null : ExpectedSha256.Trim() },
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
            var reporter = new UiProgress((id, value) => { if (IsBusy && generation == runGeneration) UpdateProgress(id, value); });
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
            foreach (var job in snapshot.OrderByDescending(j => j.CreatedAt)) Jobs.Add(new(job));
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

    private void UpdateProgress(Guid id, DownloadProgress value)
    {
        var existing = Jobs.FirstOrDefault(row => row.Job.Id == id);
        if (existing is not null && existing.Job.State is QueueJobState.Queued or QueueJobState.Running)
        {
            var replacement = new QueueRow(existing.Job with { State = QueueJobState.Running, CompletedBytes = value.CompletedBytes, TotalBytes = value.TotalBytes });
            var selected = SelectedJob?.Job.Id == id;
            Jobs[Jobs.IndexOf(existing)] = replacement;
            if (selected) SelectedJob = replacement;
        }
        IsIndeterminate = value.TotalBytes is null;
        Percentage = value.TotalBytes is > 0 ? (double)value.CompletedBytes / value.TotalBytes.Value * 100 : value.Stage == DownloadStage.Completed ? 100 : 0;
        Status = string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("ProgressFormat"), Text($"Stage{value.Stage}"), value.CompletedBytes, value.TotalBytes?.ToString("N0") ?? Text("UnknownTotal"));
    }

    public async Task ShutdownAsync()
    {
        IsReady = false; autoRun = false; timer.Stop(); stop?.Cancel();
        var tasks = new[] { RunQueueCommand.ExecutionTask, AddCommand.ExecutionTask, ResumeSelectedCommand.ExecutionTask,
            CancelSelectedCommand.ExecutionTask, ClearHistoryCommand.ExecutionTask, RefreshCommand.ExecutionTask };
        try { await Task.WhenAll(tasks.Where(t => t is not null).Cast<Task>()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
    }

    public void Dispose()
    { autoRun = false; timer.Stop(); store?.Dispose(); }

    private sealed class UiProgress(Action<Guid, DownloadProgress> update)
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
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => update(id, value));
            }
        }
    }
}
