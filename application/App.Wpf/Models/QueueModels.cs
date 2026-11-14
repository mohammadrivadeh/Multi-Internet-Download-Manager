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

public sealed record QueueRow(QueueJob Job, IDesktopServices Desktop)
{
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
    public string Failure => Job.CheckpointCleanupPending ? Desktop.Text("CheckpointCleanupNotice") : Job.FailureCode is { } code ? Desktop.Text($"QueueFailure{code}") : "";
    public string RateLimit => Job.Options.BytesPerSecondLimit is { } limit ? (limit / 1024d).ToString("N1") : Desktop.Text("UnlimitedLabel");
    public int Priority => Job.Priority;
}
