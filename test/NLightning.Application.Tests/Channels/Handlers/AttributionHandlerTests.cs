using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Application.Channels.Services;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Infrastructure.Crypto.Hashes;
using static NormalOperationTestContext;

/// <summary>
/// <c>attribution_data</c> and <c>fulfillment_payload</c> on the receive handlers and on the wire mapping (NL-326,
/// NL-022, NL-325; BOLT 2 "Removing an HTLC", BOLT 4 attributable failures).
/// </summary>
public class AttributionHandlerTests
{
    private readonly NormalOperationTestContext _context = new();

    [Fact]
    public async Task Given_FulfillWithAttributionAndPayload_When_Handled_Then_BothArePersistedAndCarriedByTheEvent()
    {
        // Arrange
        var htlc = _context.LockIn(HtlcDirection.Outgoing, 50_000_000, SecretOf(1));
        var handler = CreateFulfillHandler();
        var attribution = Bytes(OnionConstants.AttributionDataLength, 0xA1);
        var payload = Bytes(272, 0xB2);
        var message = new UpdateFulfillHtlcMessage(new UpdateFulfillHtlcPayload(TestChannelId, htlc.Id, SecretOf(1)),
                                                   new AttributionDataTlv(attribution),
                                                   new FulfillmentPayloadTlv(payload));

        // Act
        await handler.HandleAsync(message, ChannelState.Open, new FeatureOptions(), PeerNodeId);

        // Assert: the removal as persisted (the transition the repository applied) keeps both TLVs
        var (_, transition, _) = Assert.Single(_context.Applied);
        var removal = Assert.Single(transition.UpsertedHtlcs).Removal!;
        Assert.Equal(attribution, removal.AttributionData.ToArray());
        Assert.Equal(payload, removal.FulfillmentPayload.ToArray());
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(Assert.Single(_context.Events.Drain()));
        Assert.Equal(attribution, fulfilled.AttributionData.ToArray());
        Assert.Equal(payload, fulfilled.FulfillmentPayload.ToArray());
    }

    [Fact]
    public async Task Given_FulfillWithoutTlvs_When_Handled_Then_TheEventCarriesNoAttribution()
    {
        // Arrange
        var htlc = _context.LockIn(HtlcDirection.Outgoing, 50_000_000, SecretOf(1));
        var handler = CreateFulfillHandler();

        // Act
        await handler.HandleAsync(
            new UpdateFulfillHtlcMessage(new UpdateFulfillHtlcPayload(TestChannelId, htlc.Id, SecretOf(1))),
            ChannelState.Open, new FeatureOptions(), PeerNodeId);

        // Assert
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(Assert.Single(_context.Events.Drain()));
        Assert.True(fulfilled.AttributionData.IsEmpty);
        Assert.True(fulfilled.FulfillmentPayload.IsEmpty);
    }

    [Fact]
    public async Task Given_FulfillmentPayloadAbove32KiBWithAValidPreimage_When_Handled_Then_ThePreimageIsKeptAndTheChannelFails()
    {
        // Arrange - BOLT 2: "MUST send an error and fail the channel" (NL-325); the preimage must still be kept and
        // reach the switch, or a forwarded HTLC the downstream peer claims on chain cannot be fulfilled upstream
        var htlc = _context.LockIn(HtlcDirection.Outgoing, 50_000_000, SecretOf(1));
        var handler = CreateFulfillHandler();
        var attribution = Bytes(OnionConstants.AttributionDataLength, 0xA7);
        var message = new UpdateFulfillHtlcMessage(new UpdateFulfillHtlcPayload(TestChannelId, htlc.Id, SecretOf(1)),
                                                   new AttributionDataTlv(attribution),
                                                   new FulfillmentPayloadTlv(
                                                       new byte[OnionConstants.MaxFulfillmentPayloadLength + 1]));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelFailedException>(
                            () => handler.HandleAsync(message, ChannelState.Open, new FeatureOptions(), PeerNodeId));

        // Assert: the channel fails, after one save that kept the preimage (without the oversized payload)
        Assert.Equal(TestChannelId, exception.ChannelId);
        Assert.Contains("32768", exception.PeerMessage);
        Assert.Equal(["apply", "save"], _context.Calls);
        Assert.Equal(SecretOf(1), _context.State.GetHtlc(HtlcDirection.Outgoing, htlc.Id)!.KnownPreimage);
        var (_, transition, _) = Assert.Single(_context.Applied);
        var removal = Assert.Single(transition.UpsertedHtlcs).Removal!;
        Assert.Equal(HtlcRemovalKind.Fulfill, removal.Kind);
        Assert.True(removal.FulfillmentPayload.IsEmpty);
        Assert.Equal(attribution, removal.AttributionData.ToArray());
        // and the switch gets the fulfill (ChannelManager drains the queue even after the handler threw)
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(Assert.Single(_context.Events.Drain()));
        Assert.Equal(SecretOf(1), fulfilled.PaymentPreimage);
        Assert.True(fulfilled.FulfillmentPayload.IsEmpty);
    }

