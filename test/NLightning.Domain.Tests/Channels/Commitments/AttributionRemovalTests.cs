namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using static CommitmentsTestKit;

/// <summary>
/// The engine keeps a removal's <c>attribution_data</c> and <c>fulfillment_payload</c> (NL-326) opaque: stored with the
/// removal, sent with our own and carried by the fulfill event, live and replayed.
/// </summary>
public class AttributionRemovalTests
{
    private static readonly byte[] s_attribution = Enumerable.Repeat((byte)0xA7, 920).ToArray();
    private static readonly byte[] s_payload = Enumerable.Repeat((byte)0x5C, 272).ToArray();

    /// <summary>Alice offered HTLC 0 (preimage 1) and it is locked in on both sides.</summary>
    private static CommitmentPair LockedInPair()
    {
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        pair.AliceFullRound();
        return pair;
    }

    [Fact]
    public void Given_AttributedFulfill_When_Sent_Then_TheRemovalAndTheOutboundCarryIt()
    {
        // Arrange
        var pair = LockedInPair();

        // Act
        var result = pair.Bob.SendFulfill(0, Preimage(1), Sha256, s_attribution, s_payload);

        // Assert
        var outbound = Assert.IsType<OutboundFulfillHtlc>(Assert.Single(result.Outbound));
        Assert.Equal(s_attribution, outbound.AttributionData.ToArray());
        Assert.Equal(s_payload, outbound.FulfillmentPayload.ToArray());
        var removal = result.Next.GetHtlc(HtlcDirection.Incoming, 0)!.Removal!;
        Assert.Equal(s_attribution, removal.AttributionData.ToArray());
        Assert.Equal(s_payload, removal.FulfillmentPayload.ToArray());
    }

    [Fact]
    public void Given_AttributedFail_When_Sent_Then_TheRemovalAndTheOutboundCarryIt()
    {
        // Arrange
        var pair = LockedInPair();

        // Act
        var result = pair.Bob.SendFail(0, new byte[] { 1, 2, 3 }, s_attribution);

        // Assert
        var outbound = Assert.IsType<OutboundFailHtlc>(Assert.Single(result.Outbound));
        Assert.Equal(s_attribution, outbound.AttributionData.ToArray());
        Assert.Equal(s_attribution,
                     result.Next.GetHtlc(HtlcDirection.Incoming, 0)!.Removal!.AttributionData.ToArray());
    }

    [Fact]
    public void Given_AttributedFulfillReceived_When_Applied_Then_TheEventCarriesItAndTheReplayDerivesItAgain()
    {
        // Arrange
        var pair = LockedInPair();

        // Act
        var result = pair.Alice.ReceiveFulfill(0, Preimage(1), Sha256, s_attribution, s_payload);

        // Assert: live
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(Assert.Single(result.Events));
        Assert.Equal(s_attribution, fulfilled.AttributionData.ToArray());
        Assert.Equal(s_payload, fulfilled.FulfillmentPayload.ToArray());

        // Assert: replayed from the persisted record (startup, link-up)
        var replayed = Assert.IsType<OutgoingHtlcFulfilled>(
            Assert.Single(ChannelDomainEvents.DerivePending(result.Next)));
        Assert.Equal(s_attribution, replayed.AttributionData.ToArray());
        Assert.Equal(s_payload, replayed.FulfillmentPayload.ToArray());
    }

    [Fact]
    public void Given_AttributedFulfillRevertedByDisconnect_When_Replayed_Then_OnlyThePreimageRemains()
    {
        // Arrange - BOLT 2: an unsigned fulfill is forgotten on reconnection, its preimage is not
        var pair = LockedInPair();
        var received = pair.Alice.ReceiveFulfill(0, Preimage(1), Sha256, s_attribution, s_payload).Next;

        // Act
        var reverted = received.RevertUncommitted().Next;

        // Assert
        var replayed = Assert.IsType<OutgoingHtlcFulfilled>(Assert.Single(ChannelDomainEvents.DerivePending(reverted)));
        Assert.Equal(Preimage(1), replayed.PaymentPreimage);
        Assert.True(replayed.AttributionData.IsEmpty);
    }

    [Fact]
    public void Given_AttributedFailReceived_When_Irrevocable_Then_OutgoingHtlcFailedCarriesTheAttribution()
    {
        // Arrange - Bob fails Alice's HTLC 0 with attribution_data and the removal is committed both ways
        var pair = LockedInPair();
        pair.BobFail(0, s_attribution);

        // Act
        pair.BobFullRound();

        // Assert
        var failed = Assert.Single(pair.AliceEvents.Select(e => e.Event).OfType<OutgoingHtlcFailed>());
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        Assert.Equal(s_attribution, failed.Removal.AttributionData.ToArray());
    }

    [Fact]
    public void Given_NoAttribution_When_Removed_Then_EverythingIsEmptyAsBefore()
    {
        // Arrange
        var pair = LockedInPair();

        // Act
        var result = pair.Alice.ReceiveFulfill(0, Preimage(1), Sha256);

        // Assert
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(Assert.Single(result.Events));
        Assert.Equal(new OutgoingHtlcFulfilled(ChannelId, 0, PaymentHash(1), Preimage(1)), fulfilled);
    }
}