namespace DownloadManager.Core;

public static class DownloadCategory
{
    public const string Default = "General";
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Trim();
        if (value.Length is < 1 or > 64 || value.Any(char.IsControl))
            throw new ArgumentException("A category needs 1–64 printable characters.", nameof(value));
        return value;
    }
}

public sealed record DesktopPreferences
{
    public List<QueuedPath>? DownloadAdapters { get; init; }
    public List<CategoryDownloadSettings> CategoryDefaults { get; init; } = [];
    public List<DownloadQueueSettings> Queues { get; init; } = [new()];
    public string? DestinationRoot { get; init; }
    public int Workers { get; init; } = 4;
    public long SpeedLimitKiB { get; init; }
    public int Priority { get; init; }
    public string Category { get; init; } = DownloadCategory.Default;
    public int MotionLevel { get; init; } = 2;
    public void Validate()
    {
        if (DownloadAdapters is { } adapters &&
            (adapters.Count > 128 || adapters.Any(path => path is null || string.IsNullOrWhiteSpace(path.AdapterId) ||
                path.AdapterId.Length > 1024 || !System.Net.IPAddress.TryParse(path.SourceAddress, out _)) ||
             adapters.Select(path => path.AdapterId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != adapters.Count))
            throw new ArgumentException("Invalid download adapter selection.");
        if (Workers is < 1 or > 128 || SpeedLimitKiB is < 0 or > 1048576 || Priority is < -10 or > 10
            || MotionLevel is < 0 or > 3)
            throw new ArgumentException("Invalid desktop defaults.");
        _ = DownloadCategory.Normalize(Category);
        if (CategoryDefaults is null || CategoryDefaults.Any(item => item is null) ||
            CategoryDefaults.Select(item => item.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count() != CategoryDefaults.Count)
            throw new ArgumentException("Invalid category settings.");
        foreach (var settings in CategoryDefaults) settings.Validate();
        if (Queues is null || Queues.Count is < 1 or > 32 || !Queues.Any(q => string.Equals(q.Name, QueueJob.MainQueue, StringComparison.OrdinalIgnoreCase))
            || Queues.Select(q => q.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Queues.Count)
            throw new ArgumentException("Invalid queue list.");
        foreach (var queue in Queues) queue.Validate();
        if (DestinationRoot is { } root && (!Path.IsPathFullyQualified(root) || root.Length > 32000)) throw new ArgumentException("Invalid destination root.");
    }

    public CategoryDownloadSettings DefaultsFor(string category) =>
        CategoryDefaults.FirstOrDefault(item => string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
        ?? (string.Equals(Category.Trim(), category, StringComparison.OrdinalIgnoreCase)
            ? new(category, Workers, Priority, DestinationRoot)
            : new(category));
}

public sealed record CategoryDownloadSettings(string Category, int Workers = 4, int Priority = 0, string? DestinationRoot = null)
{
    public void Validate()
    {
        if (DownloadCategory.Normalize(Category) != Category || Workers is < 1 or > 128 || Priority is < -10 or > 10 ||
            DestinationRoot is { } root && (!Path.IsPathFullyQualified(root) || root.Length > 32000))
            throw new ArgumentException("Invalid category download settings.");
    }
}

public sealed record DownloadQueueSettings
{
    public string Name { get; init; } = QueueJob.MainQueue;
    public int MaxConcurrentDownloads { get; init; } = 1;
    public string StartTime { get; init; } = "";
    public string StopTime { get; init; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name != Name.Trim() || Name.Length > 48 || Name.Any(char.IsControl)) throw new ArgumentException("Invalid queue name.");
        if (MaxConcurrentDownloads is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(MaxConcurrentDownloads));
        if (string.IsNullOrWhiteSpace(StartTime) != string.IsNullOrWhiteSpace(StopTime)) throw new ArgumentException("Set both queue schedule times or leave both empty.");
        if (StartTime.Length > 0 && (!TimeOnly.TryParseExact(StartTime, "HH:mm", out var start) || !TimeOnly.TryParseExact(StopTime, "HH:mm", out var stop) || start == stop))
            throw new ArgumentException("Queue schedule times must be different 24-hour HH:mm values.");
    }

    public bool IsWithinSchedule(DateTime localNow)
    {
        if (StartTime.Length == 0) return true;
        var now = TimeOnly.FromDateTime(localNow);
        TimeOnly.TryParseExact(StartTime, "HH:mm", out var start);
        TimeOnly.TryParseExact(StopTime, "HH:mm", out var stop);
        return start < stop ? now >= start && now < stop : now >= start || now < stop;
    }
}
