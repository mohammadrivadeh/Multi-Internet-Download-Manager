using DownloadManager.Core;
namespace DownloadManager.Infrastructure.Persistence;

public static class DestinationDirectory
{
    public static string SuggestFreshCopy(string original, IReadOnlySet<string> reserved)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(original))!;
        var stem = Path.GetFileNameWithoutExtension(original);
        var extension = Path.GetExtension(original);
        for (var number = 1; ; number++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({number}){extension}");
            if (!reserved.Contains(candidate) && IsFresh(candidate)) return candidate;
        }
    }

    public static bool IsFresh(string path) =>
        !Path.Exists(path) && !Path.Exists(path + ".part") && !Path.Exists(path + ".part.sqlite")
        && !Path.Exists(path + ".part.sqlite-wal") && !Path.Exists(path + ".part.sqlite-shm")
        && !Path.Exists(path + ".part.sqlite-journal");

    public static void CreateFor(PlannedDestination plan)
    {
        // Revalidate the plan rather than trusting caller-constructed path strings.
        var root = Path.GetFullPath(plan.Root);
        var category = DestinationRules.ValidateLeaf(Path.GetFileName(plan.CategoryDirectory));
        var expected = Path.GetFullPath(Path.Combine(root, category));
        if (!Path.IsPathFullyQualified(plan.Root) || !string.Equals(expected, Path.GetFullPath(plan.CategoryDirectory), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected, Path.GetDirectoryName(Path.GetFullPath(plan.FullPath)), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid destination plan.");
        _ = DestinationRules.ValidateLeaf(Path.GetFileName(plan.FullPath));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Select an existing base directory.");
        CheckAncestors(expected);
        Directory.CreateDirectory(expected);
        CheckAncestors(expected);
    }
    private static void CheckAncestors(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked or reparse-point destination folders are not supported by automatic rules.");
    }
}
