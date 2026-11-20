using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
            return await Task.Run(ScanManaged, cancellationToken);

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

    private static DetectedAdapter[] ScanManaged() => NetworkInterface.GetAllNetworkInterfaces().Select(adapter =>
    {
        var properties = adapter.GetIPProperties();
        var addresses = properties.UnicastAddresses.Select(entry => entry.Address).Where(address =>
            !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal
            && !(address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, _, _])).ToArray();
        var ipv4 = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
        var ipv6 = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetworkV6);
        var index = properties.GetIPv4Properties()?.Index ?? properties.GetIPv6Properties()?.Index ?? 0;
        var reason = adapter.OperationalStatus != OperationalStatus.Up ? "Disconnected"
            : adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ? "Loopback or tunnel"
            : ipv4 is null && ipv6 is null ? "No usable IP address" : "";
        return new DetectedAdapter(adapter.Name, index, ipv4?.ToString() ?? "", ipv6?.ToString() ?? "", reason);
    }).ToArray();

    private sealed record DiagnosticReport(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("adapters")] DetectedAdapter[]? Adapters);
}
