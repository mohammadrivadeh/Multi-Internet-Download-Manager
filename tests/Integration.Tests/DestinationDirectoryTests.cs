using DownloadManager.Core;
using DownloadManager.Infrastructure.Persistence;
namespace DownloadManager.Tests;
public class DestinationDirectoryTests
{
    [Fact]
    public void CreatesOnlyPlannedDirectoryAndRejectsForgedPlans()
    {
        var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var plan=DestinationRules.Plan(root,"Archives",new("https://example.test/a.zip"));
            Assert.False(Directory.Exists(plan.CategoryDirectory));
            DestinationDirectory.CreateFor(plan);
            Assert.True(Directory.Exists(plan.CategoryDirectory)); Assert.False(File.Exists(plan.FullPath));
            Assert.Throws<ArgumentException>(() => DestinationDirectory.CreateFor(plan with { CategoryDirectory=Path.GetTempPath() }));
        }
        finally { Directory.Delete(root,true); }
    }
}
