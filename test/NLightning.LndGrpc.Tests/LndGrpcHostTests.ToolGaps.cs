using Grpc.Core;

namespace NLightning.LndGrpc.Tests;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using LndGrpc.Macaroons;
using Testing.Lnd.Chainrpc;
using Testing.Lnd.Lnrpc;

/// <summary>The gaps bos 24.2.2 and RTL 0.15.13 found against the server (NL-1242..NL-1249).</summary>
public partial class LndGrpcHostTests
{
    [Fact]
    public async Task Given_AProcessedBlock_When_GetInfo_Then_TheBlockHashIsInDisplayOrderAsLndPrintsIt()
    {
        // Arrange: the node keeps block hashes in internal (serialized) order
        var internalOrder = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        _chainState = new BlockchainState(150, new Hash(internalOrder), DateTime.UtcNow);
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var info = await connection.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: Ct);

        // Assert
        Assert.Equal(Convert.ToHexStringLower(internalOrder.Reverse().ToArray()), info.BlockHash);
    }

    [Fact]
    public async Task Given_AClosedChannel_When_ClosedChannels_Then_TheChainHashIsTheGenesisHashInDisplayOrder()
    {
        // Arrange
        _closedChannels.Add(CreateChannel(7, ChannelState.Closed));
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var closed = await connection.LightningClient.ClosedChannelsAsync(new ClosedChannelsRequest(),
                                                                           cancellationToken: Ct);

        // Assert: regtest's genesis block as bitcoind and LND print it
        Assert.Equal("0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206",
                     Assert.Single(closed.Channels).ChainHash);
    }

    [Fact]
    public async Task Given_AMethodTheNodeLacks_When_Called_Then_GrpcGosUnknownMethodText()
    {
        // Arrange
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var declared = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.GetDebugInfoAsync(new GetDebugInfoRequest(), cancellationToken: Ct)
                            .ResponseAsync);

        // Assert
        Assert.Equal(StatusCode.Unimplemented, declared.StatusCode);
        Assert.Equal("unknown method GetDebugInfo for service lnrpc.Lightning", declared.Status.Detail);
    }

    [Fact]
    public async Task Given_AServiceTheNodeLacks_When_Called_Then_GrpcGosUnknownServiceText()
    {
        // Arrange
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(
            () => connection.ChainKitClient.GetBestBlockAsync(new GetBestBlockRequest(), cancellationToken: Ct)
                            .ResponseAsync);

        // Assert
        Assert.Equal(StatusCode.Unimplemented, error.StatusCode);
        Assert.Equal("unknown service chainrpc.ChainKit", error.Status.Detail);
    }

    [Fact]
    public void Given_RegisteredServices_When_PathsAreChecked_Then_OnlyImplementedMethodsPass()
    {
        // Arrange
        var methods = new LndUnknownMethods([typeof(LndGrpc.Services.LightningService)]);

        // Act / Assert
        Assert.Null(methods.GetError("/lnrpc.Lightning/GetInfo"));
        Assert.Equal("unknown method NoSuchMethod for service lnrpc.Lightning",
                     methods.GetError("/lnrpc.Lightning/NoSuchMethod"));
        Assert.Equal("unknown service walletrpc.WalletKit", methods.GetError("/walletrpc.WalletKit/ListSweeps"));
        Assert.Null(methods.GetError("/not-a-grpc-path"));
    }
}