using DownloadManager.Core;
namespace DownloadManager.Tests;
public class QueueSchedulingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
    private static QueueJob Job(int priority, int age = 0) => new()
    { Source = new("https://example.test/file"), Destination = "file.bin", UseSystemRoute = true, Priority = priority, CreatedAt = Now.AddMinutes(-age) };

    [Fact]
    public void SkipsFutureAndNonqueuedJobsAndOrdersByPriority()
    {
        var low = Job(-1);
        var high = Job(2);
        Assert.Equal(high.Id, QueueScheduling.Next([low, high, Job(10) with { NotBefore = Now.AddSeconds(1) }, Job(10) with { State = QueueJobState.Paused }], Now)?.Id);
        Assert.Null(QueueScheduling.Next([high with { NotBefore = Now.AddSeconds(1) }], Now));
        Assert.Equal(high.Id, QueueScheduling.Next([high with { NotBefore = Now }], Now)?.Id);
    }
    [Fact]
    public void AgingEventuallyOutranksNewHighPriorityJobs()
    {
        var old = Job(-10, 110);
        Assert.Equal(old.Id, QueueScheduling.Next([Job(10), old], Now)?.Id);
    }
    [Fact]
    public void StateMachineRejectsTerminalRestartAndPrematureCompletion()
    {
        var job = Job(0);
        Assert.Throws<InvalidOperationException>(() => job.Transition(QueueJobState.Completed));
        var running = job.Transition(QueueJobState.Running);
        var paused = running.Transition(QueueJobState.Paused);
        Assert.Equal(QueueJobState.Queued, paused.Transition(QueueJobState.Queued).State);
        Assert.Throws<InvalidOperationException>(() => running.Transition(QueueJobState.Completed).Transition(QueueJobState.Queued));
    }
    [Theory]
    [InlineData("2026-11-15T09:00:00+03:30", "2026-11-15T05:30:00Z")]
    [InlineData("2026-11-15T09:00:00Z", "2026-11-15T09:00:00Z")]
    public void ScheduleRequiresAnExplicitOffsetAndComparesInstants(string input, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), QueueScheduling.ParseStart(input));
    [Theory]
    [InlineData("2026-11-15T09:00:00")]
    [InlineData("tomorrow")]
    [InlineData("09:00+03:30")]
    [InlineData("11/15/2026 09:00+03:30")]
    public void RejectsAmbiguousScheduleInput(string input) => Assert.Throws<FormatException>(() => QueueScheduling.ParseStart(input));
}
