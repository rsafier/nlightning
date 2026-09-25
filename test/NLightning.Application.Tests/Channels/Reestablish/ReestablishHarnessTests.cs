namespace NLightning.Application.Tests.Channels.Reestablish;

using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.ValueObjects;
using Harness;
using NLightning.Tests.Utils.Mocks;

/// <summary>
/// BOLT2 plan N7 proof in process (invariant I11): two real nodes (<see cref="TwoNodeHarness"/>, production handlers,
/// signers, operations and schedulers, the production <c>LocalOnlyHtlcSwitch</c>) run an HTLC dance in both directions;
/// the link drops at every message boundary, or a node crashes at every save and restarts from what it saved. After
/// channel_reestablish both sides always converge: no HTLC left, every HTLC failed back to its offerer, the commitment
/// numbers and balances mirror each other, and every commitment one side accepted is one the other signed (never two
/// different commitments under one number).
/// </summary>
public class ReestablishHarnessTests
{
    private const uint CltvExpiry = 700;
    private const ulong AliceAmountMsat = 30_000_000;
    private const ulong BobAmountMsat = 20_000_000;

    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);

    [Fact]
    public async Task Given_IdleChannel_When_TheLinkDropsAndComesBack_Then_UpdatesWaitForTheReestablish()
    {
        // Arrange
        using var harness = new TwoNodeHarness(localOnlySwitch: true);
        await harness.DisconnectAsync();

        // Act 1 - while down and before the reestablish, nothing may be offered
        Assert.False(harness.Alice.Tracker.IsReestablished(TwoNodeHarness.ChannelId));
        await Assert.ThrowsAsync<CommitmentRefusedException>(() => OfferAsync(harness.Alice, AliceAmountMsat, 1));

        await harness.ReconnectAsync();
        Assert.False(harness.Alice.Tracker.IsReestablished(TwoNodeHarness.ChannelId));
        await harness.PumpAsync();

        // Assert 1 - both reestablished (one channel_reestablish each way), no failure
        Assert.True(harness.Alice.Tracker.IsReestablished(TwoNodeHarness.ChannelId));
        Assert.True(harness.Bob.Tracker.IsReestablished(TwoNodeHarness.ChannelId));
        Assert.Single(harness.Alice.Received.OfType<ChannelReestablishMessage>());
        Assert.Single(harness.Bob.Received.OfType<ChannelReestablishMessage>());
        Assert.Equal(ChannelState.Open, harness.Alice.Channel.State);

        // Act 2 - updates flow again on the new connection
        await OfferAsync(harness.Alice, AliceAmountMsat, 1);
        await harness.PumpAsync();

        // Assert 2
        AssertConverged(harness, aliceOffered: 1, bobOffered: 0);
    }

    [Fact]
    public async Task Given_TheFullDance_When_RunWithoutFailures_Then_ItNeedsSeveralMessagesAndSaves()
    {
        // Arrange / Act
        var (messages, aliceSaves, bobSaves) = await MeasureAsync();

        // Assert - the tables below are only meaningful when the dance is long enough
        Assert.True(messages >= 10, $"{messages} messages");
        Assert.True(aliceSaves >= 5 && bobSaves >= 5, $"{aliceSaves}/{bobSaves} saves");
    }

    [Fact]
    public async Task Given_TheLinkDropsAtEveryMessageBoundary_When_Reconnected_Then_BothSidesConverge()
    {
        var (messages, _, _) = await MeasureAsync();
        var retransmittedCommitments = 0;
        var retransmittedRevocations = 0;
        for (var boundary = 0; boundary <= messages; boundary++)
        {
            // Arrange - both offer at once (their commitments cross), then only `boundary` messages get through
            using var harness = new TwoNodeHarness(localOnlySwitch: true);
            await OfferBothAsync(harness);
            harness.DeliveryBudget = boundary;
            await harness.PumpAsync();

            // Act
            harness.DeliveryBudget = null;
            await harness.DisconnectAsync();
            await harness.ReconnectAsync();
            await harness.PumpAsync();

            // Assert
            AssertConverged(harness, aliceOffered: 1, bobOffered: 1, $"link drop after {boundary} messages");
            Assert.Equal(0, harness.Restarts);
            // A lost commitment_signed or revoke_and_ack can only have been delivered again by the reestablish
            if (Lost<CommitmentSignedMessage>(harness) > 0)
                retransmittedCommitments++;
            if (Lost<RevokeAndAckMessage>(harness) > 0)
                retransmittedRevocations++;
        }

        // Some boundaries lost a commitment_signed (resent from the stored diff) or a revoke_and_ack (regenerated)
        Assert.True(retransmittedCommitments > 0 && retransmittedRevocations > 0,
                    $"{retransmittedCommitments} commitment and {retransmittedRevocations} revocation retransmissions");
    }

    /// <summary>commitment_signed (and revoke_and_ack) messages of the undisturbed two-offer dance, both ways.</summary>
    private const int CommitmentsPerDance = 8;

    private static int Lost<T>(TwoNodeHarness harness) =>
        harness.Alice.Lost.OfType<T>().Count() + harness.Bob.Lost.OfType<T>().Count();

    private static int Received<T>(TwoNodeHarness harness) =>
        harness.Alice.Received.OfType<T>().Count() + harness.Bob.Received.OfType<T>().Count();

    [Fact]
    public async Task Given_TheLinkDropsTwiceInARow_When_Reconnected_Then_BothSidesConverge()
    {
        var (messages, _, _) = await MeasureAsync();
        for (var boundary = 1; boundary < messages; boundary += 2)
        {
            // Arrange - a drop, then another one during the retransmission
            using var harness = new TwoNodeHarness(localOnlySwitch: true);
            await OfferBothAsync(harness);
            harness.DeliveryBudget = boundary;
            await harness.PumpAsync();
            await harness.DisconnectAsync();
            await harness.ReconnectAsync();
            harness.DeliveryBudget = harness.Delivered + 3;
            await harness.PumpAsync();

            // Act
            harness.DeliveryBudget = null;
            await harness.DisconnectAsync();
            await harness.ReconnectAsync();
            await harness.PumpAsync();

            // Assert
            AssertConverged(harness, aliceOffered: 1, bobOffered: 1, $"double drop after {boundary} messages");
        }
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("Bob")]
    public async Task Given_ACrashAtEverySave_When_TheNodeRestarts_Then_BothSidesConverge(string crashing)
    {
        var (_, aliceSaves, bobSaves) = await MeasureAsync();
        var saves = crashing == "Alice" ? aliceSaves : bobSaves;
        for (var crashAt = 1; crashAt <= saves; crashAt++)
        {
            // Arrange - the node dies instead of performing its crashAt-th save (nothing of that save persists)
            using var harness = new TwoNodeHarness(localOnlySwitch: true);
            var node = crashing == "Alice" ? harness.Alice : harness.Bob;
            node.Store.CrashAtSave = crashAt;

            // Act - an offer whose save crashes is simply lost (the caller sees the crash)
            var aliceOffered = await TryOfferAsync(harness.Alice, AliceAmountMsat, 1);
            await harness.RecoverAsync();
            var bobOffered = await TryOfferAsync(harness.Bob, BobAmountMsat, 2);
            await harness.PumpAsync();

            // Assert
            var context = $"{crashing} crashed at save {crashAt}";
            Assert.True(harness.Restarts == 1, $"{context}: {harness.Restarts} restarts");
            AssertConverged(harness, aliceOffered, bobOffered, context);
        }
    }

    [Fact]
    public async Task Given_ACrashAfterCommitmentSignedIsSaved_When_Restarted_Then_TheRevokeAndAckIsRegenerated()
    {
        // Arrange - Bob saves Alice's commitment_signed, then dies before his revoke_and_ack leaves (his next save is
        // the signature of his own commitment_signed)
        using var harness = new TwoNodeHarness(localOnlySwitch: true);
        await OfferAsync(harness.Alice, AliceAmountMsat, 1);
        await harness.Alice.Scheduler.WhenIdleAsync();
        var bob = harness.Bob;
        bob.Store.CrashAtSave = 2;

        // Act
        await harness.PumpAsync();

        // Assert - Alice re-received the revoke_and_ack for Bob's commitment 0 after the reestablish
        Assert.Equal(1, harness.Restarts);
        Assert.True(harness.Alice.Received.OfType<RevokeAndAckMessage>().Count() >= 2);
        AssertConverged(harness, aliceOffered: 1, bobOffered: 0);
    }

    /// <summary>Runs the two-offer dance without failures: how many messages and saves it takes.</summary>
    private static async Task<(int Messages, int AliceSaves, int BobSaves)> MeasureAsync()
    {
        using var harness = new TwoNodeHarness(localOnlySwitch: true);
        await OfferBothAsync(harness);
        await harness.PumpAsync();
        AssertConverged(harness, 1, 1, "measure");
        Assert.Equal(CommitmentsPerDance, Received<CommitmentSignedMessage>(harness));
        Assert.Equal(CommitmentsPerDance, Received<RevokeAndAckMessage>(harness));
        return (harness.Delivered, harness.Alice.Store.Saves, harness.Bob.Store.Saves);
    }

    private static async Task OfferBothAsync(TwoNodeHarness harness)
    {
        await OfferAsync(harness.Alice, AliceAmountMsat, 1);
        await OfferAsync(harness.Bob, BobAmountMsat, 2);
    }

    /// <summary>Offers, and returns how many HTLCs this made (0 when the node crashed on the offer's save).</summary>
    private static async Task<int> TryOfferAsync(HarnessNode node, ulong amountMsat, int tag)
    {
        try
        {
            await OfferAsync(node, amountMsat, tag);
            return 1;
        }
        catch (SimulatedCrashException)
        {
            return 0;
        }
        catch (CommitmentRefusedException)
        {
            // The node is down (its peer crashed and the link is not reestablished yet)
            return 0;
        }
    }

    private static Task<ulong> OfferAsync(HarnessNode node, ulong amountMsat, int tag)
    {
        var hash = TwoNodeHarness.Hash(TwoNodeHarness.Preimage(tag));
        return node.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId, LightningMoney.MilliSatoshis(amountMsat), hash,
                                              CltvExpiry, s_onion, null, HtlcOrigin.Local(hash),
                                              TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// I11: the channel is back to idle on both sides with every HTLC failed back to its offerer, the state mirrors,
    /// and every commitment accepted by one side was signed by the other (one commitment per number).
    /// </summary>
    private static void AssertConverged(TwoNodeHarness harness, int aliceOffered, int bobOffered,
                                        string context = "")
    {
        var alice = harness.Alice;
        var bob = harness.Bob;
        var a = alice.State;
        var b = bob.State;

        Assert.True(a.Htlcs.IsEmpty && b.Htlcs.IsEmpty,
                    $"{context}: HTLCs left: Alice {a.Htlcs.Count}, Bob {b.Htlcs.Count}");
        Assert.True(a.RemoteNextCommit is null && b.RemoteNextCommit is null, $"{context}: a signature is unacked");
        Assert.False(a.HasPendingChangesForRemote || b.HasPendingChangesForRemote, $"{context}: changes pending");
        Assert.True(a.LocalCommit.Number == b.RemoteCommit.Number && b.LocalCommit.Number == a.RemoteCommit.Number,
                    $"{context}: numbers {a.LocalCommit.Number}/{a.RemoteCommit.Number} vs {b.LocalCommit.Number}/{b.RemoteCommit.Number}");
        Assert.Equal(TwoNodeHarness.FundingSatoshis * 1_000 - TwoNodeHarness.PushSatoshis * 1_000, a.LocalBalanceMsat);
        Assert.Equal(a.LocalBalanceMsat, b.RemoteBalanceMsat);
        Assert.Equal(a.RemoteBalanceMsat, b.LocalBalanceMsat);
        Assert.Equal((ulong)aliceOffered, a.LocalNextHtlcId);
        Assert.Equal((ulong)bobOffered, b.LocalNextHtlcId);
        Assert.Equal(a.LocalNextHtlcId, b.RemoteNextHtlcId);
        Assert.Equal(b.LocalNextHtlcId, a.RemoteNextHtlcId);

        // Every HTLC failed back once (events may be replayed, the ids may not differ)
        Assert.Equal(aliceOffered, alice.Events.OfType<OutgoingHtlcFailed>().Select(e => e.HtlcId).Distinct().Count());
        Assert.Equal(bobOffered, bob.Events.OfType<OutgoingHtlcFailed>().Select(e => e.HtlcId).Distinct().Count());

        // Persisted equals in memory
        Assert.Same(a, alice.Store.Committed);
        Assert.Same(b, bob.Store.Committed);

        AssertSignedAndVerifiedAgree(alice, bob, context);
        AssertSignedAndVerifiedAgree(bob, alice, context);
        Assert.True(alice.Tracker.IsReestablished(TwoNodeHarness.ChannelId), $"{context}: Alice not reestablished");
        Assert.True(bob.Tracker.IsReestablished(TwoNodeHarness.ChannelId), $"{context}: Bob not reestablished");
    }

    private static void AssertSignedAndVerifiedAgree(HarnessNode signer, HarnessNode verifier, string context)
    {
        foreach (var verified in verifier.Verified)
            Assert.True(signer.Signed.Contains(verified),
                        $"{context}: {verifier.Name} accepted commitment {verified.Number} {verified.TxId} that {signer.Name} never signed");

        foreach (var group in verifier.Verified.GroupBy(v => v.Number))
            Assert.True(group.Select(v => v.TxId).Distinct().Count() == 1,
                        $"{context}: {verifier.Name} accepted two different commitments {group.Key}");

        // The last commitment each side holds is one the other signed
        var last = verifier.State.LocalCommit.Number;
        if (last > 0)
            Assert.Contains(verifier.Verified, v => v.Number == last);
    }
}