using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DownloadManager.NetworkBalancer;

namespace DownloadManager.App.Views.Sections;

public partial class BalancerView : UserControl
{
    private bool scanned;
    private CaptureSession? capture = null;
    private AdapterRow[]? adapterRows;

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
        ScanButton.IsEnabled = false;
        ScanStatus.Text = "Scanning adapters…";
        try
        {
            var adapters = await AdapterDiagnostics.ScanAsync();
            var previous = adapterRows?.Where(row => row.Selected).Select(row => (row.Adapter.Index, row.Adapter.Name)).ToHashSet();
            adapterRows = adapters.Select(adapter => new AdapterRow(adapter,
                string.IsNullOrEmpty(adapter.Reason) && (previous is null || previous.Contains((adapter.Index, adapter.Name))))).ToArray();
            AdapterTable.ItemsSource = adapterRows;
            var eligible = adapterRows.Count(row => row.Eligible);
            ScanStatus.Text = $"Eligible adapters detected: {eligible}.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Win32Exception or JsonException or TimeoutException)
        {
            ScanStatus.Text = error.Message;
        }
        finally { ScanButton.IsEnabled = true; }
    }

    private async void StartCaptureClick(object sender, RoutedEventArgs e)
    {
#if CAPTURE_PREVIEW
        if (capture is not null)
            return;
        var selected = adapterRows?.Where(row => row.Eligible && row.Selected)
            .Select(row => new CaptureAdapterChoice(row.Adapter.Index, row.Adapter.Name)).ToArray();
        if (selected is not { Length: > 0 })
        {
            CaptureStatus.Text = "Scan and select at least one eligible adapter.";
            return;
        }
        StartCaptureButton.IsEnabled = false;
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

    private sealed class AdapterRow(DetectedAdapter adapter, bool selected)
    {
        public DetectedAdapter Adapter { get; } = adapter;
        public bool Eligible => string.IsNullOrEmpty(Adapter.Reason);
        public bool Selected { get; set; } = selected;
    }
}
