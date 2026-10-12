using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Client;
using NLightning.Client.Printers;

namespace NLightning.Daemon.Tests.Handlers;

using Daemon.Handlers;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.KeyIndexes;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>NL-1392: <c>nltg openhold [--release]</c> shows and lifts the hold on channel opens.</summary>
public sealed class OpenHoldClientHandlerTests
{
    [Fact]
    public async Task Given_TheStartHold_When_Released_Then_OpensAreAllowedAgain()
    {
        // Arrange
        var gate = new ChannelKeyIndexGate();
        gate.HoldUntilRestore();
        var handler = new OpenHoldClientHandler(gate, NullLogger<OpenHoldClientHandler>.Instance);

        // Act
        var shown = await handler.HandleAsync(new OpenHoldClientRequest(false), TestContext.Current.CancellationToken);
        var released = await handler.HandleAsync(new OpenHoldClientRequest(true), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(shown.Held);
        Assert.True(shown.AwaitingRestore);
        Assert.Equal(ChannelKeyIndexHold.AwaitingRestoreReason, shown.Reason);
        Assert.False(shown.Released);
        Assert.True(released.Released);
        Assert.False(released.Held);
        Assert.Null(released.Reason);
        Assert.Equal(1u, gate.Allocate(() => 1));
    }

    [Fact]
    public async Task Given_ARestoreInProgress_When_Released_Then_TheGateStaysHeld()
    {
        // Arrange
        var gate = new ChannelKeyIndexGate();
        using var restore = await gate.BeginRestoreAsync(TestContext.Current.CancellationToken);
        var handler = new OpenHoldClientHandler(gate, NullLogger<OpenHoldClientHandler>.Instance);

        // Act
        var response = await handler.HandleAsync(new OpenHoldClientRequest(true),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.False(response.Released);
        Assert.True(response.Held);
        Assert.True(response.RestoreInProgress);
        Assert.Equal(ChannelKeyIndexHold.RestoreInProgressReason, response.Reason);
    }

    [Fact]
    public async Task Given_TheStartHold_When_OpenChannel_Then_InvalidOperationBeforeAnyPeerIsContacted()
    {
        // Arrange - our openchannel (and LND gRPC's OpenChannel, which goes through it) while opens are held
        var gate = new ChannelKeyIndexGate();
        gate.HoldUntilRestore();
        var peerManager = new Mock<IPeerManager>();
        var handler = new OpenChannelClientHandler(new Mock<IBlockchainMonitor>().Object,
                                                   new Mock<IChannelFactory>().Object,
                                                   new Mock<IChannelManager>().Object,
                                                   new Mock<IChannelMemoryRepository>().Object,
                                                   new Mock<ILogger<OpenChannelClientHandler>>().Object,
                                                   new Mock<IMessageFactory>().Object, peerManager.Object,
                                                   new Mock<IUtxoMemoryRepository>().Object, keyIndexGate: gate);
        var request = new OpenChannelClientRequest(
            "02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619@127.0.0.1:9735",
            LightningMoney.Satoshis(100_000));

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => handler.HandleAsync(request, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        Assert.Contains("NL-1392", exception.Message);
        Assert.Contains("restorechanbackup", exception.Message);
        peerManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void Given_TheIpcContracts_When_RoundTripped_Then_EveryFieldSurvives()
    {
        // Arrange
        var options = NLightningMessagePackOptions.Options;
        var request = new OpenHoldIpcRequest { Release = true };
        var response = new OpenHoldIpcResponse
        {
            Held = true,
            Reason = "why",
            AwaitingRestore = true,
            RestoreInProgress = true,
            Released = true
        };

        // Act
        var ct = TestContext.Current.CancellationToken;
        var request2 = MessagePackSerializer.Deserialize<OpenHoldIpcRequest>(
            MessagePackSerializer.Serialize(request, options, ct), options, ct);
        var response2 = MessagePackSerializer.Deserialize<OpenHoldIpcResponse>(
            MessagePackSerializer.Serialize(response, options, ct), options, ct);

        // Assert
        Assert.True(request2.ToClientRequest().Release);
        Assert.Equal(("why", true, true, true, true),
                     (response2.Reason, response2.Held, response2.AwaitingRestore, response2.RestoreInProgress,
                      response2.Released));
    }

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--release" }, true)]
    [InlineData(new[] { "--force" }, false)]
    [InlineData(new[] { "--release", "x" }, false)]
    public void Given_OpenHoldArguments_When_Validated_Then_OnlyReleaseIsAccepted(string[] args, bool valid)
    {
        // Act
        var error = ClientApp.ValidateArguments("openhold", args);

        // Assert
        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void Given_AHeldGate_When_Printed_Then_TheReasonAndTheWayOutAreShown()
    {
        // Arrange
        using var output = new StringWriter();

        // Act
        new OpenHoldPrinter(output).Print(new OpenHoldIpcResponse
        {
            Held = true,
            Reason = ChannelKeyIndexHold.AwaitingRestoreReason,
            AwaitingRestore = true
        });

        // Assert
        Assert.Contains("Channel opens are refused", output.ToString());
        Assert.Contains("openhold --release", output.ToString());
    }
}