using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownloadManager.NetworkBalancer;

public sealed record DetectedAdapter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("ipv4")] string IPv4,
    [property: JsonPropertyName("ipv6")] string IPv6,
    [property: JsonPropertyName("reason")] string Reason)
{
    public string DiscoveryStatus => string.IsNullOrEmpty(Reason) ? "Eligible" : Reason;
}

public static class AdapterDiagnostics
{
    public static async Task<IReadOnlyList<DetectedAdapter>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "balancer", "adapter-diagnostics.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Adapter diagnostics are missing from this build.", executable);

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var process = Process.Start(startInfo) ?? throw new IOException("Could not start adapter discovery.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var diagnosticOutput = await output;
            var errorOutput = await errors;
            if (process.ExitCode != 0)
                throw new IOException(string.IsNullOrWhiteSpace(errorOutput)
                    ? $"Adapter discovery exited with code {process.ExitCode}."
                    : errorOutput.Trim());

            var report = JsonSerializer.Deserialize<DiagnosticReport>(diagnosticOutput);
            if (report is not { Version: 1, Adapters: not null } ||
                report.Adapters.Any(adapter => adapter is null || string.IsNullOrWhiteSpace(adapter.Name)))
                throw new InvalidDataException("The balancer engine returned an unsupported diagnostic report.");

            return report.Adapters;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) when (process.HasExited) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Adapter discovery timed out.");
        }
    }

    private sealed record DiagnosticReport(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("adapters")] DetectedAdapter[]? Adapters);
}
