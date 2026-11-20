namespace DownloadManager.Core;

public sealed record PlannedDestination(string Root, string CategoryDirectory, string FullPath);

public static class DestinationRules
{
    public static string ValidateLeaf(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ')
            || value.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)))
            throw new ArgumentException("Invalid Windows destination name.");
        var stem = value.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]))
            throw new ArgumentException("Reserved Windows destination name.");
        return value;
    }

    public static PlannedDestination Plan(string root, string category, Uri source, string? filename = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Path.IsPathFullyQualified(root) || !source.IsAbsoluteUri || source.Scheme is not ("http" or "https") || source.UserInfo.Length != 0)
            throw new ArgumentException("An absolute base directory and HTTP(S) source are required.");
        root = Path.GetFullPath(root);
        var folder = ValidateLeaf(DownloadCategory.Normalize(category));
        var last = source.AbsolutePath.Split('/').Last();
        filename = ValidateLeaf(filename ?? (last.Length == 0 ? "download.bin" : Uri.UnescapeDataString(last)));
        var directory = Path.GetFullPath(Path.Combine(root, folder));
        var target = Path.GetFullPath(Path.Combine(directory, filename));
        return new(root, directory, target);
    }
}
