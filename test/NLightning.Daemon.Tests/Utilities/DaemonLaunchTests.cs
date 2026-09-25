using System.Diagnostics;

namespace NLightning.Daemon.Tests.Utilities;

using Daemon.Utilities;

public class DaemonLaunchTests
{
    [Fact]
    public void GivenDaemonAndPasswordArgs_WhenBuildDaemonChildArgs_ThenTheyAreDroppedAndChildFlagAdded()
    {
        // Arrange
        string[] args =
        [
            "--daemon", "true", "--network", "regtest", "--password", "secret", "--password-file=/tmp/pw",
            "--password-stdin", "--daemon=true", "--config", "/tmp/my config.json", "--daemon"
        ];

        // Act
        var result = DaemonUtils.BuildDaemonChildArgs(args);

        // Assert
        Assert.Equal(["--network", "regtest", "--config", "/tmp/my config.json", "--daemon-child"], result);
    }

    [Fact]
    public async Task GivenLauncherScript_WhenRunWithArgsContainingSpacesAndQuotes_ThenReportsPidOfDetachedProgram()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");

        // Arrange
        var marker = Path.Combine(Path.GetTempPath(), $"nltg-launch-{Guid.NewGuid():N}");
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(DaemonUtils.UnixDaemonLauncherScript);
        startInfo.ArgumentList.Add("/bin/sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("printf '%s' \"$1\" > \"$0\"");
        startInfo.ArgumentList.Add(marker);
        startInfo.ArgumentList.Add("a \"quoted\" $value");

        try
        {
            // Act
            using var shell = Process.Start(startInfo)!;
            var pidText = await shell.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken);
            await shell.WaitForExitAsync(TestContext.Current.CancellationToken);

            for (var i = 0; i < 50 && !File.Exists(marker); i++)
                await Task.Delay(100, TestContext.Current.CancellationToken);

            // Assert
            Assert.True(int.TryParse(pidText, out var pid) && pid > 0);
            Assert.Equal("a \"quoted\" $value",
                         await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(marker);
        }
    }
}