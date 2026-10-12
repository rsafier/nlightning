namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using static CommitmentsTestKit;

/// <summary>
/// Engine domain events (plan N4-T4, invariant I8, matrix B2-NO-03, B2-FWD-01/02/05): <see cref="IncomingHtlcLockedIn"/>
/// once both sides revoked, <see cref="OutgoingHtlcFulfilled"/> as soon as the preimage arrives,
/// <see cref="OutgoingHtlcFailed"/> only when the removal is irrevocable, <see cref="OutgoingHtlcSettled"/> on pruning,
/// and <see cref="ChannelDomainEvents.DerivePending(ChannelCommitments, IEnumerable{HtlcRecord})"/> for the replay.
/// </summary>
public class CommitmentsEventsTests
{
    [Fact]
    public void Given_IncomingAdd_When_BothRevoked_Then_IncomingHtlcLockedInOnce()
    {
        // Arrange
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        pair.DeliverBobRevoke(pair.AliceCommits());
        var aliceRevoke = pair.BobCommits();
        Assert.Empty(pair.BobEvents); // in both commitments, but Alice has not revoked her previous one yet

        // Act
        pair.DeliverAliceRevoke(aliceRevoke);

        // Assert
        var (step, domainEvent) = Assert.Single(pair.BobEvents);
        Assert.Equal("receive revoke", step);
        var lockedIn = Assert.IsType<IncomingHtlcLockedIn>(domainEvent);
        Assert.Equal(ChannelId, lockedIn.ChannelId);
        Assert.Equal(0UL, lockedIn.HtlcId);
        Assert.Equal(HtlcState.RcvdAddAckRevocation, lockedIn.Htlc.State);
        Assert.Equal(PaymentHash(1), lockedIn.Htlc.PaymentHash);
        Assert.Equal(10_000 * Sat, lockedIn.Htlc.AmountMsat);
        Assert.Equal(Onion.Length, lockedIn.Htlc.OnionRoutingPacket.Length);
        Assert.Empty(pair.AliceEvents); // an outgoing add raises nothing

        // More rounds (a fee update, another HTLC, the fulfill) never raise it again
        pair.AliceFee(2_000);
        pair.AliceAdd(20_000 * Sat, preimageTag: 3);
        pair.Converge();
        pair.BobFulfill(0);
        pair.Converge();
        Assert.Single(pair.BobEvents, e => e.Event is IncomingHtlcLockedIn { HtlcId: 0 });
        Assert.Single(pair.BobEvents, e => e.Event is IncomingHtlcLockedIn { HtlcId: 1 });
        Assert.Single(pair.BobEvents, e => e.Event is IncomingHtlcSettled
        {
            HtlcId: 0, Kind: HtlcRemovalKind.Fulfill
        });
        Assert.Equal(3, pair.BobEvents.Count);
    }

    [Fact]
    public void Given_DownstreamFail_Then_OutgoingHtlcFailedOnlyWhenIrrevocable()
    {
        // Arrange - Alice offered HTLC 0, locked in on both sides; Bob fails it
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        pair.AliceFullRound();
        pair.BobFail(0);

        // Act / Assert - nothing while the removal is only pending or partly committed
        Assert.Empty(pair.AliceEvents); // 15: received the fail
        var bobRevoke = pair.BobCommits();
        Assert.Empty(pair.AliceEvents); // 16/17: our commitment lost it, we revoked
        pair.DeliverAliceRevoke(bobRevoke);
        var aliceRevoke = pair.AliceCommits();
        Assert.Empty(pair.AliceEvents); // 18: signed the peer's commitment without it, not yet revoked
        // Bob saw the lock-in, then (on the revoke that made his removal final) the incoming settle, nothing else
        Assert.Collection(pair.BobEvents,
                          e => Assert.IsType<IncomingHtlcLockedIn>(e.Event),
                          e =>
                          {
                              var settled = Assert.IsType<IncomingHtlcSettled>(e.Event);
                              Assert.Equal(0UL, settled.HtlcId);
                              Assert.Equal(HtlcRemovalKind.Fail, settled.Kind);
                          });

        pair.DeliverBobRevoke(aliceRevoke); // 19: irrevocable

        // Assert
        Assert.Collection(pair.AliceEvents,
                          e =>
                          {
                              Assert.Equal("receive revoke", e.Step);
                              var failed = Assert.IsType<OutgoingHtlcFailed>(e.Event);
                              Assert.Equal(0UL, failed.HtlcId);
                              Assert.Equal(PaymentHash(1), failed.PaymentHash);
                              Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
                              Assert.Equal(new byte[] { 1, 2, 3 }, failed.Removal.Reason.ToArray());
                          },
                          e =>
                          {
                              Assert.Equal("receive revoke", e.Step);
                              var settled = Assert.IsType<OutgoingHtlcSettled>(e.Event);
                              Assert.Equal(0UL, settled.HtlcId);
                              Assert.Equal(HtlcRemovalKind.Fail, settled.Kind);
                          });
        Assert.Empty(pair.Alice.Htlcs);
    }

