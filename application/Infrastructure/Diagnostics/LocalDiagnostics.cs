using System.Globalization;
using System.Diagnostics;
using System.Net.Sockets;

namespace DownloadManager.Infrastructure.Diagnostics;

public static class LocalDiagnostics
{
    private static readonly object Gate = new();
    public static string FilePath { get; } = Path.Combine(
        AppContext.BaseDirectory, "Logs", "diagnostics.log");

    public static void Trace(string operation, string? detail = null) =>
        Write($"{operation} {detail}");

    public static void Record(string operation, Exception error, string? detail = null)
    {
        var causes = new List<string>();
        for (Exception? cause = error; cause is not null && causes.Count < 5; cause = cause.InnerException)
            causes.Add(cause is SocketException socket ? $"{cause.GetType().Name}/{socket.SocketErrorCode}" :
                $"{cause.GetType().Name}/0x{cause.HResult:X8}");
        var frame = new StackTrace(error, true).GetFrames().FirstOrDefault(candidate =>
            candidate.GetMethod()?.DeclaringType?.FullName?.StartsWith("DownloadManager.", StringComparison.Ordinal) == true);
        var origin = frame is null ? "" : $" source={frame.GetMethod()?.DeclaringType?.Name}:{frame.GetFileLineNumber()}";
        Write($"{operation} {detail} {string.Join(" > ", causes)}{origin}");
    }

    private static void Write(string message)
    {
        var line = $"{DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)} {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= 512 * 1024)
                    File.Move(FilePath, FilePath + ".previous", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
}
