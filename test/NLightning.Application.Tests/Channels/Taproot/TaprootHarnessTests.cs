using NBitcoin;

namespace NLightning.Application.Tests.Channels.Taproot;

using Application.Channels.Safety;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.ValueObjects;
using Harness;
using Infrastructure.Bitcoin.Builders.Interfaces;
using NLightning.Tests.Utils.Mocks;

/// <summary>
/// Simple taproot channels in normal operation and after reconnections, restarts and crashes (NL-877 T3/T5, plan
/// D-T4): two in-process nodes (<see cref="TwoNodeHarness"/> with <c>simpleTaproot</c>: production handlers, engine
/// ports, <c>LocalLightningSigner</c>s, operations and schedulers) exchange MuSig2 <c>commitment_signed</c> (zero
/// signature, <c>partial_signature_with_nonce</c>, BIP 340 HTLC signatures), <c>revoke_and_ack</c> and
/// <c>channel_reestablish</c> with <c>next_local_nonces</c>; a retransmitted <c>commitment_signed</c> is signed again
/// with a fresh nonce. The nonce-reuse proof: no public signing nonce is ever sent twice, every accepted partial
/// signature verified, and each local commitment number (whose verification nonce signs it for broadcast) was only
/// ever accepted for one commitment transaction.
/// </summary>
public class TaprootHarnessTests
{
    private const uint CltvExpiry = 700;
    private const ulong AliceAmountMsat = 30_000_000;
    private const ulong BobAmountMsat = 20_000_000;

    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);

    [Fact]
    public async Task Given_TaprootChannel_When_HtlcsAreFulfilledAndFailedBothWays_Then_MusigCommitmentsAgree()
    {
        // Arrange
        using var harness = new TwoNodeHarness(simpleTaproot: true);
        var alice = harness.Alice;
        var bob = harness.Bob;
        var aliceStart = alice.State.LocalBalanceMsat;
        var ct = TestContext.Current.CancellationToken;

        // Act - Alice offers two HTLCs (one fulfilled, one failed), Bob one (fulfilled), then a fee update
        await OfferAsync(alice, AliceAmountMsat, 1);
        await OfferAsync(alice, 5_000_000, 2);
        await OfferAsync(bob, BobAmountMsat, 3);
        await harness.PumpAsync();
        await bob.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, 0, TwoNodeHarness.Preimage(1), ct);
        await bob.Operations.FailHtlcAsync(TwoNodeHarness.ChannelId, 1, new byte[292], ct);
        await alice.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, 0, TwoNodeHarness.Preimage(3), ct);
        await harness.PumpAsync();
        await alice.Operations.UpdateFeeAsync(TwoNodeHarness.ChannelId, TwoNodeHarness.InitialFeeratePerKw * 2, ct);
        await harness.PumpAsync();

        // Assert - settled on both sides, balances moved by the fulfilled HTLCs
        Assert.Empty(alice.State.Htlcs);
        Assert.Empty(bob.State.Htlcs);
        Assert.Equal(aliceStart - AliceAmountMsat + BobAmountMsat, alice.State.LocalBalanceMsat);
        Assert.Equal(alice.State.LocalBalanceMsat, bob.State.RemoteBalanceMsat);
        Assert.Single(alice.Events.OfType<OutgoingHtlcFulfilled>());
        Assert.Single(alice.Events.OfType<OutgoingHtlcFailed>());
        Assert.Single(bob.Events.OfType<OutgoingHtlcFulfilled>());
        AssertSignedAndVerifiedAgree(alice, bob, "dance");
        AssertSignedAndVerifiedAgree(bob, alice, "dance");

        // Every commitment_signed: zero signature, a partial signature with nonce, 64-byte HTLC signatures
        var signedMessages = alice.Received.Concat(bob.Received).OfType<CommitmentSignedMessage>().ToList();
        Assert.True(signedMessages.Count >= 6, $"{signedMessages.Count} commitment_signed");
        Assert.All(signedMessages, m =>
        {
            Assert.True(m.Payload.Signature.IsZero);
            Assert.NotNull(m.PartialSignatureWithNonceTlv);
            Assert.All(m.Payload.HtlcSignatures, s => Assert.Equal(64, ((byte[])s).Length));
        });
        Assert.Contains(signedMessages, m => m.Payload.HtlcSignatures.Any());

        // Every revoke_and_ack: next_local_nonces with exactly the funding's entry, the nonce for the commitment the
        // peer signs next (one after the commitment the sender now holds)
        var revocations = alice.Received.OfType<RevokeAndAckMessage>().ToList();
        Assert.NotEmpty(revocations);
        for (var i = 0; i < revocations.Count; i++)
        {
            var entry = Assert.Single(revocations[i].NextLocalNoncesTlv!.Nonces.Entries);
            Assert.Equal(harness.FundingTxId, entry.FundingTxId);
            Assert.Equal(bob.Signer.GetLocalVerificationNonce(TwoNodeHarness.ChannelId, harness.FundingTxId,
                                                              (ulong)i + 2), entry.Nonce);
        }

        AssertNoSigningNonceReused(harness, "dance");
        Assert.Single(alice.State.RemoteNextNonces);
        Assert.Single(bob.State.RemoteNextNonces);
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("Bob")]
    public async Task Given_HtlcsInFlight_When_ANodeRestarts_Then_TheReestablishCarriesNoncesAndTheChannelGoesOn(
        string restarting)
    {
        // Arrange - HTLCs from both sides locked in, then one more from Alice whose commitment_signed is lost
        using var harness = new TwoNodeHarness(simpleTaproot: true);
        var ct = TestContext.Current.CancellationToken;
        await OfferAsync(harness.Alice, AliceAmountMsat, 1);
        await OfferAsync(harness.Bob, BobAmountMsat, 2);
        await harness.PumpAsync();
        await OfferAsync(harness.Alice, 7_000_000, 3);
        await harness.Alice.Scheduler.WhenIdleAsync();
        harness.DeliveryBudget = harness.Delivered + 1;
        await harness.PumpAsync();
        Assert.NotNull(harness.Alice.State.RemoteNextCommit);

        // Act - the node restarts from what it saved (link down), then both reconnect
        var node = restarting == "Alice" ? harness.Alice : harness.Bob;
        await harness.RestartNodeAsync(node);
        harness.DeliveryBudget = null;
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert - both reestablished with nonces, Alice's commitment_signed signed again (fresh nonce, not replayed)
        var alice = harness.Alice;
        var bob = harness.Bob;
        Assert.All(alice.Received.Concat(bob.Received).OfType<ChannelReestablishMessage>(),
                   m => Assert.True(m.NextLocalNoncesTlv!.Nonces.Contains(harness.FundingTxId)));
        Assert.Equal(2UL, bob.State.RemoteNextHtlcId);
        Assert.Equal(2, bob.Events.OfType<IncomingHtlcLockedIn>().Select(e => e.HtlcId).Distinct().Count());
        AssertNoSigningNonceReused(harness, $"{restarting} restarted");

        // Act 2 - everything settles over the reestablished channel
        await bob.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, 0, TwoNodeHarness.Preimage(1), ct);
        await bob.Operations.FulfillHtlcAsync(TwoNodeHarness.ChannelId, 1, TwoNodeHarness.Preimage(3), ct);
        await alice.Operations.FailHtlcAsync(TwoNodeHarness.ChannelId, 0, new byte[292], ct);
        await harness.PumpAsync();

        // Assert 2
        Assert.Empty(alice.State.Htlcs);
        Assert.Empty(bob.State.Htlcs);
        Assert.Equal(alice.State.LocalBalanceMsat, bob.State.RemoteBalanceMsat);
        Assert.Equal(TwoNodeHarness.FundingSatoshis * 1_000 - TwoNodeHarness.PushSatoshis * 1_000 - AliceAmountMsat
                   - 7_000_000, alice.State.LocalBalanceMsat);
        AssertSignedAndVerifiedAgree(alice, bob, restarting);
        AssertSignedAndVerifiedAgree(bob, alice, restarting);
        AssertNoSigningNonceReused(harness, $"{restarting} restarted, settled");
        await AssertLatestCommitmentBroadcastVerifiesAsync(alice);
        await AssertLatestCommitmentBroadcastVerifiesAsync(bob);
    }

    [Fact]
    public async Task Given_TheLinkDropsAtEveryMessageBoundary_When_Reconnected_Then_BothConvergeWithoutNonceReuse()
    {
        var (messages, _, _) = await MeasureAsync();
        var resigned = 0;
        for (var boundary = 0; boundary <= messages; boundary++)
        {
            // Arrange - both offer at once, then only `boundary` messages get through
            using var harness = new TwoNodeHarness(localOnlySwitch: true, simpleTaproot: true);
            await OfferBothAsync(harness);
            harness.DeliveryBudget = boundary;
            await harness.PumpAsync();

            // Act
            harness.DeliveryBudget = null;
            await harness.DisconnectAsync();
            await harness.ReconnectAsync();
            await harness.PumpAsync();

            // Assert
            var context = $"link drop after {boundary} messages";
            AssertConverged(harness, 1, 1, context);
            AssertNoSigningNonceReused(harness, context);
            if (harness.Alice.Lost.Concat(harness.Bob.Lost).OfType<CommitmentSignedMessage>().Any())
                resigned++;
        }

        // Some boundaries lost a commitment_signed: it went out again, signed with a fresh nonce
        Assert.True(resigned > 0, "no boundary lost a commitment_signed");
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("Bob")]
    public async Task Given_ACrashAtEverySave_When_TheNodeRestarts_Then_NoNonceIsReusedAndBothConverge(string crashing)
    {
        var (_, aliceSaves, bobSaves) = await MeasureAsync();
        var saves = crashing == "Alice" ? aliceSaves : bobSaves;
        for (var crashAt = 1; crashAt <= saves; crashAt++)
        {
            // Arrange - the node dies instead of performing its crashAt-th save (persist and send points of
            // commitment_signed, revoke_and_ack and the reestablish's nonce save and re-signing)
            using var harness = new TwoNodeHarness(localOnlySwitch: true, simpleTaproot: true);
            var node = crashing == "Alice" ? harness.Alice : harness.Bob;
            node.Store.CrashAtSave = crashAt;

            // Act
            var aliceOffered = await TryOfferAsync(harness.Alice, AliceAmountMsat, 1);
            await harness.RecoverAsync();
            var bobOffered = await TryOfferAsync(harness.Bob, BobAmountMsat, 2);
            await harness.PumpAsync();

            // Assert
            var context = $"{crashing} crashed at save {crashAt}";
            Assert.True(harness.Restarts == 1, $"{context}: {harness.Restarts} restarts");
            AssertConverged(harness, aliceOffered, bobOffered, context);
            AssertNoSigningNonceReused(harness, context);
            await AssertLatestCommitmentBroadcastVerifiesAsync(harness.Alice);
            await AssertLatestCommitmentBroadcastVerifiesAsync(harness.Bob);
        }
    }

    [Fact]
    public async Task Given_ChannelReestablishWithoutNonces_When_Received_Then_TheChannelFails()
    {
        // Arrange - an HTLC in flight, then the link drops; Bob's channel_reestablish loses its next_local_nonces
        using var harness = new TwoNodeHarness(simpleTaproot: true);
        await OfferAsync(harness.Alice, AliceAmountMsat, 1);
        await harness.PumpAsync();
        await harness.DisconnectAsync();
        await harness.ReconnectAsync();
        Assert.True(harness.Bob.TryTakeNext(out var bobMessage));
        var reestablish = Assert.IsType<ChannelReestablishMessage>(bobMessage);
        var stripped = new ChannelReestablishMessage(reestablish.Payload);

        // Act
        var failure = await Assert.ThrowsAsync<ChannelFailedException>(
                          () => harness.Alice.ChannelManager.HandleChannelMessageAsync(
                              stripped, harness.Bob.NegotiatedFeatures, harness.Bob.NodeId));

        // Assert
        Assert.Equal("TAPROOT-RE-R01", failure.RequirementId);
        Assert.Equal(ChannelState.Failed, harness.Alice.Channel.State);
    }

    [Fact]
    public async Task Given_RevokeAndAckWithoutNonces_When_Received_Then_TheChannelFails()
    {
        // Arrange - Alice's commitment_signed reaches Bob; Bob's revoke_and_ack loses its next_local_nonces
        using var harness = new TwoNodeHarness(simpleTaproot: true);
        await OfferAsync(harness.Alice, AliceAmountMsat, 1);
        await harness.Alice.Scheduler.WhenIdleAsync();
        harness.DeliveryBudget = 2;
        await harness.PumpAsync();
        Assert.True(harness.Bob.TryTakeNext(out var bobMessage));
        var revokeAndAck = Assert.IsType<RevokeAndAckMessage>(bobMessage);

        // Act
        var failure = await Assert.ThrowsAsync<ChannelFailedException>(
                          () => harness.Alice.ChannelManager.HandleChannelMessageAsync(
                              new RevokeAndAckMessage(revokeAndAck.Payload), harness.Bob.NegotiatedFeatures,
                              harness.Bob.NodeId));

        // Assert
        Assert.Equal("TAPROOT-NONCE-R01", failure.RequirementId);
    }

    [Fact]
    public async Task Given_CommitmentSignedWithANonZeroSignature_When_Received_Then_TheChannelFails()
    {
        // Arrange - Alice's commitment_signed carries a non-zero 64-byte signature next to its partial signature
        using var harness = new TwoNodeHarness(simpleTaproot: true);
        await OfferAsync(harness.Alice, AliceAmountMsat, 1);
        await harness.Alice.Scheduler.WhenIdleAsync();
        harness.DeliveryBudget = 1;
        await harness.PumpAsync();
        Assert.True(harness.Alice.TryTakeNext(out var aliceMessage));
        var signed = Assert.IsType<CommitmentSignedMessage>(aliceMessage);
        var signature = new byte[64];
        signature[10] = 1;
        var tampered = new CommitmentSignedMessage(
            new Domain.Protocol.Payloads.CommitmentSignedPayload(signed.Payload.ChannelId,
                                                                 signed.Payload.HtlcSignatures,
                                                                 new CompactSignature(signature)),
            signed.FundingTxIdTlv, signed.PartialSignatureWithNonceTlv);

        // Act
        var failure = await Assert.ThrowsAsync<ChannelFailedException>(
                          () => harness.Bob.ChannelManager.HandleChannelMessageAsync(
                              tampered, harness.Alice.NegotiatedFeatures, harness.Alice.NodeId));

        // Assert
        Assert.Equal("TAPROOT-CS-R00", failure.RequirementId);
    }

    [Theory]
    [InlineData(ChannelState.ReadyForUs, ChannelState.ReadyForUs)]
    [InlineData(ChannelState.ReadyForUs, ChannelState.Open)]
    public async Task Given_ChannelReadyLostWithTheLink_When_Reconnected_Then_ItIsResentWithItsNonce(
        ChannelState aliceState, ChannelState bobState)
    {
        // Arrange
        using var harness = new TwoNodeHarness(localOnlySwitch: true, aliceState: aliceState, bobState: bobState,
                                               simpleTaproot: true);
        await harness.DisconnectAsync();

        // Act
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert - channel_ready carries our verification nonce for commitment 1, the same as the reestablish's
        var ready = Assert.Single(harness.Bob.Received.OfType<ChannelReadyMessage>());
        var expected = harness.Alice.Signer.GetLocalVerificationNonce(TwoNodeHarness.ChannelId, harness.FundingTxId, 1);
        Assert.Equal(expected, ready.NextLocalNonceTlv!.Nonce);
        Assert.Equal(ChannelState.Open, harness.Alice.Channel.State);
        Assert.Equal(ChannelState.Open, harness.Bob.Channel.State);

        // Act 2 - the channel carries an HTLC
        await OfferAsync(harness.Alice, AliceAmountMsat, 1);
        await harness.PumpAsync();
        AssertConverged(harness, aliceOffered: 1, bobOffered: 0);
    }

    /// <summary>Runs the two-offer dance without failures: how many messages and saves it takes.</summary>
    private static async Task<(int Messages, int AliceSaves, int BobSaves)> MeasureAsync()
    {
        using var harness = new TwoNodeHarness(localOnlySwitch: true, simpleTaproot: true);
        await OfferBothAsync(harness);
        await harness.PumpAsync();
        AssertConverged(harness, 1, 1, "measure");
        return (harness.Delivered, harness.Alice.Store.Saves, harness.Bob.Store.Saves);
    }

    private static async Task OfferBothAsync(TwoNodeHarness harness)
    {
        await OfferAsync(harness.Alice, AliceAmountMsat, 1);
        await OfferAsync(harness.Bob, BobAmountMsat, 2);
    }

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
    /// D-T4: every public signing nonce either node ever put in a <c>commitment_signed</c> (delivered, lost with a
    /// link or dropped while the link was down) is distinct, so no retransmission or restart signed with one twice.
    /// </summary>
    private static void AssertNoSigningNonceReused(TwoNodeHarness harness, string context)
    {
        foreach (var (sender, receiver) in new[] { (harness.Alice, harness.Bob), (harness.Bob, harness.Alice) })
        {
            var nonces = receiver.Received.Concat(sender.Lost).Concat(sender.Dropped)
                                 .OfType<CommitmentSignedMessage>()
                                 .Select(m => m.PartialSignatureWithNonceTlv!.PartialSignatureWithNonce.PublicNonce)
                                 .ToList();
            var duplicates = nonces.GroupBy(n => n).Where(g => g.Count() > 1).ToList();
            Assert.True(duplicates.Count == 0,
                        $"{context}: {sender.Name} sent signing nonce {duplicates.FirstOrDefault()?.Key} more than once");
        }
    }

    /// <summary>
    /// Each local commitment number the node accepted was one commitment transaction (its verification nonce signs
    /// only that one for broadcast), signed by the peer.
    /// </summary>
    private static void AssertSignedAndVerifiedAgree(HarnessNode signer, HarnessNode verifier, string context)
    {
        foreach (var verified in verifier.Verified)
            Assert.True(signer.Signed.Contains(verified),
                        $"{context}: {verifier.Name} accepted commitment {verified.Number} {verified.TxId} that {signer.Name} never signed");

        foreach (var group in verifier.Verified.GroupBy(v => v.Number))
            Assert.True(group.Select(v => v.TxId).Distinct().Count() == 1,
                        $"{context}: {verifier.Name} accepted two different commitments {group.Key}");
    }

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
        Assert.Equal(a.LocalBalanceMsat, b.RemoteBalanceMsat);
        Assert.Equal(a.RemoteBalanceMsat, b.LocalBalanceMsat);
        Assert.Equal((ulong)aliceOffered, a.LocalNextHtlcId);
        Assert.Equal((ulong)bobOffered, b.LocalNextHtlcId);
        Assert.Equal(aliceOffered, alice.Events.OfType<OutgoingHtlcFailed>().Select(e => e.HtlcId).Distinct().Count());
        Assert.Equal(bobOffered, bob.Events.OfType<OutgoingHtlcFailed>().Select(e => e.HtlcId).Distinct().Count());
        Assert.Same(a, alice.Store.Committed);
        Assert.Same(b, bob.Store.Committed);

        // Each side holds the peer's verification nonce for its next commitment, from the last revoke_and_ack or
        // channel_reestablish
        Assert.True(a.HasRemoteNoncesForActiveFundings && b.HasRemoteNoncesForActiveFundings,
                    $"{context}: a peer nonce is missing");
        AssertSignedAndVerifiedAgree(alice, bob, context);
        AssertSignedAndVerifiedAgree(bob, alice, context);
        Assert.True(alice.Tracker.IsReestablished(TwoNodeHarness.ChannelId), $"{context}: Alice not reestablished");
        Assert.True(bob.Tracker.IsReestablished(TwoNodeHarness.ChannelId), $"{context}: Bob not reestablished");
    }

    /// <summary>
    /// The node's latest commitment, signed for broadcast from the peer's stored partial signature
    /// (<c>SignLocalCommitmentForBroadcast</c>), passes script verification against the MuSig2 funding output.
    /// </summary>
    private static Task AssertLatestCommitmentBroadcastVerifiesAsync(HarnessNode node)
    {
        if (node.State.LocalCommit.Number == 0)
            return Task.CompletedTask;

        var services = node.Services;
        var builder = new LocalCommitmentBroadcastBuilder(
            (ICommitmentTransactionModelFactory)services.GetService(typeof(ICommitmentTransactionModelFactory))!,
            (ICommitmentTransactionBuilder)services.GetService(typeof(ICommitmentTransactionBuilder))!, node.Signer);
        var signed = builder.Build(node.Channel);

        var musig2 = (IMusig2Service)services.GetService(typeof(IMusig2Service))!;
        var funding = node.Channel.FundingOutput!;
        var outputKey = musig2.AggregateTaprootKeyPath(funding.LocalFundingPubKey, funding.RemoteFundingPubKey);
        var prevOut = new TxOut(Money.Satoshis(TwoNodeHarness.FundingSatoshis),
                                new Script(outputKey.GetTaprootScriptPubKey()));
        var tx = Transaction.Load(signed.Transaction.RawTxBytes, Network.RegTest);
        var precomputed = tx.PrecomputeTransactionData([prevOut]);
        var input = tx.Inputs.AsIndexedInputs().Single();
        Assert.True(input.VerifyScript(prevOut, ScriptVerify.Standard | ScriptVerify.Taproot, precomputed,
                                       out var error),
                    $"{node.Name}'s commitment {signed.CommitmentNumber} does not verify: {error}");
        Assert.Equal(new TxId(tx.GetHash().ToBytes()), signed.Transaction.TxId);
        return Task.CompletedTask;
    }
}