    [Fact]
    public void Given_DownstreamFailRevertedByDisconnect_When_Reconnected_Then_NoOutgoingHtlcFailed()
    {
        // Arrange - Bob's fail reaches Alice but no commitment_signed covers it
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        pair.AliceFullRound();
        pair.BobFail(0);

        // Act
        pair.Disconnect();

        // Assert
        Assert.Empty(pair.AliceEvents);
        Assert.Equal(HtlcState.SentAddAckRevocation, pair.Alice.GetHtlc(HtlcDirection.Outgoing, 0)!.State);
        Assert.Empty(ChannelDomainEvents.DerivePending(pair.Alice));
    }

    [Fact]
    public void Given_DownstreamFulfill_When_Received_Then_OutgoingHtlcFulfilledImmediatelyAndSettledLater()
    {
        // Arrange
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        pair.AliceFullRound();

        // Act
        pair.BobFulfill(0);

        // Assert - raised on update_fulfill_htlc itself, before any commitment covers it (B2-FWD-05)
        var (step, domainEvent) = Assert.Single(pair.AliceEvents);
        Assert.Equal("receive fulfill", step);
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(domainEvent);
        Assert.Equal(0UL, fulfilled.HtlcId);
        Assert.Equal(Preimage(1), fulfilled.PaymentPreimage);
        Assert.Equal(PaymentHash(1), fulfilled.PaymentHash);

        // Once the removal is irrevocable: settled, never failed
        pair.Converge();
        Assert.Equal(2, pair.AliceEvents.Count);
        var settled = Assert.IsType<OutgoingHtlcSettled>(pair.AliceEvents[1].Event);
        Assert.Equal("receive revoke", pair.AliceEvents[1].Step);
        Assert.Equal(HtlcRemovalKind.Fulfill, settled.Kind);
        Assert.DoesNotContain(pair.AliceEvents, e => e.Event is OutgoingHtlcFailed);
    }

    [Fact]
    public void Given_FulfillRevertedByDisconnect_When_ResentAfterReconnect_Then_OutgoingHtlcFulfilledRaisedOnce()
    {
        // Arrange
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat);
        pair.AliceFullRound();
        pair.BobFulfill(0);
        pair.Disconnect();

        // Act - Bob re-sends the fulfill (it stayed in his state 35) and Alice receives it again
        var result = pair.Alice.ReceiveFulfill(0, Preimage(1), Sha256);

