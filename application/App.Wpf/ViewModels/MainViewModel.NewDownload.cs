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
    internal bool DownloadAdded { get; private set; }
    [ObservableProperty] private string newDownloadQueue = QueueJob.MainQueue;
    [ObservableProperty] private string newDownloadError = "";
    private string? automaticDestination;
    public IEnumerable<CategoryOption> DownloadCategories => Categories.Where(option => option.Key.Length > 0).ToArray();

    internal void PrepareNewDownload()
    {
        NewDownloadQueue = QueueJob.MainQueue;
        NewDownloadError = "";
        automaticDestination = Destination;
        var requested = string.IsNullOrEmpty(CategoryFilter) ? savedPreferences.Category : CategoryFilter;
        Category = DownloadCategories.FirstOrDefault(item => string.Equals(item.Key, requested, StringComparison.OrdinalIgnoreCase))?.Key
            ?? DownloadCategory.Default;
        ApplyCategoryDefaults(Category);
    }

    partial void OnCategoryChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) ApplyCategoryDefaults(value);
    }

    private void ApplyCategoryDefaults(string name)
    {
        var settings = GetCategoryDefaults(name);
        WorkerCount = settings.Workers.ToString();
        Priority = settings.Priority.ToString();
        DestinationRoot = settings.DestinationRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        UpdateAutomaticDestination(Url);
    }

    partial void OnUrlChanged(string value) => UpdateAutomaticDestination(value);
    [RelayCommand]
    private void Browse()
    {
        if (desktop.ChooseDestination() is { } chosen) Destination = chosen;
    }

    private void UpdateAutomaticDestination(string url)
    {
        if (automaticDestination is null || !string.Equals(Destination, automaticDestination, StringComparison.Ordinal))
            return;
        plannedDestination = null;
        var suggested = DestinationRoot;
        try
        {
            if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var source) && source.Scheme is "http" or "https")
            {
                plannedDestination = DestinationRules.Plan(DestinationRoot, Category, source);
                suggested = plannedDestination.FullPath;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException) { }
        Destination = automaticDestination = suggested;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        DownloadAdded = false;
        NewDownloadError = "";
        if (!IsReady || queue is null) return;
        try
        {
            if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var source) || string.IsNullOrWhiteSpace(Destination)
                || !int.TryParse(WorkerCount, out var workers) || !int.TryParse(Priority, out var priorityValue))
                throw new ArgumentException();
            if (string.Equals(Destination, DestinationRoot, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(Destination))
            {
                NewDownloadError = "The base download folder is unavailable. Choose another destination.";
                return;
            }
            var destination = Directory.Exists(Destination) ? Path.Combine(Destination, GetSuggestedFilename(source)) : Destination;
            if (File.Exists(destination)) { NewDownloadError = Text("DestinationExistsStatus"); return; }
            var job = new QueueJob
            {
                Source = source, Destination = destination, UseSystemRoute = true,
                Options = new DownloadOptions { Workers = workers, ExpectedSha256 = string.IsNullOrWhiteSpace(ExpectedSha256) ? null : ExpectedSha256.Trim() },
                DailyWindow = DailyTransferWindow.Parse(WindowStart, WindowEnd, WindowOffset), Category = DownloadCategory.Normalize(Category),
                QueueName = NewDownloadQueue, Priority = priorityValue, NotBefore = QueueScheduling.ParseStart(ScheduledStart)
            };
            job.Validate();
            var plan = plannedDestination;
            if (plan is not null && string.Equals(System.IO.Path.GetFullPath(job.Destination), plan.FullPath, StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => DestinationDirectory.CreateFor(plan));
            await Task.Run(() => queue.EnqueueAsync(job));
            DownloadAdded = true;
            await RefreshJobsAsync();
            await StartQueueAsync(job.QueueName);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { NewDownloadError = Text("QueueInputError"); }
        catch (InvalidOperationException) { NewDownloadError = Text("QueueConflictError"); }
        catch (DirectoryNotFoundException) { NewDownloadError = "The base download folder is unavailable. Choose another destination."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { NewDownloadError = Text("QueueSaveError"); }
    }

    private static string GetSuggestedFilename(Uri source)
    {
        var filename = Uri.UnescapeDataString(source.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);
        filename = Path.GetFileName(filename);
        if (string.IsNullOrWhiteSpace(filename))
            filename = $"{source.Host}.download";
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            filename = filename.Replace(invalidCharacter, '_');
        return filename;
    }

}
