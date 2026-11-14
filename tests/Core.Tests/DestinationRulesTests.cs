using DownloadManager.Core;
namespace DownloadManager.Tests;
public class DestinationRulesTests
{
    [Theory]
    [InlineData("CON.txt")]
    [InlineData("LPT1")]
    [InlineData("COM¹.bin")]
    [InlineData("../escape")]
    [InlineData("file:stream")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void RejectsInvalidWindowsLeaves(string value) => Assert.Throws<ArgumentException>(() => DestinationRules.ValidateLeaf(value));
    [Fact]
    public void PlansOnlyUnderSelectedCategoryAndExcludesQueryFromFilename()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var result = DestinationRules.Plan(root, " Documents ", new("https://example.test/My%20file.zip?secret=token"));
        Assert.Equal(Path.Combine(root,"Documents","My file.zip"),result.FullPath);
        Assert.Equal("download.bin",Path.GetFileName(DestinationRules.Plan(root,"General",new("https://example.test/")).FullPath));
        Assert.Throws<ArgumentException>(() => DestinationRules.Plan(root,"../other",new("https://example.test/a")));
        Assert.Throws<ArgumentException>(() => DestinationRules.Plan(root,"General",new("https://example.test/%2Foutside")));
    }
}
