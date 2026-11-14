using System.Globalization;

namespace DownloadManager.Core;

public enum QueueJobState { Queued, Running, Paused, Completed, Failed, Cancelled }
public sealed record QueuedPath(string AdapterId, string SourceAddress);
public sealed record QueueJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Uri Source { get; init; }
    public required string Destination { get; init; }
    public DownloadOptions Options { get; init; } = new();
    public bool UseSystemRoute { get; init; }
    public IReadOnlyList<QueuedPath> Paths { get; init; } = [];
    public int Priority { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public QueueJobState State { get; init; } = QueueJobState.Queued;
    public long CompletedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public string? FailureCode { get; init; }

    public void Validate()
    {
        if (Id == Guid.Empty || Source is null || !Source.IsAbsoluteUri || Source.Scheme is not ("http" or "https") || Source.UserInfo.Length != 0)
            throw new ArgumentException("Invalid queue identity or source.");
        ArgumentException.ThrowIfNullOrWhiteSpace(Destination);
        if (Source.AbsoluteUri.Length > 32768 || Destination.Length > 32768 || !Enum.IsDefined(State))
            throw new ArgumentException("Invalid queue payload size or state.");
        Options.Validate();
        if (Priority is < -10 or > 10) throw new ArgumentOutOfRangeException(nameof(Priority));
        if (!UseSystemRoute && (Paths.Count == 0 || Paths.Count > Options.Workers))
            throw new ArgumentException("Select allowed paths and allocate at least one worker per path.");
        if (Paths.Count > 128 || Paths.Distinct().Count() != Paths.Count || Paths.Any(p => string.IsNullOrWhiteSpace(p.AdapterId) || p.AdapterId.Length > 1024 || string.IsNullOrWhiteSpace(p.SourceAddress) || p.SourceAddress.Length > 128))
            throw new ArgumentException("Invalid or duplicate queued paths.");
        if (FailureCode is not null and not ("Validation" or "Network" or "Storage" or "Timeout" or "Failure"))
            throw new ArgumentException("Unknown failure category.");
        if (CompletedBytes < 0 || TotalBytes is < 0 || (TotalBytes is { } total && CompletedBytes > total))
            throw new ArgumentException("Invalid queue progress.");
    }

    public QueueJob Transition(QueueJobState next)
    {
        var allowed = State switch
        {
            QueueJobState.Queued => next is QueueJobState.Running or QueueJobState.Paused or QueueJobState.Cancelled,
            QueueJobState.Running => next is QueueJobState.Paused or QueueJobState.Completed or QueueJobState.Failed,
            QueueJobState.Paused or QueueJobState.Failed => next is QueueJobState.Queued or QueueJobState.Cancelled,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("Invalid queue state transition.");
        return this with { State = next, FailureCode = null };
    }
}

public static class QueueScheduling
{
    public static QueueJob? Next(IEnumerable<QueueJob> jobs, DateTimeOffset now) => jobs
        .Where(j => j.State == QueueJobState.Queued && (j.NotBefore is null || j.NotBefore <= now))
        // Aging adds one priority point per five minutes to prevent starvation by newly enqueued jobs.
        .OrderByDescending(j => (long)j.Priority + (long)Math.Max(0, (now - j.CreatedAt).TotalMinutes / 5))
        .ThenBy(j => j.CreatedAt).ThenBy(j => j.Id).FirstOrDefault();

    public static DateTimeOffset? ParseStart(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.EndsWith('Z')) text = text[..^1] + "+00:00";
        string[] formats = ["yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        if (!DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new FormatException("Start time must include an ISO date, time and explicit UTC offset or Z.");
        return value;
    }
}
