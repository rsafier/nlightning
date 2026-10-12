using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Revoked;

using Channels.Services;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;

/// <summary>
/// BOLT 5 plan O7-T3, penalty rules of option_anchors channels, on a fake chain with real crypto (the breach of
/// <see cref="RevokedResolutionTests"/> on an anchors channel): the HTLC Bob (the victim) offered can be taken by the
/// cheater's HTLC-success at any time, and with <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> that spend can be pinned, so
/// its penalty stands alone from the first round; the cheater may batch its HTLC transactions, so its second-level
/// output is found at the index of the input that spent the HTLC output.
/// </summary>
public class RevokedAnchorResolutionTests
{
    private const ulong B1Msat = 50_000_000;
    private const ulong A1Msat = 40_000_000;
    private const uint B1Expiry = 600;
    private const uint A1Expiry = 650;

    private static readonly Secret s_b1Preimage = RealSigningCommitmentPair.Preimage(0xB1);
    private static readonly Secret s_a1Preimage = RealSigningCommitmentPair.Preimage(0xA1);

    private static RevokedBreachKit CreateBreach(bool hasAnchors)
    {
        var kit = new RevokedBreachKit(hasAnchors: hasAnchors);
        var pair = kit.Pair;
        pair.Add(pair.Bob, B1Msat, s_b1Preimage, B1Expiry);
        pair.Add(pair.Alice, A1Msat, s_a1Preimage, A1Expiry);
        pair.Settle(pair.Bob);
        kit.CaptureRevokedState();
        pair.UpdateFee(3_000);
        pair.Settle(pair.Alice);
        kit.Breach();
        return kit;
    }

    [Fact]
    public async Task Given_AnchorsBreach_When_Resolved_Then_OurOfferedHtlcPenalizedAloneAndTheRestBatched()
    {
        // Arrange
        using var kit = CreateBreach(hasAnchors: true);

        // Act: the first round, far from every deadline
        var actions = await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);

        // Assert: two penalties, every input script-valid
        var broadcasts = actions.OfType<BroadcastAction>().Select(b => b.Transaction).ToList();
        Assert.Equal(2, broadcasts.Count);
        Assert.All(broadcasts, b => kit.AssertVerifies(b.TransactionId));

        var ourOffered = Assert.Single(kit.Rows, r => r is
        {
            Descriptor: OutputDescriptorKind.RevokedHtlc, HtlcDirection: HtlcDirection.Outgoing
        });
        var single = kit.LoadBroadcast(ourOffered.ResolvingTransactionId!.Value);
        Assert.Equal(new OutPoint(new uint256((byte[])ourOffered.TransactionId), ourOffered.OutputIndex),
                     Assert.Single(single.Inputs).PrevOut);

        // to_local, the cheater's HTLC (open only at its cltv_expiry) and our to_remote share the other one
        var batchTxId = broadcasts.Single(b => b.TransactionId != ourOffered.ResolvingTransactionId).TransactionId;
        var batch = kit.LoadBroadcast(batchTxId);
        Assert.Equal(3, batch.Inputs.Count);
        Assert.All(kit.Rows.Where(r => r != ourOffered),
                   r => Assert.Equal(batchTxId, r.ResolvingTransactionId));
        Assert.Contains(kit.Rows, r => r.Descriptor == OutputDescriptorKind.RevokedToLocal);
        var toRemote = Assert.Single(kit.Rows, r => r.Descriptor == OutputDescriptorKind.PaymentToRemote);

        // Anchors to_remote carries a CSV of 1: its input's nSequence is 1 inside the penalty
        var toRemoteInput = batch.Inputs.Single(i => i.PrevOut.N == toRemote.OutputIndex);
        Assert.Equal(1U, toRemoteInput.Sequence.Value);
        Assert.Empty(kit.Alerts);
    }

    [Fact]
    public async Task Given_NoAnchors_When_Resolved_Then_OneBatchAsBefore()
    {
        // Arrange: the anchors rule leaves non-anchor channels alone
        using var kit = CreateBreach(hasAnchors: false);

        // Act
        var actions = await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);

        // Assert
        var broadcast = Assert.Single(actions.OfType<BroadcastAction>()).Transaction;
        Assert.Equal(kit.RevokedCommitment.Outputs.Count, kit.LoadBroadcast(broadcast.TransactionId).Inputs.Count);
    }

    [Fact]
    public async Task Given_CheaterBatchesItsHtlcSuccess_When_Confirmed_Then_SecondLevelAtItsInputIndexPenalized()
    {
        // Arrange: our single penalty of b1 is in the mempool when the cheater's HTLC-success of b1, batched behind a
        // fee input (the HTLC pair at index 1), confirms first
        using var kit = CreateBreach(hasAnchors: true);
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        var htlcSuccess = kit.CheaterBatchedSecondLevel(HtlcDirection.Outgoing, 0, s_b1Preimage);
        var height = RevokedBreachKit.SpentAtHeight + 2;

        // Act
        var onSpent = await kit.ConfirmAsync(htlcSuccess, height);
        var round = await kit.RunAsync(height);

        // Assert: the preimage goes upstream at once (B5-REV-07)
        var fulfilled = Assert.Single(onSpent.OfType<RaiseChannelEventAction>().Select(a => a.Event)
                                             .OfType<OutgoingHtlcFulfilled>());
        Assert.Equal(s_b1Preimage, fulfilled.PaymentPreimage);

        // The second-level output is output 1 (paired with input 1), watched and penalized with <revsig> 1
        var secondLevel = Assert.Single(kit.Rows, r => r.Descriptor == OutputDescriptorKind.RevokedSecondLevel);
        Assert.Equal(htlcSuccess.TxId, secondLevel.TransactionId);
        Assert.Equal(1U, secondLevel.OutputIndex);
        Assert.Contains((secondLevel.TransactionId, 1u), kit.Watches);
        var penalty = Assert.Single(round.OfType<BroadcastAction>(),
                                    b => b.Transaction.TransactionId == secondLevel.ResolvingTransactionId);
        var penaltyTx = kit.LoadBroadcast(penalty.Transaction.TransactionId);
        Assert.Contains(penaltyTx.Inputs, i => i.PrevOut == new OutPoint(new uint256((byte[])htlcSuccess.TxId), 1));
        kit.AssertVerifies(penalty.Transaction.TransactionId, htlcSuccess);

        // The batch of the other outputs was not touched by the cheater's spend
        Assert.DoesNotContain(round.OfType<AlertAction>(), a => a.RequirementId == "B5-REV-08");
    }
}