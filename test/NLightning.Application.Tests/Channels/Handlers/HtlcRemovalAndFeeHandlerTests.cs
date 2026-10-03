using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using static NormalOperationTestContext;

/// <summary>
/// The receive handlers for update_fulfill_htlc, update_fail_htlc, update_fail_malformed_htlc and update_fee (N6-T1).
/// </summary>
public class HtlcRemovalAndFeeHandlerTests
{
    private readonly NormalOperationTestContext _context = new();

    [Fact]
    public async Task Given_LockedInOutgoingHtlc_When_FulfillWithThePreimage_Then_PersistedAndFulfilledEventQueued()
    {
        // Arrange
        var htlc = _context.LockIn(HtlcDirection.Outgoing, 50_000_000, SecretOf(1));
        var handler = new UpdateFulfillHtlcMessageHandler(NullLogger<UpdateFulfillHtlcMessageHandler>.Instance,
                                                          _context.CreateTransitions());

        // Act
        var replies = await handler.HandleAsync(
                          new UpdateFulfillHtlcMessage(new UpdateFulfillHtlcPayload(TestChannelId, htlc.Id, SecretOf(1))),
                          ChannelState.Open, new FeatureOptions(), PeerNodeId);

        // Assert - I10: the preimage is saved before anything uses it; B2-FWD-05: the event is raised at once
        Assert.Empty(replies);
        Assert.Equal(["apply", "save"], _context.Calls);
        Assert.Equal(SecretOf(1), _context.State.GetHtlc(HtlcDirection.Outgoing, htlc.Id)!.KnownPreimage);
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(Assert.Single(_context.Events.Drain()));
        Assert.Equal(SecretOf(1), fulfilled.PaymentPreimage);
    }

