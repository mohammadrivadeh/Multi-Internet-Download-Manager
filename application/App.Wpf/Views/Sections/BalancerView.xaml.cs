using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DownloadManager.NetworkBalancer;
using DownloadManager.Infrastructure.Networking;

namespace DownloadManager.App.Views.Sections;

public partial class BalancerView : UserControl
{
    private bool scanned;
    private CaptureSession? capture = null;
    private AdapterRow[]? adapterRows;
    private readonly NetworkPathCatalog catalog = new();
    private CancellationTokenSource? scan;

    public BalancerView()
    {
        InitializeComponent();
#if CAPTURE_PREVIEW
        StartCaptureButton.Visibility = Visibility.Visible;
        StopCaptureButton.Visibility = Visibility.Visible;
        CaptureStatus.Text = "Ready to start. Windows will request administrator approval.";
#endif
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ScanOnceAsync();

    private async void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
            await ScanOnceAsync();
    }

    private async Task ScanOnceAsync()
    {
        if (IsVisible && !scanned)
        {
            scanned = true;
            await ScanAsync();
        }
    }

    private async void ScanClick(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (scan is not null || capture is not null) return;
        using var cancellation = new CancellationTokenSource();
        scan = cancellation;
        ScanButton.IsEnabled = false;
        StartCaptureButton.IsEnabled = false;
        AdapterTable.IsEnabled = false;
        ScanStatus.Text = "Scanning and testing adapters…";
        try
        {
            var adapters = await AdapterDiagnostics.ScanAsync(cancellation.Token);
            var paths = await Task.Run(catalog.Discover, cancellation.Token);
            var previous = adapterRows?.Where(row => row.Online)
                .ToDictionary(row => (row.Adapter.Index, row.Adapter.Name), row => row.Selected);
            var rows = new List<AdapterRow>();
            foreach (var adapter in adapters)
            {
                AdapterSample? sample = null;
                string? family = null;
                if (string.IsNullOrEmpty(adapter.Reason))
                {
                    ScanStatus.Text = $"Testing {adapter.Name}…";
                    foreach (var path in TestPaths(adapter, paths))
                    {
                        var result = await Task.Run(() => AdapterSample.MeasureAsync(path, catalog, cancellation.Token), cancellation.Token);
                        if (result is null || (sample is not null && result.MegabitsPerSecond is null)) continue;
                        sample = result;
                        family = path.SourceAddress.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                        if (sample.MegabitsPerSecond.HasValue) break;
                    }
                }
                var selected = sample is not null && previous?.GetValueOrDefault((adapter.Index, adapter.Name), true) != false;
                rows.Add(new AdapterRow(adapter, sample, family, selected));
            }
            adapterRows = rows.ToArray();
            AdapterTable.ItemsSource = rows.Where(row => row.Online).ToArray();
            OtherAdapterTable.ItemsSource = rows.Where(row => !row.Online).ToArray();
            ScanStatus.Text = $"{rows.Count(row => row.Online)} online · {rows.Count(row => !row.Online)} other";
            UpdateSelectionSummary();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception
                                      or JsonException or TimeoutException or NetworkInformationException)
        {
            ScanStatus.Text = error.Message;
        }
        finally
        {
            scan = null;
            ScanButton.IsEnabled = true;
            AdapterTable.IsEnabled = true;
            StartCaptureButton.IsEnabled = true;
        }
    }

    private static IEnumerable<NetworkPath> TestPaths(DetectedAdapter adapter, IReadOnlyList<NetworkPath> paths)
    {
        foreach (var address in new[] { adapter.IPv4, adapter.IPv6 })
        {
            if (!IPAddress.TryParse(address, out var advertised)) continue;
            var match = paths.FirstOrDefault(path => path.Name == adapter.Name && path.SourceAddress.Equals(advertised));
            if (match is not null) yield return match;
        }
    }

    private void AdapterSelectionChanged(object sender, RoutedEventArgs e) => UpdateSelectionSummary();

