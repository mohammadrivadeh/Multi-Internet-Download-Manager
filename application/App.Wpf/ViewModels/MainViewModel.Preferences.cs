using CommunityToolkit.Mvvm.Input;
using DownloadManager.Core;
namespace DownloadManager.App.ViewModels;
public partial class MainViewModel
{
    private DesktopPreferences savedPreferences = new();

    internal int MotionLevel => savedPreferences.MotionLevel;

    internal async Task<string?> SaveMotionLevelAsync(int level)
    {
        if (!IsReady || !preferencesWritable) return Text("PreferencesLoadError");
        var updated = CapturePreferences() with { MotionLevel = level };
        try
        {
            await session.SavePreferencesAsync(updated);
            savedPreferences = updated;
            Presentation.AppMotion.Level = level;
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Text("PreferencesSaveError"); }
    }

    private DesktopPreferences CapturePreferences() => savedPreferences with { Queues = Queues.ToList() };

    internal CategoryDownloadSettings GetCategoryDefaults(string name) => savedPreferences.DefaultsFor(name);

    internal async Task<string?> SaveCategoryDefaultsAsync(CategoryDownloadSettings settings)
    {
        if (!IsReady || !preferencesWritable) return Text("PreferencesLoadError");
        try
        {
            settings.Validate();
            var updated = CapturePreferences() with
            {
                CategoryDefaults = savedPreferences.CategoryDefaults
                    .Where(item => !string.Equals(item.Category, settings.Category, StringComparison.OrdinalIgnoreCase))
                    .Append(settings).ToList()
            };
            await session.SavePreferencesAsync(updated);
            savedPreferences = updated;
            await RefreshJobsAsync();
            return null;
        }
        catch (ArgumentException) { return "Enter 1–128 requests, a priority from −10 to 10, and a full folder path."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Text("PreferencesSaveError"); }
    }

    [RelayCommand]
    private async Task SaveQueueSettingsAsync()
    {
        Status = await SaveQueueSettingsForAsync(SelectedQueueName, QueueConcurrency, QueueStartTime, QueueStopTime)
            ?? Text("QueueSettingsSavedStatus");
    }

