namespace NLightning.Application.Tests.Channels.Harness;

using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;

/// <summary>
/// BOLT2 plan N6-T4: two in-process nodes run the whole commitment dance through their real channel managers,
/// normal-operation handlers, engine ports and signers, and (N6-T2) their real channel operations and commit
/// schedulers: 30 HTLCs each way, then fulfills and fails, then a fee update. Every commitment one side signed was
/// verified by the other with the same txid (invariant I7).
/// </summary>
public class TwoNodeHarnessTests
{
    private const int HtlcsPerSide = 30;
    private const uint CltvExpiry = 700;

    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_TwoNodes_When_ThirtyHtlcsEachWayAreSettledAndTheFeeChanges_Then_TxIdsAgreeAtEveryStep(
        bool hasAnchors)
    {
        // Arrange
        using var harness = new TwoNodeHarness(hasAnchors);
        var alice = harness.Alice;
        var bob = harness.Bob;
        var aliceStart = alice.State.LocalBalanceMsat;

        // Act 1 - Alice offers 30 HTLCs (some trimmed on one or both commitments), then Bob offers 30
        var aliceAmounts = new List<ulong>();
        for (var i = 0; i < HtlcsPerSide; i++)
        {
            var amountMsat = AmountMsat(i);
            aliceAmounts.Add(amountMsat);
            var id = await OfferAsync(alice, amountMsat, TwoNodeHarness.Preimage(i));
            Assert.Equal((ulong)i, id);
        }

        await harness.PumpAsync();
        AssertAgreement(harness);

        var bobAmounts = new List<ulong>();
        for (var i = 0; i < HtlcsPerSide; i++)
        {
            var amountMsat = AmountMsat(i + 7);
            bobAmounts.Add(amountMsat);
            await OfferAsync(bob, amountMsat, TwoNodeHarness.Preimage(1_000 + i));

            // Interleave: Alice's side runs a few messages while Bob keeps adding
            if (i % 10 == 9)
                await harness.PumpAsync();
        }

        await harness.PumpAsync();
        AssertAgreement(harness);

        // Assert 1 - every HTLC locked in on the receiver, once
        Assert.Equal(HtlcsPerSide, bob.Events.OfType<IncomingHtlcLockedIn>().Count());
        Assert.Equal(HtlcsPerSide, alice.Events.OfType<IncomingHtlcLockedIn>().Count());
        Assert.Equal(2 * HtlcsPerSide, alice.State.Htlcs.Count);
        Assert.All(alice.State.Htlcs.Values,
                   h => Assert.True(h.State is HtlcState.SentAddAckRevocation or HtlcState.RcvdAddAckRevocation));

        // Act 2 - each side fulfills the even and fails the odd HTLCs it received, interleaved
        var ct = TestContext.Current.CancellationToken;
        for (ulong id = 0; id < HtlcsPerSide; id++)
        {
            if (id % 2 == 0)
            {
                await bob.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, id,
                                                      TwoNodeHarness.Preimage((int)id), ct);
                await alice.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, id,
                                                        TwoNodeHarness.Preimage(1_000 + (int)id), ct);
            }
            else
            {
                await bob.Operations.FailHtlcAsync(TwoNodeHarness.ChannelId, id, new byte[292], ct);
                await alice.Operations.FailHtlcAsync(TwoNodeHarness.ChannelId, id, new byte[292], ct);
            }

