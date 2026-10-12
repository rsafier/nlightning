using NBitcoin;
using NLightning.Integration.Tests.BOLT3.Mocks;
using NLightning.Integration.Tests.BOLT3.Vectors;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Builders;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Factories;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Outputs;
using Signers;

/// <summary>
/// BOLT 5 plan O5-T1: with <c>remote_revocation_basepoint_secret</c> and <c>x_local_per_commitment_secret</c> (node A's
/// commitment 42 treated as revoked, we are node B) a penalty spends the <c>to_local</c> (<c>&lt;revsig&gt; 1</c>) and
/// every HTLC output (<c>&lt;revsig&gt; &lt;revocationpubkey&gt;</c>) of each Appendix C commitment, batched with our
/// <c>to_remote</c> or one output per transaction, and the output of each Appendix C HTLC transaction (B5-REV-06).
/// Witness and input weights are checked against BOLT 5 §Penalty Transactions Weight Calculation.
/// </summary>
public class PenaltyTransactionBuilderTests
{
    private const uint FeeratePerKw = 1_000;

    private static readonly Secret s_secret = new(AppendixCSweepSigner.XLocalPerCommitmentSecret);
    private static readonly CompactPubKey s_revocationPubKey = Bolt3AppendixCVectors.NodeARevocationPubkey.ToBytes();

    public static TheoryData<string> AppendixCNames => Bolt3SpecVectors.AppendixCNames;

