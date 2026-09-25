namespace NLightning.Daemon.Tests.Client;

using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Ipc;
using TestCollections;

[Collection(SerialTestCollection.Name)]
public class ClientAppTests
{
    private static readonly string s_missingCookieDir =
        Path.Combine(Path.GetTempPath(), $"nltg-missing-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public async Task GivenHelpAndMissingCookieDirectory_WhenRunAsync_ThenShowsHelpAndSucceeds(string helpFlag)
    {
        // Arrange
        string[] args = ["--cookie", s_missingCookieDir, helpFlag];

        // Act
        var exitCode = await ClientApp.RunAsync(args, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ClientApp.Success, exitCode);
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("connect-peer")]
    [InlineData("openchannel")]
    [InlineData("open-channel", "peer@host")]
    [InlineData("openchannel", "-n", "regtest")]
    [InlineData("openchannel", "--network", "regtest")]
    [InlineData("openchannel", "peer@host", "--cookie=/tmp/nltg.cookie")]
    [InlineData("unknown-command")]
    public async Task GivenMissingCommandArguments_WhenRunAsync_ThenReturnsUsageError(
        string command, params string[] commandArgs)
    {
        // Arrange
        string[] args = ["--cookie", s_missingCookieDir, command, .. commandArgs];

        // Act
        var exitCode = await ClientApp.RunAsync(args, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ClientApp.UsageError, exitCode);
    }

    [Fact]
    public async Task GivenOneArgument_WhenOpenChannelHandleAsync_ThenThrowsArgumentException()
    {
        // Arrange
        await using var client = new NamedPipeIpcClient("nonexistent.ipc", "nonexistent.cookie");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => OpenChannelMessageHandler.HandleAsync(
                                                        ["peer@host"], client,
                                                        TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("getaddress")]
    [InlineData("info")]
    public void GivenCommandWithOptionalArguments_WhenValidateArguments_ThenIsValid(string command)
    {
        // Act
        var error = ClientApp.ValidateArguments(command, []);

        // Assert
        Assert.Null(error);
    }
}