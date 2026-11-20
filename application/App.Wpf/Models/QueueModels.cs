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
    public string LiveSpeed { get => liveSpeed; internal set => SetProperty(ref liveSpeed, value); }
    private readonly IDesktopServices Desktop;
    public QueueJob Job => job;
    public QueueRow(QueueJob job, IDesktopServices desktop) { this.job = job; Desktop = desktop; }
    public void Update(QueueJob value)
    {
        if (job == value) return;
        job = value;
        if (job.State != QueueJobState.Running) LiveSpeed = "—";
        OnPropertyChanged(string.Empty);
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
    public string ProgressDisplay => Job.TotalBytes is { } total ? $"{FormatBytes(Job.CompletedBytes)} / {FormatBytes(total)}" : FormatBytes(Job.CompletedBytes);
    public double ProgressPercent => Job.TotalBytes is long total && total > 0 ? Math.Clamp((double)Job.CompletedBytes / total * 100d, 0d, 100d) : 0d;
    public string SizeDisplay => Job.TotalBytes is { } total ? FormatBytes(total) : "—";
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
                    .Where(field => field != CheckpointDifferences.None && Job.ResumeDifferences.HasFlag(field))
                    .Select(field => Desktop.Text($"CheckpointField{field}"));
                return string.Format(Desktop.Text("CheckpointMismatchMessage"), string.Join(", ", fields));
            }
            return Job.CheckpointCleanupPending ? Desktop.Text("CheckpointCleanupNotice") :
                Job.FailureCode is { } code ? Desktop.Text($"QueueFailure{code}") : "";
        }
    }
    public string RateLimit => Job.Options.BytesPerSecondLimit is { } limit ? (limit / 1000d).ToString("N1") : Desktop.Text("UnlimitedLabel");
    public int Priority => Job.Priority;
}