            if (id % 7 == 6)
                await harness.PumpAsync();
        }

        await harness.PumpAsync();
        AssertAgreement(harness);

        // Act 3 - the funder doubles the feerate
        await alice.Operations.UpdateFeeAsync(TwoNodeHarness.ChannelId, TwoNodeHarness.InitialFeeratePerKw * 2, ct);
        await harness.PumpAsync();
        AssertAgreement(harness);

        // Assert 2 - all settled, balances moved by the fulfilled HTLCs only, fee applied on both sides
        Assert.Empty(alice.State.Htlcs);
        Assert.Empty(bob.State.Htlcs);
        var aliceFulfilled = aliceAmounts.Where((_, i) => i % 2 == 0).Aggregate(0UL, (a, b) => a + b);
        var bobFulfilled = bobAmounts.Where((_, i) => i % 2 == 0).Aggregate(0UL, (a, b) => a + b);
        Assert.Equal(aliceStart - aliceFulfilled + bobFulfilled, alice.State.LocalBalanceMsat);
        Assert.Equal(TwoNodeHarness.InitialFeeratePerKw * 2, alice.State.LocalCommit.Spec.FeeratePerKw);
        Assert.Equal(TwoNodeHarness.InitialFeeratePerKw * 2, bob.State.LocalCommit.Spec.FeeratePerKw);
        Assert.Equal(HtlcsPerSide / 2, alice.Events.OfType<OutgoingHtlcFulfilled>().Count());
        Assert.Equal(HtlcsPerSide / 2, alice.Events.OfType<OutgoingHtlcFailed>().Count());
        Assert.Equal(HtlcsPerSide, alice.Events.OfType<OutgoingHtlcSettled>().Count());
        Assert.Equal(HtlcsPerSide, bob.Events.OfType<OutgoingHtlcSettled>().Count());

        // Persisted state is what is in memory, and the peer's shachain was saved as it grew
        Assert.Same(alice.State, alice.Store.Committed);
        Assert.Same(bob.State, bob.Store.Committed);
        Assert.NotEmpty(alice.Store.CommittedShachain);
        Assert.NotEmpty(bob.Store.CommittedShachain);

        // The dance ran through every message type
        Assert.Contains(bob.Received, m => m is UpdateFeeMessage);
        Assert.Contains(alice.Received, m => m is UpdateFulfillHtlcMessage);
        Assert.Contains(alice.Received, m => m is UpdateFailHtlcMessage);
        Assert.True(alice.Signed.Count > 5 && bob.Signed.Count > 5);
    }

    [Fact]
    public async Task Given_AnHtlcAddedByEachSideAtOnce_When_TheMessagesCross_Then_BothConvergeWithTheSameTxIds()
    {
        // Arrange - both sign before seeing the other's add: the commitments cross on the wire
        using var harness = new TwoNodeHarness();

        // Act
        await OfferAsync(harness.Alice, 50_000_000, TwoNodeHarness.Preimage(1));
        await OfferAsync(harness.Bob, 40_000_000, TwoNodeHarness.Preimage(2));
        await harness.Alice.Scheduler.WhenIdleAsync();
        await harness.Bob.Scheduler.WhenIdleAsync();
        await harness.PumpAsync();

        // Assert
        AssertAgreement(harness);
        Assert.Single(harness.Alice.Events.OfType<IncomingHtlcLockedIn>());
        Assert.Single(harness.Bob.Events.OfType<IncomingHtlcLockedIn>());
    }

    [Fact]
    public async Task Given_LocalOnlySwitches_When_AnHtlcIsOffered_Then_ItIsFailedBackAndBothCommitmentNumbersReachTwo()
    {
        // Arrange - the N6-T5 flow in process: Bob can neither receive nor forward, so he fails the HTLC back with
        // incorrect_or_unknown_payment_details, encrypted with the onion's shared secret
        using var harness = new TwoNodeHarness(localOnlySwitch: true);
        var alice = harness.Alice;
        var bob = harness.Bob;
        const ulong amountMsat = 25_000_000;

        // Act
        var id = await OfferAsync(alice, amountMsat, TwoNodeHarness.Preimage(7));
        await harness.PumpAsync();

        // Assert - Alice learnt the irrevocable failure with Bob's reason; nothing is left and balances are back
        var failed = Assert.Single(alice.Events.OfType<OutgoingHtlcFailed>());
        Assert.Equal(id, failed.HtlcId);
        var expectedReason = TwoNodeHarness.FakeErrorPacket(
            FailureMessage.IncorrectOrUnknownPaymentDetails(LightningMoney.MilliSatoshis(amountMsat),
                                                            TwoNodeHarness.BlockHeight));
        Assert.Equal(expectedReason, failed.Removal.Reason.ToArray());
        Assert.Single(bob.Events.OfType<IncomingHtlcLockedIn>());
        Assert.Contains(alice.Received, m => m is UpdateFailHtlcMessage);

        Assert.Empty(alice.State.Htlcs);
        Assert.Empty(bob.State.Htlcs);
        Assert.Equal(TwoNodeHarness.FundingSatoshis * 1_000 - TwoNodeHarness.PushSatoshis * 1_000,
                     alice.State.LocalBalanceMsat);
        AssertAgreement(harness);
        Assert.Equal(2UL, alice.State.LocalCommit.Number);
        Assert.Equal(2UL, alice.State.RemoteCommit.Number);
        Assert.Equal(2UL, bob.State.LocalCommit.Number);
        Assert.Equal(2UL, bob.State.RemoteCommit.Number);

        // Bob stored the onion secret with the HTLC; Alice's switch pruned the settled HTLC's archive (NL-243)
        Assert.Equal(new Secret(Enumerable.Repeat((byte)0xB0, 32).ToArray()),
                     await bob.Store.GetOnionSharedSecretAsync(TwoNodeHarness.ChannelId,
                                                               new HtlcKey(HtlcDirection.Incoming, id)));
        Assert.Equal([new HtlcKey(HtlcDirection.Outgoing, id)], alice.Store.Pruned);
    }

    [Fact]
    public async Task Given_PeerNotAlive_When_Operating_Then_OfferIsRefusedAndAFailWaitsUnsignedForThePeer()
    {
        // Arrange - an HTLC from Alice locked in at Bob, then Bob's peer goes away
        using var harness = new TwoNodeHarness();
        var alice = harness.Alice;
        var bob = harness.Bob;
        var id = await OfferAsync(alice, 30_000_000, TwoNodeHarness.Preimage(3));
        await harness.PumpAsync();
        bob.PeerAlive = false;
        var signedBefore = bob.Signed.Count;

        // Act
        var offer = OfferAsync(bob, 10_000_000, TwoNodeHarness.Preimage(4));
        await bob.Operations.FailHtlcAsync(TwoNodeHarness.ChannelId, id, new byte[292],
                                           TestContext.Current.CancellationToken);
        await bob.Scheduler.WhenIdleAsync();

        // Assert - the offer is refused before anything is persisted; the fail is persisted but not signed
        await Assert.ThrowsAsync<CommitmentRefusedException>(() => offer);
        Assert.Equal(0UL, bob.State.LocalNextHtlcId);
        Assert.Equal(HtlcState.SentRemoveHtlc, bob.State.GetHtlc(HtlcDirection.Incoming, id)!.State);
        Assert.Same(bob.State, bob.Store.Committed);
        Assert.Equal(signedBefore, bob.Signed.Count);

        // Act 2 - the peer is back: the next scheduler run signs the pending fail
        bob.PeerAlive = true;
        Assert.True(await bob.Scheduler.SignNowAsync(TwoNodeHarness.ChannelId, TestContext.Current.CancellationToken));
        await harness.PumpAsync();

        // Assert 2
        AssertAgreement(harness);
        Assert.Single(alice.Events.OfType<OutgoingHtlcFailed>());
    }

    private static Task<ulong> OfferAsync(HarnessNode node, ulong amountMsat, Secret preimage)
    {
        var hash = TwoNodeHarness.Hash(preimage);
        return node.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId, LightningMoney.MilliSatoshis(amountMsat), hash,
                                              CltvExpiry, s_onion, null, HtlcOrigin.Local(hash),
                                              TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// I7 plus mirrored state: every commitment a node signed was verified by its peer under the same number and txid,
    /// nothing is left to sign, and the two snapshots mirror each other.
    /// </summary>
    private static void AssertAgreement(TwoNodeHarness harness)
    {
        var alice = harness.Alice;
        var bob = harness.Bob;

        Assert.Equal(alice.Signed, bob.Verified);
        Assert.Equal(bob.Signed, alice.Verified);
        Assert.Equal(alice.Signed.Count + bob.Signed.Count,
                     alice.Signed.Concat(bob.Signed).Select(c => c.TxId).Distinct().Count());

        Assert.False(alice.State.HasPendingChangesForRemote || bob.State.HasPendingChangesForRemote);
        Assert.Null(alice.State.RemoteNextCommit);
        Assert.Null(bob.State.RemoteNextCommit);
        Assert.Equal(alice.State.LocalCommit.Number, bob.State.RemoteCommit.Number);
        Assert.Equal(bob.State.LocalCommit.Number, alice.State.RemoteCommit.Number);
        Assert.Equal(alice.State.LocalBalanceMsat, bob.State.RemoteBalanceMsat);
        Assert.Equal(alice.State.RemoteBalanceMsat, bob.State.LocalBalanceMsat);
        Assert.Equal(alice.State.Htlcs.Count, bob.State.Htlcs.Count);
        Assert.Equal(alice.State.LocalCommit.Spec.Htlcs.Count, bob.State.RemoteCommit.Spec.Htlcs.Count);
    }

    /// <summary>Amounts from dust (trimmed on one or both commitments) to about 23,000 sat.</summary>
    private static ulong AmountMsat(int i) => (i % 5) switch
    {
        0 => 700_000UL + (ulong)i * 1_000,
        1 => 2_250_000UL,
        _ => (5_000UL + (ulong)i * 500) * 1_000
    };
}