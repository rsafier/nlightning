using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Local;

using Channels.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using static LocalCommitResolutionHarness;

/// <summary>
/// NL-966 (1, 5, 6) / NL-904 item 4 with a fake chain: our simple taproot commitment is on chain with an HTLC in each
/// direction and <see cref="Application.Onchain.Resolvers.LocalCommitResolver"/> resolves every output: the zero-fee
/// HTLC-success (preimage known) and HTLC-timeout (at <c>cltv_expiry</c>) combined with wallet fee inputs and signed by
/// the production signer over every spent output, the P2TR second-level outputs swept by their delay leaf after the
/// CSV, <c>to_local</c> by its delay leaf, the offered HTLC failed upstream as an on-chain timeout, and every row
/// irrevocable. Every input of every transaction is executed against all the outputs it spends.
/// </summary>
public sealed class LocalTaprootHtlcResolutionTests
{
    private const ulong OfferedMsat = 20_000_000;
    private const ulong ReceivedMsat = 30_000_000;
    private const uint OfferedCltv = 1_010;
    private const uint ReceivedCltv = 1_020;

    private static readonly Secret s_offeredPreimage = RealSigningCommitmentPair.Preimage(1);
    private static readonly Secret s_receivedPreimage = RealSigningCommitmentPair.Preimage(2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_OurTaprootCommitmentWithHtlcsBothWays_When_ResolvedWithARestart_Then_EveryOutputIsResolved(
        bool taprootFeeInputs)
    {
        // Arrange: Alice offered one HTLC and fulfilled Bob's, then her commitment went on chain
        var wallet = new AnchorTestWallet(taprootFeeInputs, 60_000, 60_000);
        using var harness = CreateHarness(wallet, pair =>
        {
            pair.Add(pair.Alice, OfferedMsat, s_offeredPreimage, OfferedCltv);
            var id = pair.Add(pair.Bob, ReceivedMsat, s_receivedPreimage, ReceivedCltv);
            pair.Settle(pair.Alice);
            pair.Alice.Apply("fulfill", pair.Alice.State.SendFulfill(id, s_receivedPreimage,
                                                                     new Infrastructure.Crypto.Hashes.Sha256()));
        });

        // Act: the first round
        await harness.ResolveAsync();
        var offeredVout = harness.VoutOf(OutputDescriptorKind.LocalOfferedHtlc);
        var receivedVout = harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc);

        // Assert: the HTLC-success at once, by the success leaf with the preimage, our wallet paying its fee
        var success = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Equal(new OutPoint(harness.CommitmentTransaction, receivedVout), success.Inputs[0].PrevOut);
        Assert.Equal(1u, success.Inputs[0].Sequence.Value);
        Assert.Equal(5, success.Inputs[0].WitScript.PushCount);
        Assert.Equal(65, success.Inputs[0].WitScript[0].Length);
        Assert.Equal(0x83, success.Inputs[0].WitScript[0][64]);
        Assert.Equal(64, success.Inputs[0].WitScript[1].Length);
        Assert.Equal((byte[])s_receivedPreimage, success.Inputs[0].WitScript[2]);
        Assert.True(success.Inputs.Count >= 2);
        Assert.Equal(harness.CommitmentTransaction.Outputs[receivedVout].Value, success.Outputs[0].Value);
        harness.AssertAllInputsVerify(success);

        // The wallet signed knowing the HTLC input is the P2TR commitment output
        Assert.Equal(harness.CommitmentTransaction.Outputs[receivedVout].ScriptPubKey.ToBytes(),
                     (byte[])wallet.SignedHtlcInputs[0].ScriptPubKey);

        // Act: it confirms; then the tip reaches the offered HTLC's cltv_expiry
        await harness.MineAsync();
        var successConfirmedAt = harness.Height;
        await harness.MineToAsync(OfferedCltv);

        // Assert: the HTLC-timeout, nLockTime = cltv_expiry, no preimage item
        var timeout = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction),
                                    t => t.Inputs[0].PrevOut == new OutPoint(harness.CommitmentTransaction,
                                                                              offeredVout));
        Assert.Equal(OfferedCltv, (uint)timeout.LockTime);
        Assert.Equal(4, timeout.Inputs[0].WitScript.PushCount);
        harness.AssertAllInputsVerify(timeout);

        // Act: the HTLC-timeout confirms, then the node restarts before any second-level output is swept
        await harness.MineAsync();
        var timeoutConfirmedAt = harness.Height;
        var timeoutTxId = new TxId(timeout.GetHash().ToBytes());
        var successTxId = new TxId(success.GetHash().ToBytes());
        Assert.True(harness.Rows.ContainsKey((timeoutTxId, 0)));
        Assert.True(harness.Rows.ContainsKey((successTxId, 0)));
        Assert.DoesNotContain(harness.Broadcast(BroadcastPurpose.Sweep),
                              s => s.Inputs.Any(i => i.PrevOut.Hash == timeout.GetHash()
                                                  || i.PrevOut.Hash == success.GetHash()));
        harness.Restart();
        await harness.ResolveAsync();

        // Act: past every CSV
        await harness.MineToAsync(Math.Max(timeoutConfirmedAt, CloseHeight) + Csv + 1);

        // Assert: both P2TR second-level outputs swept by their delay leaf (nSequence = to_self_delay), to_local too
        foreach (var (htlcTx, confirmedAt) in new[] { (success, successConfirmedAt), (timeout, timeoutConfirmedAt) })
        {
            var row = harness.Rows[(new TxId(htlcTx.GetHash().ToBytes()), 0)];
            Assert.Equal(OutputDescriptorKind.DelayedToLocal, row.Descriptor);
            var sweep = Assert.Single(harness.Broadcast(BroadcastPurpose.Sweep),
                                      s => s.Inputs.Any(i => i.PrevOut == new OutPoint(htlcTx, 0)));
            var input = Assert.Single(sweep.Inputs);
            Assert.Equal((uint)Csv, input.Sequence.Value);
            Assert.Equal(3, input.WitScript.PushCount);
            Assert.Equal(64, input.WitScript[0].Length);
            harness.AssertAllInputsVerify(sweep);
            Assert.True(confirmedAt + Csv - 1 <= harness.Height);
        }

        var toLocalSweep = Assert.Single(harness.Broadcast(BroadcastPurpose.Sweep),
                                         s => s.Inputs.Any(i => i.PrevOut.Hash
                                                             == harness.CommitmentTransaction.GetHash()));
        harness.AssertAllInputsVerify(toLocalSweep);

        // The offered HTLC failed upstream as an on-chain timeout once its HTLC-timeout was reasonably deep
        var failed = Assert.IsType<OutgoingHtlcFailed>(harness.Events.First(e => e.Event is OutgoingHtlcFailed).Event);
        Assert.Equal(RealSigningCommitmentPair.Hash(s_offeredPreimage), failed.PaymentHash);
        Assert.Equal(HtlcRemovalKind.OnchainTimeout, failed.Removal.Kind);
        Assert.InRange(harness.Events.First(e => e.Event is OutgoingHtlcFailed).Height, timeoutConfirmedAt + 5,
                       timeoutConfirmedAt + 6);

        // Act: everything 100 deep
        await harness.MineToAsync(harness.Height + 101);

        // Assert: every output of ours irrevocably resolved, nothing left for the NL-966 alert
        Assert.All(harness.Rows.Values, r => Assert.Equal(OutputResolutionState.Irrevocable, r.State));
        Assert.Equal(5, harness.Rows.Count); // to_local, two HTLC outputs, two second-level outputs
        Assert.DoesNotContain(harness.Alerts, a => a.RequirementId == "NL-966");
        Assert.Empty(harness.Alerts);
        Assert.Empty(wallet.Reserved);
    }

    [Fact]
    public async Task Given_ATaprootHtlcSuccessUnconfirmed_When_TheBumpIsDue_Then_ItIsReplacedAndSignedAgain()
    {
        // Arrange: the HTLC-success is broadcast at once and blocks leave it out
        var wallet = new AnchorTestWallet(true, 60_000, 50_000);
        using var harness = CreateHarness(wallet, pair =>
        {
            var id = pair.Add(pair.Bob, ReceivedMsat, s_receivedPreimage, ReceivedCltv);
            pair.Settle(pair.Bob);
            pair.Alice.Apply("fulfill", pair.Alice.State.SendFulfill(id, s_receivedPreimage,
                                                                     new Infrastructure.Crypto.Hashes.Sha256()));
        });
        harness.HoldMempool = true;
        await harness.ResolveAsync();
        var vout = harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc);
        var first = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        var firstId = new TxId(first.GetHash().ToBytes());

        // Act: RbfIntervalBlocks (2) pass
        await harness.MineAsync();
        await harness.MineAsync();

        // Assert: a replacement of the same HTLC input with a higher fee, our signature made again over its new spent
        // outputs and change, valid by execution; the first one replaced
        var transactions = harness.Broadcast(BroadcastPurpose.HtlcTransaction);
        Assert.Equal(2, transactions.Count);
        var second = transactions[1];
        var secondId = new TxId(second.GetHash().ToBytes());
        Assert.Equal(firstId, harness.Broadcasts[secondId].ReplacesTransactionId);
        Assert.Equal(BroadcastState.Replaced, harness.Broadcasts[firstId].State);
        Assert.Equal(secondId, harness.CommitmentRow(vout).ResolvingTransactionId);
        Assert.Equal(first.Inputs[0].PrevOut, second.Inputs[0].PrevOut);
        Assert.Equal(first.Outputs[0].Value, second.Outputs[0].Value);
        Assert.NotEqual(first.Inputs[0].WitScript[1], second.Inputs[0].WitScript[1]);
        harness.AssertAllInputsVerify(second);
        Assert.True(FeeOf(second, wallet) > FeeOf(first, wallet));

        // Act: the replacement confirms
        harness.HoldMempool = false;
        await harness.MineAsync();
        await harness.MineAsync();

        // Assert
        Assert.Equal(OutputResolutionState.Resolved, harness.CommitmentRow(vout).State);
        Assert.True(harness.Rows.ContainsKey((secondId, 0)));
    }

    /// <summary>The fee of a combined HTLC transaction: its inputs (the HTLC output equals output 0) minus its change.
    /// </summary>
    private static ulong FeeOf(Transaction tx, AnchorTestWallet wallet) =>
        tx.Inputs.Skip(1)
          .Select(i => wallet.FundingTransactions.First(f => f.GetHash() == i.PrevOut.Hash).Outputs[i.PrevOut.N])
          .Aggregate(0UL, (sum, o) => sum + (ulong)o.Value.Satoshi)
      - tx.Outputs.Skip(1).Aggregate(0UL, (sum, o) => sum + (ulong)o.Value.Satoshi);

    private static LocalCommitResolutionHarness CreateHarness(AnchorTestWallet wallet,
                                                              Action<RealSigningCommitmentPair> setup)
    {
        var harness = new LocalCommitResolutionHarness(setup, feeInputProvider: wallet, simpleTaproot: true);
        foreach (var funding in wallet.FundingTransactions)
            harness.AddKnownTransaction(funding);
        return harness;
    }
}
