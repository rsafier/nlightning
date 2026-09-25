using Serilog;

namespace NLightning.Daemon.Tests.Utilities;

using Daemon.Utilities;
using TestCollections;

[Collection(SerialTestCollection.Name)]
public class PasswordUtilsTests : IDisposable
{
    private readonly string? _originalEnvPassword;
    private readonly Mock<ILogger> _loggerMock = new();
    private readonly string _tempFile = Path.GetTempFileName();

    public PasswordUtilsTests()
    {
        _originalEnvPassword = Environment.GetEnvironmentVariable(PasswordUtils.PasswordEnvironmentVariable);
        Environment.SetEnvironmentVariable(PasswordUtils.PasswordEnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PasswordUtils.PasswordEnvironmentVariable, _originalEnvPassword);
        File.Delete(_tempFile);
    }

    [Fact]
    public void GivenEnvironmentVariable_WhenResolvePassword_ThenUsesItWithoutWarning()
    {
        // Arrange
        Environment.SetEnvironmentVariable(PasswordUtils.PasswordEnvironmentVariable, "env-secret");

        // Act
        var password = PasswordUtils.ResolvePassword(["--network", "regtest"], TextReader.Null, _loggerMock.Object);

        // Assert
        Assert.Equal("env-secret", password);
        VerifyWarned(Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GivenPasswordFile_WhenResolvePassword_ThenReadsItAndBeatsEnvironment(bool equalsForm)
    {
        // Arrange
        File.WriteAllText(_tempFile, "file-secret\n");
        Environment.SetEnvironmentVariable(PasswordUtils.PasswordEnvironmentVariable, "env-secret");
        string[] args = equalsForm ? [$"--password-file={_tempFile}"] : ["--password-file", _tempFile];

        // Act
        var password = PasswordUtils.ResolvePassword(args, TextReader.Null, _loggerMock.Object);

        // Assert
        Assert.Equal("file-secret", password);
    }

    [Fact]
    public void GivenPasswordStdin_WhenResolvePassword_ThenReadsFirstLine()
    {
        // Arrange
        using var stdin = new StringReader("stdin-secret\nignored\n");

        // Act
        var password = PasswordUtils.ResolvePassword(["--password-stdin"], stdin, _loggerMock.Object);

        // Assert
        Assert.Equal("stdin-secret", password);
    }

    [Theory]
    [InlineData("--password", "cli-secret")]
    [InlineData("--password=cli-secret")]
    public void GivenPasswordOnCommandLine_WhenResolvePassword_ThenUsesItAndWarns(params string[] args)
    {
        // Act
        var password = PasswordUtils.ResolvePassword(args, TextReader.Null, _loggerMock.Object);

        // Assert
        Assert.Equal("cli-secret", password);
        VerifyWarned(Times.Once());
    }

    [Fact]
    public void GivenNoSource_WhenResolvePassword_ThenReturnsNull()
    {
        // Act
        var password = PasswordUtils.ResolvePassword(["--network", "regtest"], TextReader.Null, _loggerMock.Object);

        // Assert
        Assert.Null(password);
    }

    private void VerifyWarned(Times times)
    {
        _loggerMock.Verify(x => x.Warning(It.IsAny<string>(), It.IsAny<object[]>()), times);
    }
}