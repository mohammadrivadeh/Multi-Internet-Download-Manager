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
using DownloadManager.Infrastructure.Diagnostics;
using DownloadManager.App.ViewModels;
using DownloadManager.Core;

namespace DownloadManager.App.Views.Sections;

public partial class BalancerView : UserControl
{
    private bool scanned;
    private CaptureSession? capture = null;
    private AdapterRow[]? adapterRows;
    private readonly NetworkPathCatalog catalog = new();
    private CancellationTokenSource? scan;
    private bool savingSelection;
    private readonly System.Windows.Threading.DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public BalancerView()
    {
        InitializeComponent();
        refreshTimer.Tick += async (_, _) =>
        {
            refreshTimer.Stop();
            if (!IsLoaded || !IsVisible) return;
            if (scan is not null || savingSelection) { refreshTimer.Start(); return; }
            await ScanAsync();
        };
        Unloaded += (_, _) =>
        {
            NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
            refreshTimer.Stop();
            scan?.Cancel();
            scanned = false;
        };
#if CAPTURE_PREVIEW
        StartCaptureButton.Visibility = Visibility.Visible;
        StopCaptureButton.Visibility = Visibility.Visible;
        CaptureStatus.Text = "Ready to start. Windows will request administrator approval.";
#endif
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        await ScanOnceAsync();
    }

