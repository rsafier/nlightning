using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Crypto;
using NLightning.Integration.Tests.BOLT3;
using NLightning.Integration.Tests.BOLT3.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Builders;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Node.Options;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Builders;

/// <summary>
/// BOLT 5 plan O7-T3 (B5-HTX-02, NL-314): every BOLT 3 Appendix F (option_anchors) HTLC transaction, built byte for
/// byte as the spec signs it, is combined with a wallet input and a change output; the spec's own
/// <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> remote signature still spends the commitment's HTLC output next to our
/// <c>SIGHASH_ALL</c> signature over the combined transaction, and the wallet input spends its P2WPKH output.
/// </summary>
public class AnchorHtlcTransactionBuilderTests
{
    private const uint FeeratePerKw = 2_500;

    private static readonly Key s_walletKey = new(Enumerable.Repeat((byte)0x77, 32).ToArray());
    private static readonly byte[] s_walletScript = s_walletKey.PubKey.WitHash.ScriptPubKey.ToBytes();
    private static readonly byte[] s_changeScript =
        new Key(Enumerable.Repeat((byte)0x78, 32).ToArray()).PubKey.WitHash.ScriptPubKey.ToBytes();

    private readonly HtlcTransactionBuilder _builder = new(new OptionsWrapper<NodeOptions>(new NodeOptions()));

    public static TheoryData<string> AppendixFNames => Bolt3SpecVectors.AppendixFNames;

    [Theory]
    [MemberData(nameof(AppendixFNames))]
    public void Given_AppendixFHtlcTransaction_When_CombinedWithFeeInput_Then_EveryInputSpendsAndFeeIsPaid(string name)
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixF(name);
        var harness = new Bolt3VectorHarness(vector, true);
        var commitTx = Transaction.Parse(vector.CommitTxHex, Network.Main);
        var (_, models) = harness.BuildHtlcModels();
        Assert.Equal(vector.HtlcTxs.Count, models.Count);