    [Fact]
    public async Task Given_WrongPreimage_When_Fulfill_Then_WarningAndCloseWithoutPersisting()
    {
        // Arrange - B2-DEL-R02
        var htlc = _context.LockIn(HtlcDirection.Outgoing, 50_000_000, SecretOf(1));
        var handler = new UpdateFulfillHtlcMessageHandler(NullLogger<UpdateFulfillHtlcMessageHandler>.Instance,
                                                          _context.CreateTransitions());

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(
                                new UpdateFulfillHtlcMessage(new UpdateFulfillHtlcPayload(TestChannelId, htlc.Id,
                                                                                          SecretOf(2))),
                                ChannelState.Open, new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("B2-DEL-R02", exception.Message);
        Assert.Empty(_context.Calls);
    }

    [Fact]
    public async Task Given_LockedInOutgoingHtlc_When_Fail_Then_PersistedAndNoEventUntilIrrevocable()
    {
        // Arrange
        var htlc = _context.LockIn(HtlcDirection.Outgoing, 50_000_000, SecretOf(1));
        var handler = new UpdateFailHtlcMessageHandler(NullLogger<UpdateFailHtlcMessageHandler>.Instance,
                                                       _context.CreateTransitions());

        // Act
        var replies = await handler.HandleAsync(
                          new UpdateFailHtlcMessage(new UpdateFailHtlcPayload(TestChannelId, htlc.Id, new byte[292])),
                          ChannelState.Open, new FeatureOptions(), PeerNodeId);

        // Assert - B2-FWD-02: nothing propagates before the removal is irrevocably committed
        Assert.Empty(replies);
        Assert.Equal(["apply", "save"], _context.Calls);
        Assert.Equal(HtlcState.RcvdRemoveHtlc, _context.State.GetHtlc(HtlcDirection.Outgoing, htlc.Id)!.State);
        Assert.Empty(_context.Events.Drain());
    }

    [Fact]
    public async Task Given_UnknownHtlc_When_Fail_Then_WarningAndClose()
    {
        // Arrange - B2-DEL-R01
        var handler = new UpdateFailHtlcMessageHandler(NullLogger<UpdateFailHtlcMessageHandler>.Instance,
                                                       _context.CreateTransitions());

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(
                                new UpdateFailHtlcMessage(new UpdateFailHtlcPayload(TestChannelId, 7, new byte[292])),
                                ChannelState.Open, new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("B2-DEL-R01", exception.Message);
    }

    [Theory]
    [InlineData((ushort)0x0001)]
    [InlineData((ushort)0x4005)]
    public async Task Given_FailureCodeWithoutBadOnion_When_FailMalformed_Then_WarningAndCloseBeforeAnyStateCheck(
        ushort failureCode)
    {
        // Arrange - B2-DEL-R04 (NL-023), even for an HTLC we don't know
        var handler = new UpdateFailMalformedHtlcMessageHandler(
            NullLogger<UpdateFailMalformedHtlcMessageHandler>.Instance, _context.CreateTransitions());

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(CreateMalformed(9, failureCode), ChannelState.Open,
                                                      new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("BADONION", exception.PeerMessage);
        Assert.Empty(_context.Calls);
    }

    [Fact]
    public async Task Given_LockedInOutgoingHtlc_When_FailMalformedWithBadOnion_Then_Persisted()
    {
        // Arrange
        var htlc = _context.LockIn(HtlcDirection.Outgoing, 50_000_000, SecretOf(1));
        var handler = new UpdateFailMalformedHtlcMessageHandler(
            NullLogger<UpdateFailMalformedHtlcMessageHandler>.Instance, _context.CreateTransitions());

        // Act
        await handler.HandleAsync(CreateMalformed(htlc.Id, 0xC005), ChannelState.Open, new FeatureOptions(),
                                  PeerNodeId);

        // Assert
        Assert.Equal(["apply", "save"], _context.Calls);
        var removal = _context.State.GetHtlc(HtlcDirection.Outgoing, htlc.Id)!.Removal!;
        Assert.Equal(HtlcRemovalKind.FailMalformed, removal.Kind);
        Assert.Equal((ushort)0xC005, removal.FailureCode);
    }

    [Fact]
    public async Task Given_PeerFundedChannel_When_UpdateFee_Then_Persisted()
    {
        // Arrange
        var context = new NormalOperationTestContext(localIsFunder: false);
        var handler = new UpdateFeeMessageHandler(NullLogger<UpdateFeeMessageHandler>.Instance,
                                                  context.CreateTransitions());

        // Act
        var replies = await handler.HandleAsync(new UpdateFeeMessage(new UpdateFeePayload(TestChannelId, 5_000)),
                                                ChannelState.Open, new FeatureOptions(), PeerNodeId);

        // Assert
        Assert.Empty(replies);
        Assert.Equal(["apply", "save"], context.Calls);
        Assert.Equal(5_000U, context.State.LatestFeeratePerKw);
        Assert.True(Assert.Single(context.Applied).Transition.FeeUpdatesChanged);
    }

    [Fact]
    public async Task Given_WeAreTheFunder_When_UpdateFee_Then_WarningAndClose()
    {
        // Arrange - B2-FEE-R02
        var handler = new UpdateFeeMessageHandler(NullLogger<UpdateFeeMessageHandler>.Instance,
                                                  _context.CreateTransitions());

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(new UpdateFeeMessage(new UpdateFeePayload(TestChannelId, 5_000)),
                                                      ChannelState.Open, new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("B2-FEE-R02", exception.Message);
        Assert.Empty(_context.Calls);
    }

    [Theory]
    [InlineData(100U)]
    [InlineData(1_000_000U)]
    public async Task Given_UnreasonableFeerate_When_UpdateFee_Then_WarningAndClose(uint feeratePerKw)
    {
        // Arrange - B2-FEE-R01
        var context = new NormalOperationTestContext(localIsFunder: false);
        var handler = new UpdateFeeMessageHandler(NullLogger<UpdateFeeMessageHandler>.Instance,
                                                  context.CreateTransitions());

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(
                                new UpdateFeeMessage(new UpdateFeePayload(TestChannelId, feeratePerKw)),
                                ChannelState.Open, new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.Contains("B2-FEE-R01", exception.Message);
        Assert.Empty(context.Calls);
    }

    private static UpdateFailMalformedHtlcMessage CreateMalformed(ulong id, ushort failureCode) =>
        new(new UpdateFailMalformedHtlcPayload(TestChannelId, failureCode, id, new byte[32]));
}