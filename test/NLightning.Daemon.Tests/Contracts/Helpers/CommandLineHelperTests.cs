namespace NLightning.Daemon.Tests.Contracts.Helpers;

using Daemon.Contracts.Helpers;
using TestCollections;

[Collection(SerialTestCollection.Name)]
public class CommandLineHelperTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _originalCookieEnv;
    private readonly string? _originalNetworkEnv;

    public CommandLineHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nltg-clh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _originalCookieEnv = Environment.GetEnvironmentVariable(CommandLineHelper.CookieEnvironmentVariable);
        _originalNetworkEnv = Environment.GetEnvironmentVariable(CommandLineHelper.NetworkEnvironmentVariable);
        Environment.SetEnvironmentVariable(CommandLineHelper.CookieEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(CommandLineHelper.NetworkEnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CommandLineHelper.CookieEnvironmentVariable, _originalCookieEnv);
        Environment.SetEnvironmentVariable(CommandLineHelper.NetworkEnvironmentVariable, _originalNetworkEnv);
        Directory.Delete(_tempDir, true);
    }

    [Theory]
    [InlineData("--cookie")]
    [InlineData("-c")]
    public void GivenCookieOptionWithSeparateValue_WhenGetCookiePath_ThenUsesTheValue(string option)
    {
        // Arrange
        var cookieFile = Path.Combine(_tempDir, "nltg.cookie");
        string[] args = [option, cookieFile, "listpeers"];

        // Act
        var result = CommandLineHelper.GetCookiePath(args);

        // Assert
        Assert.Equal(Path.GetFullPath(_tempDir).TrimEnd(Path.DirectorySeparatorChar), result);
    }

    [Theory]
    [InlineData(new[] { "--network=regtest", "listpeers" }, "listpeers")]
    [InlineData(new[] { "--cookie=/tmp/nltg.cookie", "listpeers" }, "listpeers")]
    [InlineData(new[] { "--network", "regtest", "listpeers" }, "listpeers")]
    [InlineData(new[] { "-n", "regtest", "listpeers" }, "listpeers")]
    [InlineData(new[] { "-c", "/tmp/nltg.cookie", "ListPeers" }, "listpeers")]
    [InlineData(new[] { "--network", "regtest" }, null)]
    public void GivenOptions_WhenGetCommand_ThenReturnsFirstPositionalArgument(string[] args, string? expected)
    {
        // Act
        var result = CommandLineHelper.GetCommand(args);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GivenOptionValueEqualToCommand_WhenGetCommandArguments_ThenOnlyArgumentsAfterCommandAreReturned()
    {
        // Arrange
        string[] args = ["--network", "connect", "connect", "peer@host"];

        // Act
        var command = CommandLineHelper.GetCommand(args);
        var result = CommandLineHelper.GetCommandArguments(command!, args);

        // Assert
        Assert.Equal("connect", command);
        Assert.Equal(["peer@host"], result);
    }

    [Theory]
    [InlineData(new[] { "getaddress", "--network", "regtest" }, new string[0])]
    [InlineData(new[] { "getaddress", "-n", "regtest", "p2tr" }, new[] { "p2tr" })]
    [InlineData(new[] { "getaddress", "p2tr", "-c", "/tmp/nltg.cookie" }, new[] { "p2tr" })]
    [InlineData(new[] { "getaddress", "--network=regtest", "--cookie=/tmp/nltg.cookie" }, new string[0])]
    public void GivenOptionsAfterCommand_WhenGetCommandArguments_ThenOptionsAreNotCommandArguments(
        string[] args, string[] expected)
    {
        // Act
        var result = CommandLineHelper.GetCommandArguments("getaddress", args);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("-?")]
    [InlineData("-h")]
    [InlineData("--help")]
    public void GivenHelpFlag_WhenIsHelpRequested_ThenReturnsTrue(string flag)
    {
        // Act
        var result = CommandLineHelper.IsHelpRequested([flag]);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GivenCookieAndNetworkEnvironment_WhenGetCookiePath_ThenCookieEnvironmentIsUsed()
    {
        // Arrange
        Environment.SetEnvironmentVariable(CommandLineHelper.NetworkEnvironmentVariable,
                                           $"nonexistent-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(CommandLineHelper.CookieEnvironmentVariable,
                                           Path.Combine(_tempDir, "nltg.cookie"));

        // Act
        var result = CommandLineHelper.GetCookiePath([]);

        // Assert
        Assert.Equal(Path.GetFullPath(_tempDir).TrimEnd(Path.DirectorySeparatorChar), result);
    }

    [Fact]
    public void GivenCookieArgumentAfterNetworkArgument_WhenGetCookiePath_ThenCookieArgumentWins()
    {
        // Arrange
        string[] args = ["--network", $"nonexistent-{Guid.NewGuid():N}", "--cookie", _tempDir];

        // Act
        var result = CommandLineHelper.GetCookiePath(args);

        // Assert
        Assert.Equal(Path.GetFullPath(_tempDir).TrimEnd(Path.DirectorySeparatorChar), result);
    }
}