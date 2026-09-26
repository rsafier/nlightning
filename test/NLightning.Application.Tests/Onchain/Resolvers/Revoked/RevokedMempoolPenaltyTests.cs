namespace NLightning.Application.Tests.Onchain.Resolvers.Revoked;

using Application.Onchain.Resolvers;
using Channels.Services;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;

/// <summary>
/// BOLT 5 plan O8: <see cref="RevokedCommitResolver.PrepareUnconfirmedPenaltiesAsync"/> signs the penalties of a revoked
/// commitment that is still in the mempool, with real keys, and records nothing (the rows wait for the confirmation).
/// </summary>
public class RevokedMempoolPenaltyTests
{
    [Fact]
    public async Task Given_RevokedCommitmentInTheMempool_When_Prepared_Then_OneValidPenaltyForEveryOutputAndNoRow()
    {
        // Arrange: state k with an HTLC each way, revoked by a fee update
        using var kit = new RevokedBreachKit();
        var pair = kit.Pair;
        pair.Add(pair.Bob, 40_000_000, RealSigningCommitmentPair.Preimage(0xB1), 700);
        pair.Add(pair.Alice, 30_000_000, RealSigningCommitmentPair.Preimage(0xA1), 710);
        pair.Settle(pair.Bob);
        kit.CaptureRevokedState();
        pair.UpdateFee(3_000);
        pair.Settle(pair.Alice);
        kit.Breach();

        // Act: the tip is one block below the first block that can hold the commitment
        var actions = await kit.Resolver.PrepareUnconfirmedPenaltiesAsync(
                          RealSigningCommitmentPair.ChannelId, kit.RevokedChainTx, kit.RevokedNumber,
                          RevokedBreachKit.SpentAtHeight - 1, TestContext.Current.CancellationToken);
        kit.Apply(actions);

        // Assert: one batched penalty over every output (to_local, both HTLCs, our to_remote), script-valid against
        // the unconfirmed commitment; no row, watch or switch event (they wait for the block)
        var broadcast = Assert.Single(actions.OfType<BroadcastAction>()).Transaction;
        Assert.Equal(BroadcastPurpose.Penalty, broadcast.Purpose);
        var penalty = kit.LoadBroadcast(broadcast.TransactionId);
        Assert.Equal(kit.RevokedCommitment.Outputs.Count, penalty.Inputs.Count);
        kit.AssertVerifies(broadcast.TransactionId);
        Assert.DoesNotContain(actions, a => a is UpsertOutputAction or WatchOutpointAction or RaiseChannelEventAction);
        Assert.Empty(kit.Rows);
    }

    [Fact]
    public async Task Given_NoContextForTheCommitment_When_Prepared_Then_OnlyAnAlert()
    {
        // Arrange: the victim's data cannot serve the breach (e.g. the secret is not in the shachain)
        using var kit = new RevokedBreachKit();
        kit.CaptureRevokedState();

        // Act
        var actions = await kit.Resolver.PrepareUnconfirmedPenaltiesAsync(
                          RealSigningCommitmentPair.ChannelId, kit.RevokedChainTx, 0,
                          RevokedBreachKit.SpentAtHeight - 1, TestContext.Current.CancellationToken);

        // Assert
        var alert = Assert.IsType<AlertAction>(Assert.Single(actions));
        Assert.Equal("B5-REV-03", alert.RequirementId);
    }
}