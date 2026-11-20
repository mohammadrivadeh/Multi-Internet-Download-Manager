using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Networking;
using DownloadManager.App.Services;
namespace DownloadManager.App.Models;

public partial class PathSelection(NetworkPath path) : ObservableObject
{
    public NetworkPath Path { get; } = path;
    public string Label => $"{Path.Name} · {Path.SourceAddress}";
    [ObservableProperty] private bool selected;
    [ObservableProperty] private string speedLimitKiB = "0";
    public QueuedPath Capture()
    {
        if (!long.TryParse(SpeedLimitKiB, out var cap) || cap is < 0 or > 1048576) throw new ArgumentException("Invalid path cap.");
        return new(Path.AdapterId, Path.SourceAddress.ToString(), cap == 0 ? null : cap * 1024);
    }
}

public sealed record CategoryOption(string Key, string Name);

public sealed class QueueRow : ObservableObject
{
    private QueueJob job;
    private string liveSpeed = "—";
    private readonly TransferRateEstimator estimator = new();
    private long? liveAcceptedBytes;
    private IReadOnlyList<DownloadPathProgress> networkPaths = [];
    public string LiveSpeed { get => liveSpeed; private set => SetProperty(ref liveSpeed, value); }
    private readonly IDesktopServices Desktop;
    public QueueJob Job => job;
    public QueueRow(QueueJob job, IDesktopServices desktop) { this.job = job; Desktop = desktop; }
    public void Update(QueueJob value)
    {
        if (job == value) return;
        job = value;
        if (job.State != QueueJobState.Running)
        {
            estimator.Reset();
            liveAcceptedBytes = null;
            LiveSpeed = "—";
        }
        OnPropertyChanged(string.Empty);
    }
    public void UpdateFromSnapshot(QueueJob value)
    {
        // The coordinator keeps durable byte counts in its snapshot until a worker stops.
        // Preserve newer progress supplied by that worker while the job is still running.
        if (job.State == QueueJobState.Running && value.State == QueueJobState.Running)
            value = value with { CompletedBytes = job.CompletedBytes, TotalBytes = job.TotalBytes };
        Update(value);
    }
    public string Destination => Job.Destination;
    public string Origin => Job.Source.GetLeftPart(UriPartial.Authority);
    public string Checksum => Job.Options.ExpectedSha256 ?? "—";
    public int Workers => Job.Options.Workers;
    public string Category => Job.Category;
    public string Filename => Path.GetFileName(Job.Destination);
    public string State => Desktop.Text($"QueueState{Job.State}");
    public string DailyWindow => Job.DailyWindow is { } w ? $"{w.StartMinute / 60:00}:{w.StartMinute % 60:00}–{w.EndMinute / 60:00}:{w.EndMinute % 60:00} ({TimeSpan.FromMinutes(w.OffsetMinutes)})" : "—";
    public string Schedule => Job.NotBefore?.ToString("yyyy-MM-dd HH:mm zzz") ?? "";
    public string Progress => Job.TotalBytes is { } total ? $"{Job.CompletedBytes:N0} / {total:N0}" : $"{Job.CompletedBytes:N0}";
    private long DisplayBytes => liveAcceptedBytes ?? Job.CompletedBytes;
    public string ProgressDisplay => Job.TotalBytes is { } total ? $"{FormatBytes(DisplayBytes)} / {FormatBytes(total)}" : FormatBytes(DisplayBytes);
    public double ProgressPercent => Job.TotalBytes is long total && total > 0 ? Math.Clamp((double)DisplayBytes / total * 100d, 0d, 100d) : 0d;
    public string SizeDisplay => Job.TotalBytes is { } total ? FormatBytes(total) : "—";
    public string NetworkPathLabel => Desktop.Text("DownloadPathSection");
    public string NetworkPathSummary => networkPaths.Count == 0 ? Desktop.Text("DownloadPathNoSample")
        : string.Join(Environment.NewLine, networkPaths.Select(path => string.Format(
            Desktop.Text("DownloadPathSampleFormat"), path.Ordinal,
            path.InterfaceIndex is { } index ? string.Format(Desktop.Text("DownloadPathInterfaceFormat"), index) : Desktop.Text("DownloadPathSystemRoute"),
            Desktop.Text($"DownloadPathState{path.State}"), FormatBytes(path.ReceivedBytes), FormatBytes(path.CheckpointedBytes))));

    internal void UpdateNetworkPaths(IReadOnlyList<DownloadPathProgress> paths)
    {
        if (networkPaths.SequenceEqual(paths)) return;
        networkPaths = paths;
        OnPropertyChanged(nameof(NetworkPathSummary));
    }

    internal TimeSpan? ApplyLiveProgress(DownloadProgress progress, TimeSpan elapsed)
    {
        var previous = liveAcceptedBytes;
        var updated = progress.Stage == DownloadStage.Probing
            ? Job with { State = QueueJobState.Running }
            : Job with { State = QueueJobState.Running, CompletedBytes = progress.CompletedBytes, TotalBytes = progress.TotalBytes };
        liveAcceptedBytes = progress.Stage == DownloadStage.Probing ? null : progress.AcceptedBytes;
        if (updated != Job) Update(updated);
        else if (previous != liveAcceptedBytes)
        {
            OnPropertyChanged(nameof(ProgressDisplay));
            OnPropertyChanged(nameof(ProgressPercent));
        }

        if (progress.Stage != DownloadStage.Downloading)
        {
            estimator.Reset();
            LiveSpeed = "—";
            return null;
        }
        var (rate, remaining) = estimator.Sample(progress.AcceptedBytes, progress.TotalBytes, elapsed);
        LiveSpeed = rate > 0 ? $"{rate / 1000:N1} KB/s" : "—";
        return remaining;
    }

    internal void ClearStaleSpeed(TimeSpan elapsed)
    {
        if (estimator.ResetIfStale(elapsed)) LiveSpeed = "—";
    }
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double amount = Math.Max(0, bytes);
        var unit = 0;
        while (amount >= 1000 && unit < units.Length - 1) { amount /= 1000; unit++; }
        return $"{amount:0.##} {units[unit]}";
    }
    public string Failure
    {
        get
        {
            if (Job.ResumeDifferences != CheckpointDifferences.None)
            {
                var fields = Enum.GetValues<CheckpointDifferences>()
                    .Where(difference => difference != CheckpointDifferences.None && Job.ResumeDifferences.HasFlag(difference))
                    .Select(difference => Desktop.Text($"CheckpointField{difference}"));
                return string.Format(Desktop.Text("CheckpointMismatchMessage"), string.Join(", ", fields));
            }
            return Job.CheckpointCleanupPending ? Desktop.Text("CheckpointCleanupNotice") :
                Job.FailureCode is { } code ? Desktop.Text($"QueueFailure{code}") : "";
        }
    }
    public string RateLimit => Job.Options.BytesPerSecondLimit is { } limit ? (limit / 1000d).ToString("N1") : Desktop.Text("UnlimitedLabel");
    public int Priority => Job.Priority;
}