    [Fact]
    public async Task Given_FulfillmentPayloadAbove32KiBWithAWrongPreimage_When_Handled_Then_TheChannelFailsAndNothingIsPersisted()
    {
        // Arrange
        var htlc = _context.LockIn(HtlcDirection.Outgoing, 50_000_000, SecretOf(1));
        var handler = CreateFulfillHandler();
        var message = new UpdateFulfillHtlcMessage(new UpdateFulfillHtlcPayload(TestChannelId, htlc.Id, SecretOf(2)),
                                                   null,
                                                   new FulfillmentPayloadTlv(
                                                       new byte[OnionConstants.MaxFulfillmentPayloadLength + 1]));

        // Act
        var exception = await Assert.ThrowsAsync<ChannelFailedException>(
                            () => handler.HandleAsync(message, ChannelState.Open, new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.Contains("32768", exception.PeerMessage);
        Assert.Empty(_context.Calls);
        Assert.Null(_context.State.GetHtlc(HtlcDirection.Outgoing, htlc.Id)!.KnownPreimage);
        Assert.Empty(_context.Events.Drain());
    }

    [Fact]
    public async Task Given_FulfillmentPayloadOfExactly32KiB_When_Handled_Then_ItIsAccepted()
    {
        // Arrange
        var htlc = _context.LockIn(HtlcDirection.Outgoing, 50_000_000, SecretOf(1));
        var handler = CreateFulfillHandler();
        var message = new UpdateFulfillHtlcMessage(new UpdateFulfillHtlcPayload(TestChannelId, htlc.Id, SecretOf(1)),
                                                   null,
                                                   new FulfillmentPayloadTlv(
                                                       new byte[OnionConstants.MaxFulfillmentPayloadLength]));

        // Act
        await handler.HandleAsync(message, ChannelState.Open, new FeatureOptions(), PeerNodeId);

        // Assert
        Assert.Equal(["apply", "save"], _context.Calls);
    }

    [Fact]
    public async Task Given_FailWithAttribution_When_Handled_Then_TheRemovalKeepsItForTheSwitchAndTheOrigin()
    {
        // Arrange
        var htlc = _context.LockIn(HtlcDirection.Outgoing, 50_000_000, SecretOf(1));
        var handler = new UpdateFailHtlcMessageHandler(NullLogger<UpdateFailHtlcMessageHandler>.Instance,
                                                       _context.CreateTransitions());
        var attribution = Bytes(OnionConstants.AttributionDataLength, 0xC3);

        // Act
        await handler.HandleAsync(
            new UpdateFailHtlcMessage(new UpdateFailHtlcPayload(TestChannelId, htlc.Id, new byte[292]),
                                      new AttributionDataTlv(attribution)),
            ChannelState.Open, new FeatureOptions(), PeerNodeId);

        // Assert: persisted with the removal; OutgoingHtlcFailed (once irrevocable) carries this removal
        var removal = _context.State.GetHtlc(HtlcDirection.Outgoing, htlc.Id)!.Removal!;
        Assert.Equal(HtlcRemovalKind.Fail, removal.Kind);
        Assert.Equal(attribution, removal.AttributionData.ToArray());
        var (_, transition, _) = Assert.Single(_context.Applied);
        Assert.Equal(attribution, Assert.Single(transition.UpsertedHtlcs).Removal!.AttributionData.ToArray());
    }

    [Fact]
    public void Given_OurPendingAttributedRemovals_When_Retransmitted_Then_TheWireMessagesCarryTheTlvs()
    {
        // Arrange: our unsigned fail and fulfill of two locked-in incoming HTLCs, as the reestablish rebuilds them
        var failed = _context.LockIn(HtlcDirection.Incoming, 40_000_000, SecretOf(1));
        var fulfilled = _context.LockIn(HtlcDirection.Incoming, 30_000_000, SecretOf(2));
        var failAttribution = Bytes(OnionConstants.AttributionDataLength, 0xD4);
        var fulfillAttribution = Bytes(OnionConstants.AttributionDataLength, 0xE5);
        var payload = Bytes(272, 0xF6);
        using var sha256 = new Sha256();
        var state = _context.State.SendFail(failed.Id, new byte[292], failAttribution).Next
                            .SendFulfill(fulfilled.Id, SecretOf(2), sha256, fulfillAttribution, payload).Next;
        _context.SetState(state);
        var transitions = _context.CreateTransitions();

        // Act
        var messages = ChannelStateTransitionService.PendingLocalUpdates(state)
                                                    .Select(u => transitions.ToWireMessage(_context.Channel, u))
                                                    .ToList();

        // Assert
        var fail = Assert.Single(messages.OfType<UpdateFailHtlcMessage>());
        Assert.Equal(failAttribution, fail.AttributionDataTlv!.AttributionData);
        var fulfill = Assert.Single(messages.OfType<UpdateFulfillHtlcMessage>());
        Assert.Equal(fulfillAttribution, fulfill.AttributionDataTlv!.AttributionData);
        Assert.Equal(payload, fulfill.FulfillmentPayloadTlv!.FulfillmentPayload);
    }

    [Fact]
    public void Given_OurPendingRemovalsWithoutAttribution_When_Retransmitted_Then_TheyCarryNoTlv()
    {
        // Arrange
        var failed = _context.LockIn(HtlcDirection.Incoming, 40_000_000, SecretOf(1));
        var state = _context.State.SendFail(failed.Id, new byte[292]).Next;
        _context.SetState(state);
        var transitions = _context.CreateTransitions();

        // Act
        var message = transitions.ToWireMessage(_context.Channel,
                                                Assert.Single(ChannelStateTransitionService.PendingLocalUpdates(state)));

        // Assert
        var fail = Assert.IsType<UpdateFailHtlcMessage>(message);
        Assert.Null(fail.AttributionDataTlv);
        Assert.Null(fail.Extension);
    }

    private UpdateFulfillHtlcMessageHandler CreateFulfillHandler() =>
        new(NullLogger<UpdateFulfillHtlcMessageHandler>.Instance, _context.CreateTransitions());

    private static byte[] Bytes(int length, byte tag) => Enumerable.Repeat(tag, length).ToArray();
}