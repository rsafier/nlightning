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
        Assert.Equal(["--network", "regtest", "--config", "/tmp/my config.json", "--password-stdin", "--daemon-child"], result);
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

    [Fact]
    public async Task Given_ThePasswordWrittenToTheLauncher_When_TheDetachedProgramReadsStdin_Then_ItGetsThePassword()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");

        // Arrange (SR-11: the daemon child gets the password over its stdin, not in NLTG_PASSWORD)
        const string password = "päss wörd \"$HOME\"";
        var marker = Path.Combine(Path.GetTempPath(), $"nltg-launch-{Guid.NewGuid():N}");
        var startInfo = DaemonUtils.CreateUnixDaemonStartInfo(
            "/bin/sh", ["-c", "IFS= read -r line; printf '%s' \"$line\" > \"$0.tmp\" && mv \"$0.tmp\" \"$0\"", marker]);

        try
        {
            // Act
            using var shell = Process.Start(startInfo)!;
            DaemonUtils.WritePasswordToChild(shell.StandardInput, password);
            var pidText = await shell.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken);
            await shell.WaitForExitAsync(TestContext.Current.CancellationToken);

            for (var i = 0; i < 50 && !File.Exists(marker); i++)
                await Task.Delay(100, TestContext.Current.CancellationToken);

            // Assert
            Assert.True(int.TryParse(pidText, out var pid) && pid > 0);
            Assert.Equal(password, await File.ReadAllTextAsync(marker, PasswordUtils.StdinEncoding,
                                                               TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public void Given_NltgPasswordInTheEnvironment_When_CreateDaemonStartInfo_Then_TheChildDoesNotInheritIt()
    {
        // Arrange
        var original = Environment.GetEnvironmentVariable(PasswordUtils.PasswordEnvironmentVariable);
        Environment.SetEnvironmentVariable(PasswordUtils.PasswordEnvironmentVariable, "env-secret");

        try
        {
            // Act
            var unix = DaemonUtils.CreateUnixDaemonStartInfo("/usr/bin/nltg", ["--network", "regtest"]);
            var windows = DaemonUtils.CreateWindowsDaemonStartInfo("nltg.exe", ["--network", "regtest"]);

            // Assert
            Assert.False(unix.Environment.ContainsKey(PasswordUtils.PasswordEnvironmentVariable));
            Assert.False(windows.Environment.ContainsKey(PasswordUtils.PasswordEnvironmentVariable));
            Assert.True(unix.RedirectStandardInput);
            Assert.True(windows.RedirectStandardInput);
            Assert.DoesNotContain("env-secret", unix.ArgumentList);
            Assert.DoesNotContain("env-secret", windows.ArgumentList);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PasswordUtils.PasswordEnvironmentVariable, original);
        }
    }

    [Fact]
    public void Given_APassword_When_WritePasswordToChild_Then_WritesOneUtf8LineAndClosesTheStream()
    {
        // Arrange
        using var buffer = new MemoryStream();
        var writer = new StreamWriter(buffer, PasswordUtils.StdinEncoding);

        // Act
        DaemonUtils.WritePasswordToChild(writer, "sécret");

        // Assert
        Assert.Equal("sécret\n"u8.ToArray(), buffer.ToArray());
        Assert.False(buffer.CanWrite);
    }

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("carriage\rreturn")]
    public void Given_APasswordWithALineBreak_When_WritePasswordToChild_Then_ThrowsAndClosesTheStream(string password)
    {
        // Arrange
        using var buffer = new MemoryStream();
        var writer = new StreamWriter(buffer, PasswordUtils.StdinEncoding);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => DaemonUtils.WritePasswordToChild(writer, password));
        Assert.Empty(buffer.ToArray());
        Assert.False(buffer.CanWrite);
    }

    [Fact]
    public void Given_PasswordStdinAndAUtf8Pipe_When_ResolvePassword_Then_ReadsTheFirstLine()
    {
        // Arrange (what the daemon child sees)
        using var reader = new StreamReader(new MemoryStream("päss\n"u8.ToArray()), PasswordUtils.StdinEncoding);
        var args = DaemonUtils.BuildDaemonChildArgs(["--network", "regtest", "--daemon"]);

        // Act
        var password = PasswordUtils.ResolvePassword(args, reader, Serilog.Core.Logger.None);

        // Assert
        Assert.Equal("päss", password);
    }
}