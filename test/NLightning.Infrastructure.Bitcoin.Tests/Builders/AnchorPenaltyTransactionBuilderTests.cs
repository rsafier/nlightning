using NBitcoin;
using NLightning.Integration.Tests.BOLT3.Vectors;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Builders;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Factories;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Outputs;
using Signers;

/// <summary>
/// BOLT 5 plan O7-T3 penalties on option_anchors commitments (BOLT 3 Appendix F, node A's commitment 42 treated as
/// revoked, we are node B): every revoked <c>to_local</c> and HTLC output (the anchor HTLC scripts with their
/// <c>1 OP_CSV</c> branch) is spent through the revocation key, our <c>to_remote</c> through its CSV-1 script
/// (<c>nSequence</c> 1, also inside a batch), and the second-level outputs of the cheater's HTLC transactions whether
/// they stand alone (vout 0) or were batched behind other outputs (the output paired with the spending input).
/// </summary>
public class AnchorPenaltyTransactionBuilderTests
{
    private const uint FeeratePerKw = 253;

    private static readonly Secret s_secret = new(AppendixCSweepSigner.XLocalPerCommitmentSecret);
    private static readonly CompactPubKey s_revocationPubKey = Bolt3AppendixCVectors.NodeARevocationPubkey.ToBytes();

    public static TheoryData<string> AppendixFNames => Bolt3SpecVectors.AppendixFNames;

    [Theory]
    [MemberData(nameof(AppendixFNames))]
    public void Given_RevokedAnchorCommitment_When_BatchedAndSplit_Then_EveryOutputOfOursIsSpent(string name)
    {
        // Arrange
        var (commitTx, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.GetAppendixF(name), CommitmentCase.Revoked,
                                                        hasAnchors: true);
        var txId = map.OnChainTxId!.Value;
        var inputs = new List<SweepInput>();
        var spent = new List<TxOut>();
        foreach (var output in map.Outputs)
        {
            var input = output.Kind switch
            {
                OutputDescriptorKind.PaymentToRemote =>
                    SweepInputFactory.ToRemote(output, txId, Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes()),
                OutputDescriptorKind.RevokedToLocal or OutputDescriptorKind.RevokedHtlc =>
                    SweepInputFactory.Penalty(output, txId, s_secret, s_revocationPubKey),
                _ => null // the anchors: not ours to penalize (O7-T2 sweeps them)
            };
            if (input is null)
                continue;

            inputs.Add(input);
            spent.Add(commitTx.Outputs[(int)output.Vout]);
        }

        if (!inputs.Any(i => i.SpendKind.IsPenalty()))
            return; // node A has no output left in this vector

        var sweepBuilder = SweepTestKit.CreateBuilder();
        var builder = new PenaltyTransactionBuilder(sweepBuilder);
        var signer = new AppendixCSweepSigner(asNodeB: true);

        // Act
        var batched = sweepBuilder.Sign(builder.BuildBatched(inputs, SweepTestKit.Destination, FeeratePerKw), signer,
                                        ChannelId.Zero);
        var penalties = inputs.Where(i => i.SpendKind.IsPenalty()).ToList();
        var split = builder.BuildSplit(penalties, SweepTestKit.Destination, FeeratePerKw);

        // Assert
        SweepTestKit.AssertAllInputsVerify(batched, spent);
        var tx = Transaction.Load(batched.RawTxBytes, Network.Main);
        for (var i = 0; i < inputs.Count; i++)
        {
            // Anchors to_remote is <remotepubkey> OP_CHECKSIGVERIFY 1 OP_CSV: sequence 1 even in the penalty
            if (inputs[i].SpendKind == SweepSpendKind.PaymentToRemote)
                Assert.Equal(1U, tx.Inputs[i].Sequence.Value);
        }

        Assert.Equal(penalties.Count, split.Count);
        for (var i = 0; i < split.Count; i++)
        {
            var single = sweepBuilder.Sign(split[i], signer, ChannelId.Zero);
            Assert.True(SweepTestKit.Verifies(single, 0, spent[inputs.IndexOf(penalties[i])], out var error),
                        $"{name} penalty {i}: {error}");
        }

        // Every HTLC output of the revoked anchors commitment is penalized
        Assert.Equal(map.Outputs.Count(o => o.Kind == OutputDescriptorKind.RevokedHtlc),
                     penalties.Count(p => p.SpendKind == SweepSpendKind.RevokedHtlc));
    }

    [Theory]
    [MemberData(nameof(AppendixFNames))]
    public void Given_CheatersAnchorHtlcTransactions_When_BatchedByTheCheater_Then_SecondLevelPenaltiesSpendEachOutput(
        string name)
    {
        // Arrange: with anchors the cheater may batch its HTLC transactions (SIGHASH_SINGLE|ANYONECANPAY pairs input i
        // with output i), so a second-level output can sit at any index: here each vector HTLC transaction's output is
        // put at index 1 of a transaction behind another output, and at index 0 of the spec's own transaction
        var vector = Bolt3SpecVectors.GetAppendixF(name);
        var witnessScript = new ToLocalOutput(LightningMoney.Zero, Bolt3AppendixCVectors.NodeADelayedPubkey,
                                              Bolt3AppendixCVectors.NodeARevocationPubkey,
                                              Bolt3AppendixCVectors.LocalDelay).RedeemScript.ToBytes();
        var sweepBuilder = SweepTestKit.CreateBuilder();
        var builder = new PenaltyTransactionBuilder(sweepBuilder);
        var signer = new AppendixCSweepSigner(asNodeB: true);

        foreach (var (htlcTx, _) in SweepTestKit.HtlcTransactions(vector))
        {
            var output = htlcTx.Outputs[0];
            var batchedParent = Transaction.Create(Network.Main);
            batchedParent.Inputs.Add(new OutPoint(uint256.One, 0));
            batchedParent.Outputs.Add(new TxOut(Money.Satoshis(1_000), new Script(SweepTestKit.Destination)));
            batchedParent.Outputs.Add(output);

            var alone = SweepInputFactory.SecondLevelPenalty(SweepTestKit.TxIdOf(htlcTx),
                                                             (ulong)output.Value.Satoshi, witnessScript, s_secret);
            var behind = SweepInputFactory.SecondLevelPenalty(SweepTestKit.TxIdOf(batchedParent),
                                                              (ulong)output.Value.Satoshi, witnessScript, s_secret)
                         with
            { Vout = 1 };

            // Act
            var fromAlone = sweepBuilder.Sign(builder.BuildSingle(alone, SweepTestKit.Destination, FeeratePerKw),
                                              signer, ChannelId.Zero);
            var fromBatched = sweepBuilder.Sign(builder.BuildSingle(behind, SweepTestKit.Destination, FeeratePerKw),
                                                signer, ChannelId.Zero);

            // Assert
            Assert.True(SweepTestKit.Verifies(fromAlone, 0, output, out var error), $"{name}: {error}");
            Assert.True(SweepTestKit.Verifies(fromBatched, 0, output, out error), $"{name}: {error}");
            var spentOutPoint = Transaction.Load(fromBatched.RawTxBytes, Network.Main).Inputs[0].PrevOut;
            Assert.Equal(new OutPoint(batchedParent.GetHash(), 1), spentOutPoint);
        }
    }
}