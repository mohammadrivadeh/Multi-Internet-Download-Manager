namespace DownloadManager.Core;

/// <summary>Editable policy only; resource identity, destination and checksum are intentionally absent.</summary>
public sealed record QueueJobSettings
{
    public int Workers { get; init; } = 4;
    public long? BytesPerSecondLimit { get; init; }
    public bool UseSystemRoute { get; init; }
    public IReadOnlyList<QueuedPath> Paths { get; init; } = [];
    public string Category { get; init; } = DownloadCategory.Default;
    public int Priority { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public DailyTransferWindow? DailyWindow { get; init; }

    public QueueJob Apply(QueueJob current)
    {
        if (current.State is not (QueueJobState.Queued or QueueJobState.Paused or QueueJobState.Failed))
            throw new InvalidOperationException("Only unfinished inactive jobs can be edited.");
        var changed = current with
        {
            Options = current.Options with { Workers = Workers, BytesPerSecondLimit = BytesPerSecondLimit },
            UseSystemRoute = UseSystemRoute, Paths = Array.AsReadOnly(Paths.ToArray()),
            Category = DownloadCategory.Normalize(Category), Priority = Priority, NotBefore = NotBefore, DailyWindow = DailyWindow
        };
        changed.Validate();
        return changed;
    }
}