        for (var i = 0; i < models.Count; i++)
        {
            var model = models[i];
            var expected = vector.HtlcTxs[i];
            var built = _builder.Build(model);
            var remoteSignature = new ECDSASignature(Convert.FromHexString(expected.RemoteSigHex)).ToCompact();
            var preimage = model.Type == HtlcTransactionType.Success
                               ? Bolt3VectorHarness.Preimages[model.SpentOutput.Htlc.Id]
                               : null;

            // The uncombined transaction is still the spec's, byte for byte
            var alone = _builder.AddWitness(model, built, remoteSignature,
                                            Bolt3VectorHarness.SignLocalHtlc(built).ToCompact(), preimage);
            Assert.Equal(expected.TxHex, Convert.ToHexString(alone.RawTxBytes).ToLowerInvariant());

            var feeInput = WalletInput(0x10 + i, 40_000);

            // Act
            var combined = _builder.AddFeeInputs(model, built, [feeInput], s_changeScript, FeeratePerKw);
            var localSignature = Bolt3VectorHarness.SignLocalHtlc(combined.BuildResult).ToCompact();
            var withHtlcWitness = _builder.AddWitness(model, combined.BuildResult, remoteSignature, localSignature,
                                                      preimage);
            var signed = SignWalletInputs(withHtlcWitness, combined.FeeInputs);

            // Assert: input and output 0 are the spec's HTLC pair; the wallet input and change follow
            var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
            var specTx = Transaction.Parse(expected.TxHex, Network.Main);
            Assert.Equal(2, tx.Inputs.Count);
            Assert.Equal(specTx.Inputs[0].PrevOut, tx.Inputs[0].PrevOut);
            Assert.Equal(1U, tx.Inputs[0].Sequence.Value);
            Assert.Equal(SweepFeePolicy.RbfSequence, tx.Inputs[1].Sequence.Value);
            Assert.Equal(specTx.Outputs[0].ToBytes(), tx.Outputs[0].ToBytes());
            Assert.Equal(specTx.LockTime, tx.LockTime);
            Assert.Equal(2U, tx.Version);
            Assert.Equal(new TxId(tx.GetHash().ToBytes()), signed.TxId);

            AssertVerifies(tx, 0, commitTx.Outputs[expected.OutputIndex], name);
            AssertVerifies(tx, 1, new TxOut(Money.Satoshis(feeInput.AmountSat), new Script(s_walletScript)), name);

            // The fee is what the inputs leave, at least the feerate over the signed weight
            var change = Assert.Single(tx.Outputs.Skip(1));
            Assert.Equal(s_changeScript, change.ScriptPubKey.ToBytes());
            Assert.Equal(combined.ChangeSat, (ulong)change.Value.Satoshi);
            Assert.Equal(feeInput.AmountSat - combined.ChangeSat, combined.FeeSat);
            var weight = Weight(tx);
            Assert.InRange(combined.EstimatedWeight - weight, 0, 8);
            Assert.True(combined.FeeSat >= SweepWeights.FeeSat(FeeratePerKw, weight));
        }
    }

    [Fact]
    public void Given_LocalSignatureOverTheHtlcTransactionAlone_When_Combined_Then_ItNoLongerVerifies()
    {
        // Arrange: our SIGHASH_ALL commits to the fee inputs, so a signature made before combining is refused
        var (model, built, commitTx, vector) = FirstAppendixFHtlc();
        var combined = _builder.AddFeeInputs(model, built, [WalletInput(1, 40_000)], s_changeScript, FeeratePerKw);
        var remoteSignature = new ECDSASignature(Convert.FromHexString(vector.RemoteSigHex)).ToCompact();
        var preimage = model.Type == HtlcTransactionType.Success
                           ? Bolt3VectorHarness.Preimages[model.SpentOutput.Htlc.Id]
                           : null;

        // Act
        var stale = _builder.AddWitness(model, combined.BuildResult, remoteSignature,
                                        Bolt3VectorHarness.SignLocalHtlc(built).ToCompact(), preimage);

        // Assert
        var tx = Transaction.Load(stale.RawTxBytes, Network.Main);
        Assert.False(tx.Inputs.AsIndexedInputs().First().VerifyScript(commitTx.Outputs[vector.OutputIndex],
                                                                         out _));
    }

    [Fact]
    public void Given_ChangeBelowDust_When_Combined_Then_NoChangeAndEverythingIsFee()
    {
        // Arrange: an input that pays the fee with less than a P2WPKH dust output (294 sat) left over
        var (model, built, _, _) = FirstAppendixFHtlc();
        var noChangeFee = SweepWeights.FeeSat(FeeratePerKw,
                                              _builder.EstimateAnchorBaseWeight(model, built, 0) - 4 * 9
                                            + AnchorFeeInput.P2WpkhInputWeight);
        var input = WalletInput(2, noChangeFee + 100);

        // Act
        var combined = _builder.AddFeeInputs(model, built, [input], s_changeScript, FeeratePerKw);

        // Assert
        Assert.Null(combined.ChangeSat);
        Assert.Equal(input.AmountSat, combined.FeeSat);
        Assert.Single(Transaction.Load(combined.BuildResult.Transaction.RawTxBytes, Network.Main).Outputs);
    }

    [Fact]
    public void Given_InputsBelowTheFee_When_Combined_Then_Throws()
    {
        // Arrange
        var (model, built, _, _) = FirstAppendixFHtlc();

        // Act / Assert
        Assert.Throws<ArgumentException>(() => _builder.AddFeeInputs(model, built, [WalletInput(3, 100)],
                                                                     s_changeScript, FeeratePerKw));
    }

    [Fact]
    public void Given_TwoFeeInputs_When_Combined_Then_BothFollowTheHtlcInputAndVerify()
    {
        // Arrange
        var (model, built, commitTx, vector) = FirstAppendixFHtlc();
        var inputs = new[] { WalletInput(4, 700), WalletInput(5, 900) };
        var remoteSignature = new ECDSASignature(Convert.FromHexString(vector.RemoteSigHex)).ToCompact();
        var preimage = model.Type == HtlcTransactionType.Success
                           ? Bolt3VectorHarness.Preimages[model.SpentOutput.Htlc.Id]
                           : null;

        // Act
        var combined = _builder.AddFeeInputs(model, built, inputs, s_changeScript, 253);
        var signed = SignWalletInputs(_builder.AddWitness(model, combined.BuildResult, remoteSignature,
                                                          Bolt3VectorHarness.SignLocalHtlc(combined.BuildResult)
                                                                            .ToCompact(), preimage),
                                      combined.FeeInputs);

        // Assert
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        Assert.Equal(3, tx.Inputs.Count);
        AssertVerifies(tx, 0, commitTx.Outputs[vector.OutputIndex], "two inputs");
        for (var i = 1; i < 3; i++)
            AssertVerifies(tx, i, new TxOut(Money.Satoshis(inputs[i - 1].AmountSat), new Script(s_walletScript)),
                           "two inputs");
    }

    [Fact]
    public void Given_InvalidFeeInputs_When_Combined_Then_Throws()
    {
        // Arrange
        var (model, built, _, _) = FirstAppendixFHtlc();
        var htlcOutPoint = Transaction.Load(built.Transaction.RawTxBytes, Network.Main).Inputs[0].PrevOut;
        var input = WalletInput(6, 40_000);
        var spendsTheHtlc = input with { TxId = htlcOutPoint.Hash.ToBytes(), Vout = htlcOutPoint.N };

        // Act / Assert: none, twice the same, the HTLC output itself, no weight, no script, no value
        Assert.Throws<ArgumentException>(() => _builder.AddFeeInputs(model, built, [], s_changeScript, FeeratePerKw));
        Assert.Throws<ArgumentException>(() => _builder.AddFeeInputs(model, built, [input, input], s_changeScript,
                                                                     FeeratePerKw));
        Assert.Throws<ArgumentException>(() => _builder.AddFeeInputs(model, built, [spendsTheHtlc], s_changeScript,
                                                                     FeeratePerKw));
        Assert.Throws<ArgumentException>(() => _builder.AddFeeInputs(model, built, [input with { InputWeight = 164 }],
                                                                     s_changeScript, FeeratePerKw));
        Assert.Throws<ArgumentException>(() => _builder.AddFeeInputs(model, built, [input with { ScriptPubKey = [] }],
                                                                     s_changeScript, FeeratePerKw));
        Assert.Throws<ArgumentException>(() => _builder.AddFeeInputs(model, built, [input with { AmountSat = 0 }],
                                                                     s_changeScript, FeeratePerKw));
        Assert.Throws<ArgumentException>(() => _builder.AddFeeInputs(model, built, [input], [], FeeratePerKw));
    }

    [Fact]
    public void Given_AlreadyCombined_When_CombinedAgain_Then_Throws()
    {
        // Arrange
        var (model, built, _, _) = FirstAppendixFHtlc();
        var combined = _builder.AddFeeInputs(model, built, [WalletInput(7, 40_000)], s_changeScript, FeeratePerKw);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => _builder.AddFeeInputs(model, combined.BuildResult,
                                                                     [WalletInput(8, 40_000)], s_changeScript,
                                                                     FeeratePerKw));
    }

    [Fact]
    public void Given_NonAnchorHtlcTransaction_When_Combined_Then_Throws()
    {
        // Arrange: a SIGHASH_ALL peer signature would no longer verify over added inputs
        var vector = Bolt3SpecVectors.AppendixC.First(v => v.HtlcTxs.Count > 0);
        var harness = new Bolt3VectorHarness(vector, false);
        var model = harness.BuildHtlcModels().HtlcTxs[0];
        var built = _builder.Build(model);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => _builder.AddFeeInputs(model, built, [WalletInput(9, 40_000)],
                                                                     s_changeScript, FeeratePerKw));
    }

    [Fact]
    public void Given_AnchorHtlc_When_EstimatingBaseWeight_Then_ItIsBolt3sWeightWithChange()
    {
        // Arrange: BOLT 3 expected weights with anchors (72-byte signatures): HTLC-timeout 666, HTLC-success 706
        var vector = Bolt3SpecVectors.AppendixF.First(v => v.HtlcTxs.Count >= 2);
        var models = new Bolt3VectorHarness(vector, true).BuildHtlcModels().HtlcTxs;

        foreach (var model in models)
        {
            var built = _builder.Build(model);

            // Act: without a change output, and with a 22-byte P2WPKH change (4 * 31 more)
            var withEmptyChange = _builder.EstimateAnchorBaseWeight(model, built, 0);
            var withChange = _builder.EstimateAnchorBaseWeight(model, built, 22);

            // Assert: an empty-script output adds 4 * 9; BOLT 3 counts 73-byte signatures too, so the HTLC-timeout
            // is its figure (adjusted for the script's cltv push), and BOLT 3's HTLC-success figure keeps 2 bytes of
            // slack over the witness it lists
            var withoutChange = withEmptyChange - 4 * 9;
            var scriptLength = ((byte[])built.SpentWitnessScript).Length;
            if (model.Type == HtlcTransactionType.Timeout)
                Assert.Equal(666 + scriptLength - 136, withoutChange);
            else
                Assert.InRange(706 + scriptLength - 142 - withoutChange, 0, 2);

            Assert.Equal(4 * 31, withChange - withoutChange);
        }
    }

    private (HtlcTransactionModel Model, HtlcTransactionBuildResult Built, Transaction CommitTx, Bolt3HtlcTxVector
        Vector) FirstAppendixFHtlc()
    {
        var vector = Bolt3SpecVectors.AppendixF.First(v => v.HtlcTxs.Count > 0);
        var model = new Bolt3VectorHarness(vector, true).BuildHtlcModels().HtlcTxs[0];
        return (model, _builder.Build(model), Transaction.Parse(vector.CommitTxHex, Network.Main), vector.HtlcTxs[0]);
    }

    private static AnchorFeeInput WalletInput(int tag, ulong amountSat) =>
        new(Enumerable.Repeat((byte)tag, 32).ToArray(), 1, amountSat, s_walletScript,
            AnchorFeeInput.P2WpkhInputWeight);

    /// <summary>What the wallet does: a P2WPKH <c>SIGHASH_ALL</c> witness on every fee input.</summary>
    private static SignedTransaction SignWalletInputs(SignedTransaction transaction,
                                                      IReadOnlyList<AnchorFeeInput> feeInputs)
    {
        var tx = Transaction.Load(transaction.RawTxBytes, Network.Main);
        for (var i = 0; i < feeInputs.Count; i++)
        {
            var spent = new TxOut(Money.Satoshis(feeInputs[i].AmountSat), new Script(feeInputs[i].ScriptPubKey));
            var hash = tx.GetSignatureHash(s_walletKey.PubKey.Hash.ScriptPubKey, i + 1, SigHash.All, spent,
                                           HashVersion.WitnessV0);
            var signature = new TransactionSignature(s_walletKey.Sign(hash), SigHash.All);
            tx.Inputs[i + 1].WitScript =
                PayToWitPubKeyHashTemplate.Instance.GenerateWitScript(signature, s_walletKey.PubKey);
        }

        return new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
    }

    private static void AssertVerifies(Transaction tx, int index, TxOut spent, string name) =>
        Assert.True(tx.Inputs.AsIndexedInputs().ElementAt(index).VerifyScript(spent, out var error),
                    $"{name}: input {index}: {error}");

    private static long Weight(Transaction tx) =>
        3L * tx.GetSerializedSize(TransactionOptions.None) + tx.GetSerializedSize();
}