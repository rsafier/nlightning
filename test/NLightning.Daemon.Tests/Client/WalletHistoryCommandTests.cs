using MessagePack;

namespace NLightning.Daemon.Tests.Client;

using NLightning.Client;
using NLightning.Client.Handlers;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;

public sealed class WalletHistoryCommandTests
{
    [Fact]
    public void Given_ExplicitPartialBounds_When_Parsed_Then_HistoryOptionsArePreserved()
    {
        // Arrange / Act
        var request = WalletHistoryCommands.Parse(["--from-height", "10", "--to-height", "100", "--allow-partial", "--address-count", "40"], out var error);
        // Assert
        Assert.Null(error); Assert.Equal(10u, request!.FromHeight); Assert.Equal(100u, request.ToHeight);
        Assert.True(request.AllowPartial); Assert.Equal(40u, request.AddressCount);
        Assert.Null(ClientApp.ValidateArguments("wallethistory", []));
        Assert.Null(ClientApp.ValidateArguments("wallet-history", ["--cancel"]));
    }

    [Theory]
    [InlineData("--allow-partial")]
    [InlineData("--from-height", "-1")]
    [InlineData("--from-height", "20", "--to-height", "10")]
    [InlineData("--cancel", "--from-height", "10")]
    [InlineData("--from-height", "1", "--address-count", "0")]
    [InlineData("--from-height", "1", "--from-height", "2")]
    public void Given_AmbiguousOrUnboundedOptions_When_Parsed_Then_RequestIsRefused(params string[] args)
    {
        // Arrange / Act / Assert
        Assert.NotNull(ClientApp.ValidateArguments("wallethistory", args));
    }

    [Fact]
    public void Given_HistoryBounds_When_WireRoundTripped_Then_AllOptionsArePreserved()
    {
        // Arrange
        var request = new WalletHistoryIpcRequest
        {
            FromHeight = 10,
            ToHeight = 100,
            AllowPartial = true,
            AddressCount = 40
        };

        // Act
        var bytes = MessagePackSerializer.Serialize(request, NLightningMessagePackOptions.Options,
                                                     TestContext.Current.CancellationToken);
        var restored = MessagePackSerializer.Deserialize<WalletHistoryIpcRequest>(bytes,
            NLightningMessagePackOptions.Options, TestContext.Current.CancellationToken).ToClientRequest();

        // Assert
        Assert.Equal(10u, restored.FromHeight);
        Assert.Equal(100u, restored.ToHeight);
        Assert.True(restored.AllowPartial);
        Assert.Equal(40u, restored.AddressCount);
        Assert.False(restored.Cancel);
    }

    [Fact]
    public void Given_OmittedOptionalWireFields_When_Deserialized_Then_DefaultAddressBoundIsPreserved()
    {
        // Arrange: an array containing only the first three wire keys.
        byte[] bytes = [0x93, 0xc0, 0xc0, 0xc2];

        // Act
        var request = MessagePackSerializer.Deserialize<WalletHistoryIpcRequest>(bytes,
            NLightningMessagePackOptions.Options, TestContext.Current.CancellationToken).ToClientRequest();

        // Assert
        Assert.Null(request.FromHeight);
        Assert.Null(request.ToHeight);
        Assert.False(request.AllowPartial);
        Assert.Equal(30u, request.AddressCount);
        Assert.False(request.Cancel);
    }
}