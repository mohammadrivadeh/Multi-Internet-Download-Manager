namespace DownloadManager.Core;

public enum DownloadStage { Probing, Downloading, Verifying, Completed }
public sealed record DownloadProgress(DownloadStage Stage, long CompletedBytes, long? TotalBytes, bool Resumed)
{
    public long AcceptedBytes { get; init; } = CompletedBytes;
}

public sealed record DownloadOptions
{
    public int Workers { get; init; } = 4;
    public int MinimumRangeBytes { get; init; } = 256 * 1024;
    public int MaximumRangeBytes { get; init; } = 8 * 1024 * 1024;
    public int RetryLimit { get; init; } = 2;
    public long? BytesPerSecondLimit { get; init; }
    public string? ExpectedSha256 { get; init; }

    public void Validate()
    {
        if (Workers is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(Workers));
        if (MinimumRangeBytes < 4096 || MaximumRangeBytes < MinimumRangeBytes || MaximumRangeBytes > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MinimumRangeBytes));
        if (BytesPerSecondLimit is < 1 or > 1024L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(BytesPerSecondLimit));
        if (RetryLimit is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(RetryLimit));
        if (ExpectedSha256 is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new ArgumentException("Expected SHA-256 must contain 64 hexadecimal characters.");
    }
}