    [Fact]
    public void Given_AppendixCSecret_When_MultipliedByG_Then_ItIsTheRevokedCommitmentPoint()
    {
        // Assert: the secret the penalty uses belongs to the commitment on chain (Appendix C local_per_commitment_point)
        Assert.Equal((byte[])Bolt3TestCommitmentKeyDerivationService.LocalPerCommitmentPoint,
                     new Key(AppendixCSweepSigner.XLocalPerCommitmentSecret).PubKey.ToBytes());
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_RevokedAppendixCCommitment_When_BatchedPenaltySigned_Then_EveryOutputSpent(string name)
    {
        // Arrange: every output of node A's revoked commitment is ours (penalties + our to_remote, B5-REV-08 MAY)
        var (commitTx, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.GetAppendixC(name), CommitmentCase.Revoked);
        var (inputs, spent) = PenaltyInputs(commitTx, map, withToRemote: true);
        if (!HasPenalty(inputs, map))
            return;

        var builder = CreatePenaltyBuilder(out var sweepBuilder);

        // Act
        var unsigned = builder.BuildBatched(inputs, SweepTestKit.Destination, FeeratePerKw);
        var signed = sweepBuilder.Sign(unsigned, new AppendixCSweepSigner(asNodeB: true), ChannelId.Zero);

        // Assert
        Assert.Equal(map.Outputs.Count, inputs.Count);
        SweepTestKit.AssertAllInputsVerify(signed, spent);
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        Assert.Equal(0U, tx.LockTime.Value);
        Assert.All(tx.Inputs, i => Assert.Equal(SweepFeePolicy.RbfSequence, i.Sequence.Value));
        for (var i = 0; i < inputs.Count; i++)
        {
            var witness = tx.Inputs[i].WitScript;
            switch (inputs[i].SpendKind)
            {
                case SweepSpendKind.RevokedDelayedOutput:
                    Assert.Equal([1], witness[1]);
                    break;
                case SweepSpendKind.RevokedHtlc:
                    Assert.Equal((byte[])s_revocationPubKey, witness[1]);
                    break;
            }
        }

        // Estimated with 73-byte signatures: never below the signed weight
        Assert.InRange(unsigned.EstimatedWeight - SweepTestKit.Weight(signed), 0, 4 * inputs.Count);
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_RevokedAppendixCCommitment_When_SplitIntoSinglePenalties_Then_EachSpendsItsOutput(string name)
    {
        // Arrange: the security_delay split (B5-REV-08), one penalty per revoked output
        var (commitTx, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.GetAppendixC(name), CommitmentCase.Revoked);
        var (inputs, spent) = PenaltyInputs(commitTx, map, withToRemote: false);
        if (!HasPenalty(inputs, map))
            return;

        var builder = CreatePenaltyBuilder(out var sweepBuilder);
        var signer = new AppendixCSweepSigner(asNodeB: true);

        // Act: at the floor rate, so the smallest (546 sat) HTLC output still pays for its own penalty
        var split = builder.BuildSplit(inputs, SweepTestKit.Destination, 253);

        // Assert
        Assert.Equal(inputs.Count, split.Count);
        for (var i = 0; i < split.Count; i++)
        {
            var signed = sweepBuilder.Sign(split[i], signer, ChannelId.Zero);
            Assert.True(SweepTestKit.Verifies(signed, 0, spent[i], out var error), $"output {i}: {error}");
        }

        // And the single-output entry point builds the same transaction as a one-element split
        Assert.Equal(split[0].Transaction.RawTxBytes,
                     builder.BuildSingle(inputs[0], SweepTestKit.Destination, 253).Transaction.RawTxBytes);
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_RevokedAppendixCCommitment_When_Estimating_Then_WitnessWeightsAreBolt5s(string name)
    {
        // Arrange
        var (commitTx, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.GetAppendixC(name), CommitmentCase.Revoked);
        var (inputs, _) = PenaltyInputs(commitTx, map, withToRemote: false);
        if (!HasPenalty(inputs, map))
            return;

        var builder = CreatePenaltyBuilder(out var sweepBuilder);
        var signed = sweepBuilder.Sign(builder.BuildBatched(inputs, SweepTestKit.Destination, 253),
                                       new AppendixCSweepSigner(asNodeB: true), ChannelId.Zero);

        for (var i = 0; i < inputs.Count; i++)
        {
            // Act
            var estimate = SweepWeights.EstimateWitnessSize(inputs[i]);
            var withMaxSignature = MaxSignatureWitnessSize(inputs[i]);

            // Assert: the estimate is exactly the witness of a 73-byte signature; a real DER signature is 71-73 bytes
            // (shorter when r or s has leading zero bytes)
            Assert.Equal(withMaxSignature, estimate);
            Assert.InRange(estimate - SweepTestKit.WitnessSize(signed, i), 0, 4);

            var htlc = map.Outputs.Single(o => o.Vout == inputs[i].Vout).Htlc;
            if (htlc is null)
            {
                // to_local: BOLT 5's 160 assumes an 83-byte script (8-byte delay) and counts no byte for the "1"
                // element; the real script with the 2-byte delay 144 is 77 bytes, so the witness is 155
                Assert.Equal(155, estimate);
                Assert.True(estimate <= SweepWeights.ToLocalPenaltyWitness);
                Assert.True(SweepWeights.InputNonWitnessWeight + estimate <= SweepWeights.ToLocalPenaltyInputWeight);
            }
            else if (htlc.Value.Direction == HtlcDirection.Incoming)
            {
                // Node A offered it (an "offered" HTLC output, 133-byte script): exactly 243 / 407
                Assert.Equal(SweepWeights.OfferedHtlcPenaltyWitness, estimate);
                Assert.Equal(SweepWeights.OfferedHtlcPenaltyInputWeight, SweepWeights.InputNonWitnessWeight + estimate);
            }
            else
            {
                // Node A received it (an "accepted" HTLC output): BOLT 5's 249 assumes a 3-byte cltv_expiry push;
                // Appendix C's expiries (500-506) push 2 bytes, so the script is 138 bytes and the witness 248
                Assert.Equal(138, inputs[i].WitnessScript!.Length);
                Assert.Equal(SweepWeights.AcceptedHtlcPenaltyWitness - 1, estimate);
            }
        }
    }

    [Fact]
    public void Given_RealisticHeights_When_Estimating_Then_WitnessAndInputWeightsAreExactlyBolt5s()
    {
        // Arrange: Appendix C keys, a cltv_expiry at a mainnet height (3-byte push) and the largest to_self_delay
        var offered = new OfferedHtlcOutput(LightningMoney.Satoshis(10_000), 800_000, false,
                                            Bolt3AppendixCVectors.NodeAHtlcPubkey,
                                            Bolt3AppendixCVectors.Htlc2PaymentHash,
                                            Bolt3AppendixCVectors.NodeBHtlcPubkey,
                                            Bolt3AppendixCVectors.NodeARevocationPubkey).RedeemScript.ToBytes();
        var accepted = new ReceivedHtlcOutput(LightningMoney.Satoshis(10_000), 800_000, false,
                                              Bolt3AppendixCVectors.NodeAHtlcPubkey,
                                              Bolt3AppendixCVectors.Htlc0PaymentHash,
                                              Bolt3AppendixCVectors.NodeBHtlcPubkey,
                                              Bolt3AppendixCVectors.NodeARevocationPubkey).RedeemScript.ToBytes();
        var toLocal = new ToLocalOutput(LightningMoney.Satoshis(10_000), Bolt3AppendixCVectors.NodeADelayedPubkey,
                                        Bolt3AppendixCVectors.NodeARevocationPubkey, ushort.MaxValue).RedeemScript
                                                                                                    .ToBytes();
        SweepInput Penalty(SweepSpendKind kind, byte[] script) =>
            new(new byte[32], 0, 10_000, kind, script, PerCommitmentSecret: s_secret,
                WitnessPubKey: kind == SweepSpendKind.RevokedHtlc ? s_revocationPubKey : null);

        // Act
        var offeredWitness = SweepWeights.EstimateWitnessSize(Penalty(SweepSpendKind.RevokedHtlc, offered));
        var acceptedWitness = SweepWeights.EstimateWitnessSize(Penalty(SweepSpendKind.RevokedHtlc, accepted));
        var toLocalWitness = SweepWeights.EstimateWitnessSize(Penalty(SweepSpendKind.RevokedDelayedOutput, toLocal));

        // Assert: 243 / 249 exactly (inputs 407 / 413); to_local stays within 160 (324), BOLT 5's upper bound
        Assert.Equal(SweepWeights.OfferedHtlcPenaltyWitness, offeredWitness);
        Assert.Equal(SweepWeights.AcceptedHtlcPenaltyWitness, acceptedWitness);
        Assert.Equal(SweepWeights.OfferedHtlcPenaltyInputWeight, SweepWeights.InputNonWitnessWeight + offeredWitness);
        Assert.Equal(SweepWeights.AcceptedHtlcPenaltyInputWeight,
                     SweepWeights.InputNonWitnessWeight + acceptedWitness);
        Assert.Equal(156, toLocalWitness);
        Assert.True(SweepWeights.InputNonWitnessWeight + toLocalWitness <= SweepWeights.ToLocalPenaltyInputWeight);
        Assert.Equal(MaxSignatureWitnessSize(Penalty(SweepSpendKind.RevokedHtlc, accepted)), acceptedWitness);
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_RevokedPenaltyWithToRemote_When_Estimating_Then_WithinBolt5Bound(string name)
    {
        // Arrange
        var (commitTx, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.GetAppendixC(name), CommitmentCase.Revoked);
        var (inputs, _) = PenaltyInputs(commitTx, map, withToRemote: true);

        // Act
        var weight = SweepWeights.EstimateTransactionWeight(inputs, [SweepTestKit.Destination.Length]);

        // Assert: BOLT 5 bound = 4*53 + 2 (a P2WSH output; ours is P2WPKH) + 324/407/413 per input + 272 for to_remote
        var bound = SweepWeights.PenaltyTransactionOverheadWeight
                  + inputs.Sum(i => i.SpendKind switch
                    {
                        SweepSpendKind.PaymentToRemote => SweepWeights.ToRemoteInputWeight,
                        SweepSpendKind.RevokedDelayedOutput => SweepWeights.ToLocalPenaltyInputWeight,
                        _ => i.WitnessScript!.Length == 133
                                 ? SweepWeights.OfferedHtlcPenaltyInputWeight
                                 : SweepWeights.AcceptedHtlcPenaltyInputWeight
                    });
        Assert.True(weight <= bound, $"{weight} > {bound}");
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_CheaterHtlcTransactions_When_SecondLevelPenalized_Then_RevocationBranchSpends(string name)
    {
        // Arrange: the cheater (node A) published its HTLC-timeout/success transactions of the revoked commitment;
        // their outputs are to_local-shaped with the same revocation key (B5-REV-06)
        var vector = Bolt3SpecVectors.GetAppendixC(name);
        var builder = CreatePenaltyBuilder(out var sweepBuilder);
        var signer = new AppendixCSweepSigner(asNodeB: true);
        var witnessScript = new ToLocalOutput(LightningMoney.Zero, Bolt3AppendixCVectors.NodeADelayedPubkey,
                                              Bolt3AppendixCVectors.NodeARevocationPubkey,
                                              Bolt3AppendixCVectors.LocalDelay).RedeemScript.ToBytes();
        var inputs = new List<SweepInput>();
        var spent = new List<TxOut>();
        foreach (var (htlcTx, _) in SweepTestKit.HtlcTransactions(vector))
        {
            inputs.Add(SweepInputFactory.SecondLevelPenalty(SweepTestKit.TxIdOf(htlcTx),
                                                            (ulong)htlcTx.Outputs[0].Value.Satoshi, witnessScript,
                                                            s_secret));
            spent.Add(htlcTx.Outputs[0]);
        }

        if (inputs.Count == 0)
            return;

        // Act: one per output and all in one
        var batched = sweepBuilder.Sign(builder.BuildBatched(inputs, SweepTestKit.Destination, 253), signer,
                                        ChannelId.Zero);

        // Assert
        SweepTestKit.AssertAllInputsVerify(batched, spent);
        for (var i = 0; i < inputs.Count; i++)
        {
            var single = sweepBuilder.Sign(builder.BuildSingle(inputs[i], SweepTestKit.Destination, 253), signer,
                                           ChannelId.Zero);
            Assert.True(SweepTestKit.Verifies(single, 0, spent[i], out var error), error.ToString());
        }
    }

    [Fact]
    public void Given_NonPenaltyInput_When_BuildingPenalty_Then_Throws()
    {
        // Arrange: a delayed output of our own commitment is no penalty
        var (_, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[1], CommitmentCase.Revoked);
        var toLocal = map.Outputs.Single(o => o.Kind == OutputDescriptorKind.RevokedToLocal);
        var penalty = SweepInputFactory.Penalty(toLocal, map.OnChainTxId!.Value, s_secret, s_revocationPubKey);
        var delayed = new SweepInput(new byte[32], 0, 10_000, SweepSpendKind.DelayedOutput, [0x51], 144,
                                     PerCommitmentPoint: Bolt3TestCommitmentKeyDerivationService.LocalPerCommitmentPoint);
        var builder = CreatePenaltyBuilder(out _);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => builder.BuildBatched([penalty, delayed], SweepTestKit.Destination,
                                                                    FeeratePerKw));
    }

    [Fact]
    public void Given_OnlyToRemote_When_BuildingPenalty_Then_Throws()
    {
        // Arrange
        var (_, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[1], CommitmentCase.Revoked);
        var toRemote = SweepInputFactory.ToRemote(map.Outputs.Single(o => o.Kind == OutputDescriptorKind.PaymentToRemote),
                                                  map.OnChainTxId!.Value,
                                                  Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes());
        var builder = CreatePenaltyBuilder(out _);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => builder.BuildBatched([toRemote], SweepTestKit.Destination,
                                                                    FeeratePerKw));
        Assert.Throws<ArgumentException>(() => builder.BuildBatched([], SweepTestKit.Destination, FeeratePerKw));
    }

    [Fact]
    public void Given_UnrevokedOutput_When_CreatingPenaltyInput_Then_Throws()
    {
        // Arrange: a descriptor of our own commitment
        var (_, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[1], CommitmentCase.Local);
        var toLocal = map.Outputs.Single(o => o.Kind == OutputDescriptorKind.DelayedToLocal);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => SweepInputFactory.Penalty(toLocal, map.OnChainTxId!.Value, s_secret,
                                                                         s_revocationPubKey));
    }

    /// <summary>
    /// Three vectors leave node A (the funder) no output after the fee: only node B's to_remote exists, so there is
    /// nothing to penalize (checked, then the theory row ends).
    /// </summary>
    private static bool HasPenalty(IReadOnlyList<SweepInput> inputs, CommitmentOutputMap map)
    {
        if (inputs.Any(i => i.SpendKind.IsPenalty()))
            return true;

        Assert.Empty(map.UnmappedVouts);
        Assert.All(map.Outputs, o => Assert.Equal(OutputDescriptorKind.PaymentToRemote, o.Kind));
        return false;
    }

    private static PenaltyTransactionBuilder CreatePenaltyBuilder(out SweepTransactionBuilder sweepBuilder)
    {
        sweepBuilder = SweepTestKit.CreateBuilder();
        return new PenaltyTransactionBuilder(sweepBuilder);
    }

    private static (List<SweepInput> Inputs, List<TxOut> Spent) PenaltyInputs(Transaction commitTx,
                                                                             CommitmentOutputMap map,
                                                                             bool withToRemote)
    {
        var txId = map.OnChainTxId!.Value;
        var inputs = new List<SweepInput>();
        var spent = new List<TxOut>();
        foreach (var output in map.Outputs)
        {
            if (output.Kind == OutputDescriptorKind.PaymentToRemote)
            {
                if (!withToRemote)
                    continue;

                inputs.Add(SweepInputFactory.ToRemote(output, txId,
                                                      Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes()));
            }
            else
            {
                inputs.Add(SweepInputFactory.Penalty(output, txId, s_secret, s_revocationPubKey));
            }

            spent.Add(commitTx.Outputs[(int)output.Vout]);
        }

        return (inputs, spent);
    }

    private static int MaxSignatureWitnessSize(SweepInput input)
    {
        var witness = SweepTransactionBuilder.CreateWitness(input, new byte[73]);
        return new WitScript(witness).ToBytes().Length;
    }
}