        // Assert
        Assert.Empty(result.Events);
        Assert.Single(pair.AliceEvents, e => e.Event is OutgoingHtlcFulfilled);
        Assert.Equal(Preimage(1), pair.Alice.GetHtlc(HtlcDirection.Outgoing, 0)!.KnownPreimage);
    }

    [Fact]
    public void Given_RestoredSnapshot_When_DerivePending_Then_LockedInAndFulfilledAreReplayed()
    {
        // Arrange - Alice: her HTLC 0 fulfilled by Bob (preimage known, not yet committed), her HTLC 1 still open,
        // Bob's HTLC 0 locked in and not yet resolved
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat, preimageTag: 1);
        pair.AliceAdd(20_000 * Sat, preimageTag: 3);
        pair.BobAdd(30_000 * Sat, preimageTag: 2);
        pair.Converge();
        pair.BobFulfill(0);
        var alice = pair.Alice;
        var restored = ChannelCommitments.Restore(alice.ChannelId, alice.Params, alice.LocalBalanceMsat,
                                                  alice.RemoteBalanceMsat, alice.Htlcs.Values, alice.FeeUpdates,
                                                  alice.LocalNextHtlcId, alice.RemoteNextHtlcId, alice.LocalCommit,
                                                  alice.RemoteCommit, alice.RemoteNextCommit,
                                                  alice.RemoteNextPerCommitmentPoint);

        // Act
        var pending = ChannelDomainEvents.DerivePending(restored);

        // Assert - exactly what the live engine raised and nobody has acted on yet
        Assert.Collection(pending,
                          e => Assert.Equal(0UL, Assert.IsType<IncomingHtlcLockedIn>(e).HtlcId),
                          e =>
                          {
                              var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(e);
                              Assert.Equal(0UL, fulfilled.HtlcId);
                              Assert.Equal(Preimage(1), fulfilled.PaymentPreimage);
                          });
        Assert.All(pending, e => Assert.Equal(ChannelId, e.ChannelId));
        var raised = pair.AliceEvents.Select(e => (e.Event.GetType(), e.Event.HtlcId)).ToHashSet();
        Assert.All(pending, e => Assert.Contains((e.GetType(), e.HtlcId), raised));
    }

    [Fact]
    public void Given_SettledRecordsNotPruned_When_DerivePending_Then_FailedAndSettledAreReplayed()
    {
        // Arrange - Alice's HTLC 0 failed irrevocably, HTLC 1 fulfilled irrevocably; the settled records come from the
        // transition of the revoke_and_ack that settled them (a crash before the events were delivered)
        var pair = new CommitmentPair(600_000, 400_000);
        pair.AliceAdd(10_000 * Sat, preimageTag: 1);
        pair.AliceAdd(20_000 * Sat, preimageTag: 3);
        pair.Converge();
        pair.BobFail(0);
        pair.BobFulfill(1, preimageTag: 3);
        pair.DeliverAliceRevoke(pair.BobCommits());
        var aliceRevoke = pair.AliceCommits();
        var result = pair.Alice.ReceiveRevoke(SecretFor(BobTag, aliceRevoke.RevokedCommitmentNumber),
                                              Point(BobTag, aliceRevoke.NextCommitmentNumber),
                                              new FakeRevocationVerifier());

        // Act
        var pending = ChannelDomainEvents.DerivePending(result.Next, result.Transition.SettledHtlcs);
        var afterPruning = ChannelDomainEvents.DerivePending(result.Next);

        // Assert
        Assert.Collection(pending,
                          e => Assert.Equal(0UL, Assert.IsType<OutgoingHtlcFailed>(e).HtlcId),
                          e => Assert.Equal(0UL, Assert.IsType<OutgoingHtlcSettled>(e).HtlcId),
                          e => Assert.Equal(1UL, Assert.IsType<OutgoingHtlcFulfilled>(e).HtlcId),
                          e => Assert.Equal(1UL, Assert.IsType<OutgoingHtlcSettled>(e).HtlcId));
        Assert.Collection(result.Events,
                          e => Assert.Equal(0UL, Assert.IsType<OutgoingHtlcFailed>(e).HtlcId),
                          e => Assert.Equal(0UL, Assert.IsType<OutgoingHtlcSettled>(e).HtlcId),
                          e => Assert.Equal(1UL, Assert.IsType<OutgoingHtlcSettled>(e).HtlcId));
        Assert.Empty(afterPruning);
    }

    [Fact]
    public void Given_IncomingRemovalsFinalNotPruned_When_DerivePending_Then_IncomingSettledIsReplayed()
    {
        // Arrange - Bob fulfilled HTLC 0 and failed HTLC 1 and both removals are final (NL-243: archived rows to prune)
        var fulfilled = new HtlcRecord(HtlcDirection.Incoming, 0, 1_000, PaymentHash(1), 600,
                                       HtlcState.SentRemoveAckRevocation,
                                       HtlcRemoval.Fulfill(new Secret(new byte[32])));
        var failed = new HtlcRecord(HtlcDirection.Incoming, 1, 1_000, PaymentHash(2), 600,
                                    HtlcState.SentRemoveAckRevocation, HtlcRemoval.Fail(new byte[] { 1 }));
        var removing = new HtlcRecord(HtlcDirection.Incoming, 2, 1_000, PaymentHash(3), 600,
                                      HtlcState.SentRemoveCommit, HtlcRemoval.Fail(new byte[] { 1 }));

        // Act
        var pending = ChannelDomainEvents.DerivePending(ChannelId, [failed, removing, fulfilled]);

        // Assert - one event per final record, none for a removal that is not final yet
        Assert.Collection(pending,
                          e => Assert.Equal(new IncomingHtlcSettled(ChannelId, 0, PaymentHash(1),
                                                                    HtlcRemovalKind.Fulfill), e),
                          e => Assert.Equal(new IncomingHtlcSettled(ChannelId, 1, PaymentHash(2),
                                                                    HtlcRemovalKind.Fail), e));
    }

    [Fact]
    public void Given_LegacyHtlcState_When_DerivePending_Then_Throws()
    {
        // Arrange
        var legacy = new HtlcRecord(HtlcDirection.Incoming, 0, 1_000, PaymentHash(1), 600, HtlcState.Offered);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => ChannelDomainEvents.DerivePending(ChannelId, [legacy]));
    }

    [Fact]
    public void Given_IncomingHtlcBeforeLockIn_When_DerivePending_Then_NothingPending()
    {
        // Arrange - every incoming add state before lock-in, and the removal states after it
        HtlcState[] states =
        [
            HtlcState.RcvdAddHtlc, HtlcState.RcvdAddCommit, HtlcState.SentAddRevocation, HtlcState.SentAddAckCommit
        ];
        var records = states.Select((s, i) => new HtlcRecord(HtlcDirection.Incoming, (ulong)i, 1_000, PaymentHash(1),
                                                             600, s));

        // Act
        var pending = ChannelDomainEvents.DerivePending(ChannelId, records);

        // Assert
        Assert.Empty(pending);
    }
}