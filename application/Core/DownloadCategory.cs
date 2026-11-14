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
    public int Workers { get; init; } = 4;
    public long SpeedLimitKiB { get; init; }
    public int Priority { get; init; }
    public string Category { get; init; } = DownloadCategory.Default;
    public void Validate()
    {
        if (Workers is < 1 or > 128 || SpeedLimitKiB is < 0 or > 1048576 || Priority is < -10 or > 10)
            throw new ArgumentException("Invalid desktop defaults.");
        _ = DownloadCategory.Normalize(Category);
    }
}