    private void NetworkAddressChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!IsLoaded) return;
        scanned = false;
        refreshTimer.Stop();
        refreshTimer.Start();
    });

    private async void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
            await ScanOnceAsync();
    }

    internal async Task ScanOnceAsync()
    {
        if (IsVisible && !scanned && DataContext is MainViewModel { IsReady: true })
            await ScanAsync();
    }

    private async void ScanClick(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (scan is not null || savingSelection || DataContext is not MainViewModel { IsReady: true } model) return;
        scanned = true;
        using var cancellation = new CancellationTokenSource();
        scan = cancellation;
        ScanButton.IsEnabled = false;
        StartCaptureButton.IsEnabled = false;
        SetSelectionEnabled(false);
        ScanStatus.Text = "Scanning and testing adapters…";
        try
        {
            var adapters = await AdapterDiagnostics.ScanAsync(cancellation.Token);
            LocalDiagnostics.Trace("adapter-scan-result", $"detected={adapters.Count} eligible={adapters.Count(item => string.IsNullOrEmpty(item.Reason))}");
            var paths = await Task.Run(catalog.Discover, cancellation.Token);
            var previous = model.DownloadAdapters;
            var rows = new List<AdapterRow>();
            foreach (var adapter in adapters)
            {
                AdapterSample? sample = null;
                string? family = null;
                NetworkPath? downloadPath = null;
                if (string.IsNullOrEmpty(adapter.Reason))
                {
                    ScanStatus.Text = $"Testing {adapter.Name}…";
                    foreach (var path in TestPaths(adapter, paths))
                    {
                        downloadPath ??= path;
                        var result = await Task.Run(() => AdapterSample.MeasureAsync(path, catalog, cancellation.Token), cancellation.Token);
                        if (result is null || (sample is not null && result.MegabitsPerSecond is null)) continue;
                        sample = result;
                        downloadPath = path;
                        family = path.SourceAddress.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                        if (sample.MegabitsPerSecond.HasValue) break;
                    }
                }
                var selected = downloadPath is not null &&
                    (previous?.Any(saved => string.Equals(saved.AdapterId, downloadPath.AdapterId, StringComparison.OrdinalIgnoreCase)) ?? sample is not null);
                rows.Add(new AdapterRow(adapter, sample, family, downloadPath, selected));
            }
            adapterRows = rows.ToArray();
            AdapterTable.ItemsSource = rows.Where(row => row.Online).ToArray();
            OtherAdapterTable.ItemsSource = rows.Where(row => !row.Online).ToArray();
            ScanStatus.Text = $"{rows.Count(row => row.Online)} verified · {rows.Count(row => row.Eligible && !row.Online)} unverified";
            UpdateSelectionSummary();
            if (previous is not null || rows.Any(row => row.Selected)) await PersistSelectionAsync(model);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception
                                      or JsonException or TimeoutException or NetworkInformationException)
        {
            DownloadManager.Infrastructure.Diagnostics.LocalDiagnostics.Record("adapter-scan", error);
            ScanStatus.Text = error.Message;
        }
        finally
        {
            scan = null;
            ScanButton.IsEnabled = true;
            SetSelectionEnabled(capture is null);
            StartCaptureButton.IsEnabled = capture is null;
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

    private async void AdapterSelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateSelectionSummary();
        if (DataContext is MainViewModel model)
            await PersistSelectionAsync(model);
    }

    private async Task PersistSelectionAsync(MainViewModel model)
    {
        if (savingSelection || adapterRows is null) return;
        var savedPaths = model.DownloadAdapters ?? [];
        var visible = adapterRows.Where(row => row.Path is not null).Select(row => row.Path!.AdapterId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selection = savedPaths.Where(saved => !visible.Contains(saved.AdapterId))
            .Concat(adapterRows.Where(row => row.Selected && row.Path is not null)
                .Select(row => new QueuedPath(row.Path!.AdapterId, row.Path.SourceAddress.ToString(),
                    savedPaths.FirstOrDefault(saved => string.Equals(saved.AdapterId, row.Path.AdapterId, StringComparison.OrdinalIgnoreCase))?.BytesPerSecondLimit))).ToList();
        if (model.DownloadAdapters?.SequenceEqual(selection) == true) return;
        savingSelection = true;
        SetSelectionEnabled(false);
        try
        {
            if (await model.SaveDownloadAdaptersAsync(selection) is { } error)
            {
                ScanStatus.Text = error;
                scanned = false;
            }
        }
        finally
        {
            savingSelection = false;
            SetSelectionEnabled(capture is null && scan is null);
        }
    }

    private void SetSelectionEnabled(bool enabled)
    {
        AdapterTable.IsEnabled = enabled;
        OtherAdapterTable.IsEnabled = enabled;
    }

    private void UpdateSelectionSummary()
    {
        var selected = adapterRows?.Where(row => row.Eligible && row.Selected).ToArray() ?? [];
        if (selected.Length == 0)
        {
            SelectionSummary.Text = "Select adapters to use for downloads.";
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
        if (capture is not null || scan is not null || savingSelection)
            return;
        var selected = adapterRows?.Where(row => row.Eligible && row.Selected)
            .Select(row => new CaptureAdapterChoice(row.Adapter.Index, row.Adapter.Name)).ToArray();
        if (selected is not { Length: > 0 })
        {
            CaptureStatus.Text = "Scan and select at least one eligible adapter.";
            return;
        }
        StartCaptureButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        SetSelectionEnabled(false);
        CaptureStatus.Text = "Waiting for Windows approval and capture startup…";
        LocalDiagnostics.Trace("capture-start", $"interfaces={string.Join(',', selected.Select(item => item.Index))}");
        var session = new CaptureSession(Path.Combine(AppContext.BaseDirectory, "balancer", "desktop-capture.exe"), selected,
            stats => LocalDiagnostics.Trace("capture-interface", stats));
        capture = session;
        try
        {
            await Task.WhenAny(session.Ready, session.Completion);
            if (session.Ready.IsCompletedSuccessfully && !session.Completion.IsCompleted)
            {
                StopCaptureButton.IsEnabled = true;
                ScanButton.IsEnabled = true;
                CaptureStatus.Text = "Capture host is running.";
                LocalDiagnostics.Trace("capture-ready");
            }
            else
            {
                _ = session.Ready.Exception;
            }
            await session.Completion;
            CaptureStatus.Text = "Capture host stopped.";
            LocalDiagnostics.Trace("capture-stopped");
        }
        catch (Exception error) when (error is IOException or Win32Exception or TimeoutException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            DownloadManager.Infrastructure.Diagnostics.LocalDiagnostics.Record("capture-session", error);
            CaptureStatus.Text = error is Win32Exception { NativeErrorCode: 1223 }
                ? "Windows approval was canceled."
                : error.Message;
        }
        finally
        {
            capture = null;
            StartCaptureButton.IsEnabled = true;
            ScanButton.IsEnabled = true;
            SetSelectionEnabled(scan is null && !savingSelection);
            StopCaptureButton.IsEnabled = false;
            if (IsLoaded && IsVisible)
            {
                refreshTimer.Stop();
                refreshTimer.Start();
            }
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

    private sealed class AdapterRow(DetectedAdapter adapter, AdapterSample? sample, string? family, NetworkPath? path, bool selected)
    {
        public DetectedAdapter Adapter { get; } = adapter;
        public AdapterSample? Sample { get; } = sample;
        public NetworkPath? Path { get; } = path;
        public bool CanSelect => Path is not null;
        public bool Online => Sample is not null;
        public bool Eligible => string.IsNullOrEmpty(Adapter.Reason);
        public bool Selected { get; set; } = selected;
        public string Latency => Sample is { } result ? $"{result.Latency.TotalMilliseconds:N0} ms · {family}" : "—";
        public string Download => Sample?.MegabitsPerSecond is { } speed ? $"{speed:N1} Mbps" : Online ? "Unavailable" : "HTTPS unverified";
        public string Status => string.IsNullOrEmpty(Adapter.Reason) ? "Cloudflare HTTPS test failed" : Adapter.Reason;
    }
}
