using DownloadManager.Core;
namespace DownloadManager.Tests;
public class QueueJobSettingsTests
{
    [Fact]
    public void EditingPreservesIdentityIntegrityProgressAndState()
    {
        var job=new QueueJob { Source=new("https://example.test/f?secret=token"),Destination="same.bin",UseSystemRoute=true,
            State=QueueJobState.Paused, CompletedBytes=100,TotalBytes=1000,Options=new DownloadOptions {ExpectedSha256=new string('a',64)} };
        var paths=new List<QueuedPath> {new("adapter","192.0.2.1",1024)};
        var changed=new QueueJobSettings {Workers=16,BytesPerSecondLimit=4096,Paths=paths,Priority=5,Category=" Archives "}.Apply(job);
        paths.Clear();
        Assert.Equal(job.Id,changed.Id); Assert.Equal(job.Source,changed.Source); Assert.Equal(job.Destination,changed.Destination);
        Assert.Equal(job.Options.ExpectedSha256,changed.Options.ExpectedSha256); Assert.Equal(job.CreatedAt,changed.CreatedAt);
        Assert.Equal(job.State,changed.State);Assert.Equal(100,changed.CompletedBytes);Assert.Equal(1000,changed.TotalBytes);
        Assert.Equal(16,changed.Options.Workers);Assert.Equal(4096,changed.Options.BytesPerSecondLimit);Assert.Equal("Archives",changed.Category);
        Assert.Single(changed.Paths);
    }
    [Theory]
    [InlineData(QueueJobState.Running)]
    [InlineData(QueueJobState.Completed)]
    [InlineData(QueueJobState.Cancelled)]
    public void ActiveAndTerminalJobsCannotBeEdited(QueueJobState state)
    {
        var job=new QueueJob { Source=new("https://example.test/f"),Destination="f",UseSystemRoute=true,State=state };
        Assert.Throws<InvalidOperationException>(()=>new QueueJobSettings {UseSystemRoute=true}.Apply(job));
    }
}
