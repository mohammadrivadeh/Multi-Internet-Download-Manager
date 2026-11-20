using DownloadManager.NetworkBalancer;

namespace DownloadManager.Tests;

public sealed class CaptureSessionTests
{
    [Fact]
    public void StartInfoElevatesOnlyTheCaptureHostAndPassesPipeIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var executable = Path.GetFullPath(Path.Combine("preview with spaces", "desktop-capture.exe"));
        const string pipeName = "multi-internet-balancer-0123456789abcdef0123456789abcdef";
        var launch = CaptureSession.CreateStartInfo(executable, pipeName, 12345, [new(3, "Wi-Fi 2"), new(12, "USB: Ethernet")]);

        Assert.Equal(executable, launch.FileName);
        Assert.True(launch.UseShellExecute);
        Assert.Equal("runas", launch.Verb);
        Assert.Equal(Environment.SystemDirectory, launch.WorkingDirectory);
        Assert.Equal([pipeName, "12345", "3:Wi-Fi 2", "12:USB: Ethernet"], launch.ArgumentList.ToArray());
        Assert.False(launch.RedirectStandardInput);
    }
}
