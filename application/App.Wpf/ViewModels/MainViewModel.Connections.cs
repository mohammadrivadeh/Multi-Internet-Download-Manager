using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using DownloadManager.App.Services;
using DownloadManager.App.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Networking;
using DownloadManager.Infrastructure.Persistence;
using DownloadManager.Infrastructure.Queueing;


namespace DownloadManager.App.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var paths = await Task.Run(catalog.Discover);
            var selection = Paths.Where(p => p.Selected).Select(p => (p.Path.AdapterId, p.Path.SourceAddress)).ToHashSet();
            var limits = Paths.ToDictionary(p => (p.Path.AdapterId, p.Path.SourceAddress), p => p.SpeedLimitKiB);
            Paths.Clear();
            foreach (var path in paths) Paths.Add(new(path) { Selected = selection.Contains((path.AdapterId, path.SourceAddress)), SpeedLimitKiB = limits.GetValueOrDefault((path.AdapterId, path.SourceAddress), "0") });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("DiscoveryError"); }
    }

}
