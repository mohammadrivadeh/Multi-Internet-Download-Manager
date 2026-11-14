using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Downloads;
using DownloadManager.Infrastructure.Http;
using DownloadManager.Infrastructure.Networking;
using Microsoft.Win32;

namespace DownloadManager.App;

public partial class PathSelection(NetworkPath path) : ObservableObject
{
    public NetworkPath Path { get; } = path;
    public string Label => $"{Path.Name} · {Path.SourceAddress}";
    [ObservableProperty] private bool selected;
}

public partial class MainViewModel : ObservableObject
{
    private CancellationTokenSource? stop;
    private readonly NetworkPathCatalog catalog = new();
    public ObservableCollection<PathSelection> Paths { get; } = [];
    [ObservableProperty] private string url = "";
    [ObservableProperty] private string destination = "";
    [ObservableProperty] private string workerCount = "4";
    [ObservableProperty] private string expectedSha256 = "";
    [ObservableProperty] private bool useSystemRoute;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool isBusy;
    [ObservableProperty] private string status = Text("ReadyStatus");
    [ObservableProperty] private double percentage;
    [ObservableProperty] private bool isIndeterminate;
    public bool IsIdle => !IsBusy;
    private static string Text(string key) => (string)System.Windows.Application.Current.FindResource(key);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var paths = await Task.Run(catalog.Discover);
            var selection = Paths.Where(p => p.Selected).Select(p => (p.Path.AdapterId, p.Path.SourceAddress)).ToHashSet();
            Paths.Clear();
            foreach (var path in paths) Paths.Add(new(path) { Selected = selection.Contains((path.AdapterId, path.SourceAddress)) });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("DiscoveryError"); }
    }

    [RelayCommand]
    private void Browse()
    {
        var picker = new SaveFileDialog { OverwritePrompt = false };
        if (picker.ShowDialog() == true) Destination = picker.FileName;
    }

    [RelayCommand]
    private void Pause() => stop?.Cancel();

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https") || source.UserInfo.Length != 0
            || string.IsNullOrWhiteSpace(Destination) || !int.TryParse(WorkerCount, out var workers) || workers is < 1 or > 128)
        { Status = Text("InputError"); return; }
        if (File.Exists(Destination)) { Status = Text("DestinationExistsStatus"); return; }
        var selected = Paths.Where(p => p.Selected).Select(p => p.Path).ToArray();
        if (!UseSystemRoute && selected.Length == 0) { Status = Text("SelectionError"); return; }
        IsBusy = true;
        Percentage = 0;
        IsIndeterminate = true;
        Status = Text("PreparingStatus");
        stop = new();
        var sessions = new List<NetworkPathSession>();
        HttpClient? system = null;
        try
        {
            IReadOnlyList<HttpClient> clients;
            if (UseSystemRoute) { system = SystemRouteTransport.Create(); clients = [system]; }
            else
            {
                await Task.Run(() => { foreach (var path in selected) sessions.Add(new(path, catalog)); });
                clients = sessions.Select(s => s.Client).ToArray();
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessions.Select(s => s.Invalidated).Append(stop.Token).ToArray());
            var options = new DownloadOptions { Workers = workers, ExpectedSha256 = string.IsNullOrWhiteSpace(ExpectedSha256) ? null : ExpectedSha256.Trim() };
            var target = Destination;
            var reporter = new UiProgress(UpdateProgress);
            await Task.Run(() => new DownloadEngine().DownloadAsync(source, target, clients, options, reporter, linked.Token));
        }
        catch (OperationCanceledException) { Status = Text("PausedStatus"); }
        catch (HttpStatusFailure ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        { Status = Text("AccessStatus"); }
        catch (HttpStatusFailure ex) when (ex.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable)
        { Status = Text("ServerPressureStatus"); }
        catch (InvalidDataException) { Status = Text("ValidationStatus"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("FailureStatus"); }
        finally
        {
            foreach (var session in sessions) session.Dispose();
            system?.Dispose();
            stop.Dispose(); stop = null;
            IsBusy = false;
            IsIndeterminate = false;
        }
    }

    private void UpdateProgress(DownloadProgress value)
    {
        IsIndeterminate = value.TotalBytes is null;
        Percentage = value.TotalBytes is > 0 ? (double)value.CompletedBytes / value.TotalBytes.Value * 100 : value.Stage == DownloadStage.Completed ? 100 : 0;
        Status = string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("ProgressFormat"), Text($"Stage{value.Stage}"), value.CompletedBytes, value.TotalBytes?.ToString("N0") ?? Text("UnknownTotal"));
    }

    private sealed class UiProgress(Action<DownloadProgress> update) : IProgress<DownloadProgress>
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly object sync = new();
        private DownloadStage? previous;
        public void Report(DownloadProgress value)
        {
            lock (sync)
            {
                if (previous == value.Stage && watch.ElapsedMilliseconds < 100) return;
                previous = value.Stage; watch.Restart();
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => update(value));
            }
        }
    }
}