    internal async Task<string?> SaveQueueSettingsForAsync(string name, string concurrency, string start, string stop)
    {
        if (!IsReady || !preferencesWritable) return Text("PreferencesLoadError");
        var old = Queues.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));
        if (old is null || queueStops.ContainsKey(name)) return Text("StopQueueBeforeManageStatus");
        try
        {
            var parallelism = string.Equals(concurrency.Trim(), "All", StringComparison.OrdinalIgnoreCase) ? 0 : int.Parse(concurrency);
            var updated = old with { MaxConcurrentDownloads = parallelism, StartTime = start.Trim(), StopTime = stop.Trim() };
            updated.Validate();
            await session.SavePreferencesAsync(CapturePreferences() with { Queues = Queues.Select(q => q == old ? updated : q).ToList() });
            Queues[Queues.IndexOf(old)] = updated;
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) { return Text("QueueSettingsInputError"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Text("PreferencesSaveError"); }
    }

    [RelayCommand]
    private async Task CreateQueueAsync()
    {
        if (!IsReady || !preferencesWritable) return;
        try
        {
            var name = ValidateNewQueueName(NewQueueName);
            if (Queues.Any(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase))) { Status = Text("QueueNameExistsStatus"); return; }
            var item = new DownloadQueueSettings { Name = name };
            var updated = Queues.Append(item).ToList();
            await session.SavePreferencesAsync(CapturePreferences() with { Queues = updated });
            Queues.Add(item); NewQueueName = ""; SelectedQueueName = name; MoveTargetQueueName = name; Status = string.Format(Text("QueueCreatedStatus"), name);
        }
        catch (ArgumentException) { Status = Text("QueueNameInputError"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("PreferencesSaveError"); }
    }

    [RelayCommand]
    private async Task RenameQueueAsync()
    {
        if (!IsReady || !preferencesWritable || queue is null) return;
        var old = Queues.FirstOrDefault(q => string.Equals(q.Name, SelectedQueueName, StringComparison.OrdinalIgnoreCase));
        if (old is null || string.Equals(old.Name, QueueJob.MainQueue, StringComparison.OrdinalIgnoreCase)) { Status = Text("MainQueueRenameStatus"); return; }
        if (queueStops.ContainsKey(old.Name)) { Status = Text("StopQueueBeforeManageStatus"); return; }
        try
        {
            var name = ValidateNewQueueName(NewQueueName);
            if (Queues.Any(q => !ReferenceEquals(q, old) && string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase))) { Status = Text("QueueNameExistsStatus"); return; }
            await Task.Run(() => queue.RenameQueueAsync(old.Name, name));
            var updated = old with { Name = name };
            var settings = Queues.Select(q => q == old ? updated : q).ToList();
            try { await session.SavePreferencesAsync(CapturePreferences() with { Queues = settings }); }
            catch { await Task.Run(() => queue.RenameQueueAsync(name, old.Name)); throw; }
            Queues[Queues.IndexOf(old)] = updated; NewQueueName = ""; SelectedQueueName = name; MoveTargetQueueName = name;
            if (startedQueues.Remove(old.Name)) startedQueues.Add(name);
            if (pausedQueues.Remove(old.Name)) pausedQueues.Add(name);
            OnPropertyChanged(nameof(QueueStatuses));
            Status = string.Format(Text("QueueRenamedStatus"), name);
        }
        catch (ArgumentException) { Status = Text("QueueNameInputError"); }
        catch (InvalidOperationException) { Status = Text("StopQueueBeforeManageStatus"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("PreferencesSaveError"); }
    }

    [RelayCommand]
    private async Task DeleteQueueAsync()
    {
        if (!IsReady || !preferencesWritable || queue is null) return;
        var selected = Queues.FirstOrDefault(q => string.Equals(q.Name, SelectedQueueName, StringComparison.OrdinalIgnoreCase));
        if (selected is null || string.Equals(selected.Name, QueueJob.MainQueue, StringComparison.OrdinalIgnoreCase)) { Status = Text("MainQueueDeleteStatus"); return; }
        try
        {
            if (queueStops.TryGetValue(selected.Name, out var active))
            {
                startedQueues.Remove(selected.Name); active.Cancel();
                if (queueRunTasks.TryGetValue(selected.Name, out var task)) await task;
            }
            await Task.Run(() => queue.MoveQueueJobsAsync(selected.Name, QueueJob.MainQueue));
            var settings = Queues.Where(q => q != selected).ToList();
            await session.SavePreferencesAsync(CapturePreferences() with { Queues = settings });
            pausedQueues.Remove(selected.Name);
            startedQueues.Remove(selected.Name);
            Queues.Remove(selected); SelectedQueueName = QueueJob.MainQueue; MoveTargetQueueName = QueueJob.MainQueue;
            Status = string.Format(Text("QueueDeletedStatus"), selected.Name);
            await RefreshJobsAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueManageErrorStatus"); }
    }

    private string ValidateNewQueueName(string value)
    {
        var name = value.Trim();
        if (name.Length is < 1 or > 48 || name.Any(char.IsControl)) throw new ArgumentException();
        return name;
    }
    [RelayCommand]
    private async Task AssignCategoryAsync()
    {
        if (!IsReady || queue is null || SelectedJob is null) return;
        if (await AssignCategoryToAsync(SelectedJob.Job.Id, Category) is { } error) Status = error;
    }

    internal async Task<string?> AssignCategoryToAsync(Guid id, string category)
    {
        if (!IsReady || queue is null) return Text("QueueActionError");
        try { await Task.Run(() => queue.SetCategoryAsync(id, category)); await RefreshJobsAsync(); return null; }
        catch (ArgumentException) { return Text("CategoryInputError"); }
        catch (InvalidOperationException) { return Text("QueueActionError"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Text("QueueSaveError"); }
    }

    internal async Task<string> UpdateDownloadSelectionAsync(IReadOnlyList<Guid> ids, string value, bool move)
    {
        if (!IsReady || queue is not { } coordinator) return Text("QueueActionError");
        if (!move)
        {
            try { value = DownloadCategory.Normalize(value); }
            catch (ArgumentException) { return Text("CategoryInputError"); }
        }
        else if (!Queues.Any(item => string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase)))
            return "Choose a destination queue.";

        var updated = 0;
        try
        {
            foreach (var id in ids)
            {
                if (move) await coordinator.SetQueueNameAsync(id, value);
                else await coordinator.SetCategoryAsync(id, value);
                updated++;
            }
        }
        catch (InvalidOperationException) { return $"Updated {updated} of {ids.Count}. {Text("QueueActionError")}"; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return $"Updated {updated} of {ids.Count}. {Text("QueueSaveError")}"; }
        finally { await RefreshJobsAsync(); }
        return move ? $"Moved {updated} to {value}." : $"Set category on {updated} downloads.";
    }

    [RelayCommand]
    private async Task MoveSelectedQueueAsync()
    {
        if (!IsReady || queue is null || SelectedJob is null) return;
        if (SelectedJob.Job.State == QueueJobState.Running) { Status = Text("StopQueueBeforeMoveStatus"); return; }
        var id = SelectedJob.Job.Id;
        try { await Task.Run(() => queue.SetQueueNameAsync(id, MoveTargetQueueName)); await RefreshJobsAsync(); Status = Text("DownloadMovedStatus"); }
        catch (InvalidOperationException) { Status = Text("StopQueueBeforeMoveStatus"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Text("QueueSaveError"); }
    }
}
