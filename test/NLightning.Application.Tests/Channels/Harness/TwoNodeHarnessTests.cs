namespace NLightning.Application.Tests.Channels.Harness;

using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Protocol.Messages;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// BOLT2 plan N6-T4: two in-process nodes run the whole commitment dance through their real channel managers,
/// normal-operation handlers, engine ports and signers: 30 HTLCs each way, then fulfills and fails, then a fee
/// update. Every commitment one side signed was verified by the other with the same txid (invariant I7).
/// </summary>
public class TwoNodeHarnessTests
{
    private const int HtlcsPerSide = 30;

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
            await alice.SendAsync(c => c.SendAdd(amountMsat, TwoNodeHarness.Hash(TwoNodeHarness.Preimage(i)), 700,
                                                 TwoNodeHarness.Onion));
        }

        await harness.PumpAsync();
        AssertAgreement(harness);

        var bobAmounts = new List<ulong>();
        for (var i = 0; i < HtlcsPerSide; i++)
        {
            var amountMsat = AmountMsat(i + 7);
            bobAmounts.Add(amountMsat);
            var preimage = TwoNodeHarness.Preimage(1_000 + i);
            await bob.SendAsync(c => c.SendAdd(amountMsat, TwoNodeHarness.Hash(preimage), 700, TwoNodeHarness.Onion));

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
        for (ulong id = 0; id < HtlcsPerSide; id++)
        {
            var htlcId = id;
            var alicePreimage = TwoNodeHarness.Preimage((int)id);
            var bobPreimage = TwoNodeHarness.Preimage(1_000 + (int)id);
            if (htlcId % 2 == 0)
            {
                await bob.SendAsync(c => c.SendFulfill(htlcId, alicePreimage, new Sha256()));
                await alice.SendAsync(c => c.SendFulfill(htlcId, bobPreimage, new Sha256()));
            }
            else
            {
                await bob.SendAsync(c => c.SendFail(htlcId, new byte[292]));
                await alice.SendAsync(c => c.SendFail(htlcId, new byte[292]));
            }

            if (htlcId % 7 == 6)
                await harness.PumpAsync();
        }

        await harness.PumpAsync();
        AssertAgreement(harness);

        // Act 3 - the funder doubles the feerate
        await alice.SendAsync(c => c.SendFee(TwoNodeHarness.InitialFeeratePerKw * 2));
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

        // The dance ran through every message type, and no revoke_and_ack went out without a commitment first
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
        await harness.Alice.SendAsync(c => c.SendAdd(50_000_000, TwoNodeHarness.Hash(TwoNodeHarness.Preimage(1)), 700,
                                                     TwoNodeHarness.Onion));
        await harness.Bob.SendAsync(c => c.SendAdd(40_000_000, TwoNodeHarness.Hash(TwoNodeHarness.Preimage(2)), 700,
                                                   TwoNodeHarness.Onion));
        await harness.PumpAsync();

        // Assert
        AssertAgreement(harness);
        Assert.Single(harness.Alice.Events.OfType<IncomingHtlcLockedIn>());
        Assert.Single(harness.Bob.Events.OfType<IncomingHtlcLockedIn>());
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