    private void UpdateSelectionSummary()
    {
        var selected = adapterRows?.Where(row => row.Online && row.Selected).ToArray() ?? [];
        if (selected.Length == 0)
        {
            SelectionSummary.Text = "Select online adapters to estimate combined download speed.";
            return;
        }
        var measured = selected.Where(row => row.Sample?.MegabitsPerSecond is not null).ToArray();
        var speed = measured.Length == 0 ? "estimate unavailable"
            : $"{measured.Sum(row => row.Sample!.MegabitsPerSecond!.Value):N1} Mbps estimated across concurrent connections";
        var coverage = measured.Length > 0 && measured.Length != selected.Length ? $" ({measured.Length} sampled)" : "";
        SelectionSummary.Text = $"{selected.Length} selected → {speed}{coverage}";
    }

    private async void StartCaptureClick(object sender, RoutedEventArgs e)
    {
#if CAPTURE_PREVIEW
        if (capture is not null || scan is not null)
            return;
        var selected = adapterRows?.Where(row => row.Online && row.Selected)
            .Select(row => new CaptureAdapterChoice(row.Adapter.Index, row.Adapter.Name)).ToArray();
        if (selected is not { Length: > 0 })
        {
            CaptureStatus.Text = "Scan and select at least one eligible adapter.";
            return;
        }
        StartCaptureButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        AdapterTable.IsEnabled = false;
        CaptureStatus.Text = "Waiting for Windows approval and capture startup…";
        var session = new CaptureSession(Path.Combine(AppContext.BaseDirectory, "balancer", "desktop-capture.exe"), selected);
        capture = session;
        try
        {
            await Task.WhenAny(session.Ready, session.Completion);
            if (session.Ready.IsCompletedSuccessfully && !session.Completion.IsCompleted)
            {
                StopCaptureButton.IsEnabled = true;
                CaptureStatus.Text = "Capture host is running.";
            }
            else
            {
                _ = session.Ready.Exception;
            }
            await session.Completion;
            CaptureStatus.Text = "Capture host stopped.";
        }
        catch (Exception error) when (error is IOException or Win32Exception or TimeoutException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            CaptureStatus.Text = error is Win32Exception { NativeErrorCode: 1223 }
                ? "Windows approval was canceled."
                : error.Message;
        }
        finally
        {
            capture = null;
            StartCaptureButton.IsEnabled = true;
            ScanButton.IsEnabled = true;
            AdapterTable.IsEnabled = true;
            StopCaptureButton.IsEnabled = false;
        }
#else
        await Task.CompletedTask;
#endif
    }

    private async void StopCaptureClick(object sender, RoutedEventArgs e)
    {
        StopCaptureButton.IsEnabled = false;
        CaptureStatus.Text = "Stopping and checking that the capture adapter is removed…";
        await StopCaptureAsync();
    }

    public async Task StopCaptureAsync()
    {
        if (capture is not { } session)
            return;
        session.RequestStop();
        try { await session.Completion.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) when (!session.Completion.IsCompleted)
        {
            CaptureStatus.Text = "The capture host is still stopping. Route cleanup is unconfirmed.";
        }
        catch (Exception) when (session.Completion.IsFaulted)
        {
            // The start handler observes completion and displays the host error.
        }
    }

    public void CancelNetworkTests() => scan?.Cancel();

    private sealed class AdapterRow(DetectedAdapter adapter, AdapterSample? sample, string? family, bool selected)
    {
        public DetectedAdapter Adapter { get; } = adapter;
        public AdapterSample? Sample { get; } = sample;
        public bool Online => Sample is not null;
        public bool Selected { get; set; } = selected;
        public string Latency => Sample is { } result ? $"{result.Latency.TotalMilliseconds:N0} ms · {family}" : "—";
        public string Download => Sample?.MegabitsPerSecond is { } speed ? $"{speed:N1} Mbps" : "Unavailable";
        public string Status => string.IsNullOrEmpty(Adapter.Reason) ? "Cloudflare HTTPS test failed" : Adapter.Reason;
    }
}
