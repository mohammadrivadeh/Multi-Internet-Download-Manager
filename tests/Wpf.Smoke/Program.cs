using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DownloadManager.App.Services;
using DownloadManager.App.ViewModels;
using DownloadManager.App.Views.Sections;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Persistence;
using DownloadManager.Infrastructure.Queueing;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var application = new DownloadManager.App.App();
        application.InitializeComponent();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var exitCode = 1;
        dispatcher.InvokeAsync(async () =>
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await VerifyNewDownloadAsync(dispatcher, deadline.Token);
                await VerifyRedownloadAsync(dispatcher, useSystemRoute: false, deadline.Token);
                await VerifyRedownloadAsync(dispatcher, useSystemRoute: true, deadline.Token);
                Console.WriteLine("PASS: New Download renders and validates. The queue controls start, stop, and resume the submitted job.");
                Console.WriteLine("PASS: Redownload starts a fresh copy with its original route and speed limits.");
                exitCode = 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
            }
            finally
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
        return exitCode;
    }

    private static async Task VerifyRedownloadAsync(Dispatcher dispatcher, bool useSystemRoute, CancellationToken token)
    {
        var original = new QueueJob
        {
            Source = new Uri("https://example.invalid/redownload.bin"),
            Destination = Path.Combine(Path.GetTempPath(), $"redownload-original-{Guid.NewGuid():N}.bin"),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            Options = new DownloadOptions { Workers = 4, BytesPerSecondLimit = 8192 },
            UseSystemRoute = useSystemRoute,
            Paths = useSystemRoute ? [] : [new("original-adapter", "192.0.2.45", 4096)],
            State = QueueJobState.Completed,
            CompletedBytes = 4,
            TotalBytes = 4
        };
        var destination = Path.Combine(Path.GetTempPath(), $"redownload-copy-{Guid.NewGuid():N}.bin");
        var desktop = new DestinationDesktop(dispatcher, destination);
        var session = new SmokeSession();
        session.Save(original);
        using var model = new MainViewModel(desktop, session);
        await model.InitializeAsync().WaitAsync(token);
        try
        {
            model.SelectedJob = model.Jobs.Single();
            Require(model.RedownloadSelectedCommand.CanExecute(null), "Redownload is disabled for a completed job.");
            await model.RedownloadSelectedCommand.ExecuteAsync(null).WaitAsync(token);
            var copy = await session.Executed.Task.WaitAsync(token);
            Require(desktop.Suggestion is { } suggestion && suggestion != original.Destination,
                "Redownload did not suggest a fresh path.");
            Require(copy.Id != original.Id && copy.Destination == destination && copy.Source == original.Source &&
                    copy.QueueName == original.QueueName && copy.CompletedBytes == 0 && copy.TotalBytes is null,
                "Redownload did not start a new job at zero in the selected destination.");
            Require(copy.UseSystemRoute == useSystemRoute && copy.Options == original.Options &&
                    copy.Paths.SequenceEqual(original.Paths),
                "Redownload changed the original route or speed limits.");
            Require(session.Load().Single(job => job.Id == original.Id) == original &&
                    model.Jobs.Single(row => row.Job.Id == original.Id).Job == original,
                "Redownload changed the original history row.");
            Require(!File.Exists(destination), "The fake transfer unexpectedly wrote a redownload file.");
        }
        finally
        {
            await model.ShutdownAsync().WaitAsync(token);
        }
    }

    private static async Task VerifyNewDownloadAsync(Dispatcher dispatcher, CancellationToken token)
    {
        var session = new SmokeSession();
        using var model = new MainViewModel(new DesktopServices(dispatcher), session);
        await model.InitializeAsync().WaitAsync(token);
        Require(model.IsReady, "The desktop model could not initialize its test queue.");

        var view = new NewDownloadView { DataContext = model };
        var size = new Size(560, 440);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();

        var url = (TextBox)view.FindName("UrlInput");
        var destination = (TextBox)view.FindName("DestinationInput");
        foreach (var field in new[] { url, destination })
        {
            var host = field.Template.FindName("PART_ContentHost", field) as FrameworkElement;
            Require(field.ActualWidth > 200 && host is not null && host.ActualHeight >= field.FontFamily.LineSpacing * field.FontSize,
                "A New Download text field does not leave room for one line of text.");
        }

        var buttons = Descendants<Button>(view).ToArray();
        var pasteLabel = (string)Application.Current.FindResource("PasteLinkLabel");
        var paste = buttons.Single(button => AutomationProperties.GetName(button) == pasteLabel);
        Require(Equals(paste.ToolTip, pasteLabel) && paste.Content is TextBlock, "Paste link is missing its accessible tooltip or icon.");
        var submit = buttons.Single(button => button.IsDefault);
        Require(submit.IsEnabled, "Start download is disabled after initialization.");

        url.SetCurrentValue(TextBox.TextProperty, "invalid download URL");
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind, token);
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await (model.AddCommand.ExecutionTask ?? throw new InvalidOperationException("Start did not invoke the bound command.")).WaitAsync(token);
        var error = Descendants<TextBlock>(view).Single(text => BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == nameof(model.NewDownloadError));
        Require(error.Visibility == Visibility.Visible && error.Text == (string)Application.Current.FindResource("QueueInputError"),
            "An invalid URL did not produce a visible form error.");
        Require(session.Load().Count == 0, "An invalid URL entered the queue.");

        const string source = "https://example.invalid/smoke.bin";
        var target = Path.Combine(Path.GetTempPath(), $"download-manager-smoke-{Guid.NewGuid():N}.bin");
        url.SetCurrentValue(TextBox.TextProperty, source);
        destination.SetCurrentValue(TextBox.TextProperty, target);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind, token);
        Require(model.Url == source && model.Destination == target, "The fields did not update the download model.");
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await (model.AddCommand.ExecutionTask ?? throw new InvalidOperationException("Start did not invoke the bound command.")).WaitAsync(token);
        var executed = await session.Executed.Task.WaitAsync(token);
        Require(executed.Source.AbsoluteUri == source && executed.Destination == target &&
                executed.QueueName == QueueJob.MainQueue && executed.Paths.Count == 1,
            "Start did not preserve the submitted URL, destination, queue and selected path.");
        Require(!File.Exists(target), "The fake transfer unexpectedly wrote a file.");

        var queueView = new QueueView { DataContext = model };
        queueView.Measure(new Size(900, 500));
        queueView.Arrange(new Rect(0, 0, 900, 500));
        queueView.UpdateLayout();
        var grid = (DataGrid)queueView.FindName("DownloadsGrid");
        await WaitForAsync(() => model.Jobs.Single().Job.State == QueueJobState.Running, token);
        grid.SetCurrentValue(DataGrid.SelectedItemProperty, model.Jobs.Single());
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind, token);
        Require(model.SelectedJob?.Job.Id == executed.Id, "Selecting the grid row did not select the download.");
        var stop = Descendants<Button>(queueView).Single(button => ReferenceEquals(button.Command, model.PauseSelectedCommand));
        var resume = Descendants<Button>(queueView).Single(button => ReferenceEquals(button.Command, model.ResumeSelectedCommand));
        Require(stop.IsEnabled && !resume.IsEnabled, "Queue actions do not reflect the running state.");

        InvokeButton(stop);
        await WaitForAsync(() => model.Jobs.Single().Job.State == QueueJobState.Paused, token);
        var paused = session.Load().Single();
        Require(paused.State == QueueJobState.Paused && paused.CompletedBytes == 2,
            "Stop did not persist the paused progress.");
        Require(!stop.IsEnabled && resume.IsEnabled, "Queue actions do not reflect the paused state.");

        InvokeButton(resume);
        await WaitForAsync(() => model.Jobs.Single().Job.State == QueueJobState.Completed, token);
        var completed = session.Load().Single();
        Require(completed.State == QueueJobState.Completed && completed.CompletedBytes == 4 &&
                session.Resumed?.Id == executed.Id && session.Resumed.CompletedBytes == 2,
            "Resume did not restart the same job from its saved progress.");
        await model.ShutdownAsync().WaitAsync(token);

        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        Require(pixels[4 * 20 + stride * 20 + 3] == byte.MaxValue, "The form did not render an opaque surface.");
    }

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        var command = (IInvokeProvider)peer.GetPattern(PatternInterface.Invoke);
        command.Invoke();
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private static void Require(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }

    private sealed class DestinationDesktop(Dispatcher dispatcher, string destination) : IDesktopServices
    {
        private readonly DesktopServices desktop = new(dispatcher);
        public string? Suggestion { get; private set; }

        public string Text(string key) => desktop.Text(key);
        public string ChooseDestination(string suggestion)
        {
            Suggestion = suggestion;
            return destination;
        }
        public string? ChooseFolder() => null;
        public void Post(Action action) => desktop.Post(action);
        public IDesktopTimer CreateTimer(Func<Task> tick) => desktop.CreateTimer(tick);
    }

    private sealed class SmokeSession : IQueueSession, IQueueStore, IQueuedDownloadExecutor
    {
        private readonly ConcurrentDictionary<Guid, QueueJob> jobs = new();
        private int attempts;
        public TaskCompletionSource<QueueJob> Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public QueueJob? Resumed { get; private set; }

        public async Task<QueueCoordinator> InitializeAsync()
        {
            var queue = new QueueCoordinator(this, this);
            await queue.InitializeAsync();
            return queue;
        }

        public Task<DesktopPreferences> LoadPreferencesAsync() =>
            Task.FromResult(new DesktopPreferences { DownloadAdapters = [new("smoke-adapter", "192.0.2.1")] });

        public Task SavePreferencesAsync(DesktopPreferences value) => Task.CompletedTask;
        public void Dispose() { }
        public IReadOnlyList<QueueJob> Load() => jobs.Values.ToArray();
        public void Save(QueueJob job) => jobs[job.Id] = job;
        public void Remove(Guid id) => jobs.TryRemove(id, out _);

        public async Task ExecuteAsync(QueueJob job, IProgress<DownloadProgress>? progress, CancellationToken token)
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                progress?.Report(new(DownloadStage.Downloading, 2, 4, false));
                Executed.TrySetResult(job);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return;
            }
            Resumed = job;
            progress?.Report(new(DownloadStage.Completed, 4, 4, false));
        }
    }
}
