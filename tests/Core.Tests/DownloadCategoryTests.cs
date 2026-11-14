using DownloadManager.Core;
namespace DownloadManager.Tests;
public class DownloadCategoryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bad\nCategory")]
    public void RejectsBlankOrControlCharacters(string value) => Assert.Throws<ArgumentException>(() => DownloadCategory.Normalize(value));
    [Fact]
    public void TrimsAndBoundsCategories()
    {
        Assert.Equal("Music", DownloadCategory.Normalize(" Music "));
        Assert.Equal(64, DownloadCategory.Normalize(new string('a', 64)).Length);
        Assert.Throws<ArgumentException>(() => DownloadCategory.Normalize(new string('a', 65)));
    }
    [Fact]
    public void ExistingJobsDefaultToGeneralAndInvalidPreferencesAreRejected()
    {
        var job = System.Text.Json.JsonSerializer.Deserialize<QueueJob>("{\"Source\":\"https://example.test/file\",\"Destination\":\"file.bin\",\"UseSystemRoute\":true}");
        Assert.Equal("General", job!.Category);
        Assert.Throws<ArgumentException>(() => new DesktopPreferences { Workers = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new DesktopPreferences { SpeedLimitKiB = -1 }.Validate());
    }
}
