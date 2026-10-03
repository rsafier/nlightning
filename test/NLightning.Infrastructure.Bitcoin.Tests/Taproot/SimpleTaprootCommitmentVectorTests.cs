using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Bitcoin.Taproot;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Money;

/// <summary>
/// The three commitment transaction vectors of bolt-simple-taproot.md (no HTLC; five HTLCs; two of them trimmed at a
/// 2,500 sat dust limit) built from their balances, feerate, dust limit and HTLCs through the production factories and
/// builders: txid, every output, locktime and sequence, the fee; then, with the vector's aggregated MuSig2 witness, the
/// whole transaction byte for byte. Every HTLC resolution transaction is built, the vector's remote BIP 340 signature
/// verified over our sighash, our signature (zero aux randomness) reproduced and the signed transaction compared byte
/// for byte and executed against the HTLC output it spends.
/// </summary>
public class SimpleTaprootCommitmentVectorTests
{
    private static readonly byte[] s_zeroAuxRandomness = new byte[32];

    public static TheoryData<int> Cases => new(0, 1, 2);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Given_AVectorCase_When_BuildingTheCommitment_Then_TheTxIdAndEveryOutputMatch(int index)
    {
        // Arrange
        var vectorCase = SimpleTaprootVectors.Transactions[index];
        var harness = new SimpleTaprootVectorHarness(vectorCase);
        var expected = Transaction.Parse(vectorCase.ExpectedCommitmentTxHex, Network.Main);

        // Act
        var (model, built, _) = harness.Build();
        var tx = Transaction.Load(built.Transaction.RawTxBytes, Network.Main);

        // Assert
        Assert.Equal(CommitmentFormat.SimpleTaproot, model.Format);
        Assert.True(model.HasAnchors);
        Assert.Equal(expected.GetHash(), tx.GetHash());
        Assert.Equal(expected.GetHash().ToBytes(), (byte[])built.Transaction.TxId);
        Assert.Equal(expected.Version, tx.Version);
        Assert.Equal(expected.LockTime, tx.LockTime);
        Assert.Single(tx.Inputs);
        Assert.Equal(expected.Inputs[0].PrevOut, tx.Inputs[0].PrevOut);
        Assert.Equal(SimpleTaprootVectors.FundingTransaction.GetHash(), tx.Inputs[0].PrevOut.Hash);
        Assert.Equal(expected.Inputs[0].Sequence, tx.Inputs[0].Sequence);
        Assert.Equal(expected.Outputs.Count, tx.Outputs.Count);
        for (var i = 0; i < expected.Outputs.Count; i++)
        {
            Assert.Equal(expected.Outputs[i].Value, tx.Outputs[i].Value);
            Assert.Equal(expected.Outputs[i].ScriptPubKey, tx.Outputs[i].ScriptPubKey);
        }

        // The base fee is funding - outputs - the trimmed HTLCs (whose value goes to fees as well)
        var trimmedSat = vectorCase.Htlcs.Where(h => h.AmountMsat / 1000 < vectorCase.DustLimitSatoshis)
                                   .Sum(h => (long)(h.AmountMsat / 1000));
        var outputsSat = tx.Outputs.Sum(o => o.Value.Satoshi);
        Assert.Equal((long)SimpleTaprootVectors.FundingSatoshis - outputsSat - trimmedSat, model.Fee.Satoshi);
        Assert.Equal(vectorCase.Htlcs.Count(h => h.AmountMsat / 1000 >= vectorCase.DustLimitSatoshis),
                     built.HtlcOutputsInTxOrder.Count);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Given_AVectorCommitment_When_AddingTheVectorsWitness_Then_TheTransactionIsByteExactAndValid(int index)
    {
        // Arrange: the witness is the aggregated MuSig2 signature (lane T0 reproduces it from the vector's nonces)
        var vectorCase = SimpleTaprootVectors.Transactions[index];
        var expected = Transaction.Parse(vectorCase.ExpectedCommitmentTxHex, Network.Main);
        var (_, built, _) = new SimpleTaprootVectorHarness(vectorCase).Build();
        var fundingTxOut = SimpleTaprootVectorHarness.FundingTxOut;

        // Act
        var tx = Transaction.Load(built.Transaction.RawTxBytes, Network.Main);
        var unsignedHex = tx.ToHex();
        tx.Inputs[0].WitScript = expected.Inputs[0].WitScript;
        var sigHash = TaprootSignatures.ComputeFundingKeySpendSigHash(
            built.Transaction, LightningMoney.Satoshis(fundingTxOut.Value.Satoshi),
            fundingTxOut.ScriptPubKey.ToBytes());

        // Assert: our unsigned bytes are the vector without its witness, and with it the vector byte for byte
        var withoutWitness = expected.Clone();
        withoutWitness.Inputs[0].WitScript = WitScript.Empty;
        Assert.Equal(withoutWitness.ToHex(), unsignedHex);
        Assert.Equal(vectorCase.ExpectedCommitmentTxHex, tx.ToHex());

        // The witness is one 64-byte key-path signature (SIGHASH_DEFAULT) over our BIP 341 key-path sighash
        var witness = expected.Inputs[0].WitScript.Pushes.ToArray();
        Assert.Single(witness);
        Assert.Equal(64, witness[0].Length);
        var fundingKey = new TaprootPubKey(fundingTxOut.ScriptPubKey.ToBytes()[2..]);
        Assert.True(fundingKey.VerifySignature(new uint256(sigHash), new SchnorrSignature(witness[0])));
        Assert.Null(tx.CreateValidator([fundingTxOut]).ValidateInput(0).Error);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Given_AVectorCase_When_BuildingEveryHtlcResolution_Then_EachIsByteExactAndValid(int index)
    {
        // Arrange
        var vectorCase = SimpleTaprootVectors.Transactions[index];
        var harness = new SimpleTaprootVectorHarness(vectorCase);
        var (_, built, htlcTxs) = harness.Build();
        var commitment = Transaction.Load(built.Transaction.RawTxBytes, Network.Main);
        var localHtlcKey = SimpleTaprootVectorHarness.LocalHtlcKey;
        var remoteHtlcPubKey = SimpleTaprootVectorHarness.RemoteHtlcKey.PubKey;

        // Assert: one resolution per untrimmed HTLC, in commitment output order
        Assert.Equal(vectorCase.HtlcDescs.Count, htlcTxs.Count);
        for (var i = 0; i < htlcTxs.Count; i++)
        {
            var htlcModel = htlcTxs[i];
            var desc = vectorCase.HtlcDescs[i];
            var expected = Transaction.Parse(desc.ResolutionTxHex, Network.Main);
            var isSuccess = htlcModel.Type == HtlcTransactionType.Success;

            // Act: build, check the remote signature, sign as the vectors do (zero aux) and assemble
            var htlcBuilt = harness.HtlcBuilder.Build(htlcModel);
            var remoteSigHash = TaprootSignatures.ComputeHtlcSigHash(htlcBuilt,
                                                                     TaprootSignatures.CounterpartyHtlcSigHash,
                                                                     Network.Main);
            var localSigHash = TaprootSignatures.ComputeHtlcSigHash(htlcBuilt, TaprootSignatures.HolderHtlcSigHash,
                                                                    Network.Main);
            var localSignature = TaprootSignatures.Sign(localHtlcKey, localSigHash, s_zeroAuxRandomness);
            var preimage = isSuccess ? harness.PreimageOf(htlcModel.SpentOutput) : null;
            var signed = harness.HtlcBuilder.AddWitness(htlcModel, htlcBuilt, desc.RemoteSignature, localSignature,
                                                        preimage);
            var tx = Transaction.Load(signed.RawTxBytes, Network.Main);

            // The transaction: version 2, the HTLC output at sequence 1, zero fee, cltv_expiry or 0 as locktime
            Assert.True(htlcModel.IsSimpleTaproot);
            Assert.Equal(expected.GetHash(), Transaction.Load(htlcBuilt.Transaction.RawTxBytes, Network.Main).GetHash());
            Assert.Equal(2u, tx.Version);
            Assert.Equal(new Sequence(1), tx.Inputs[0].Sequence);
            Assert.Equal(new OutPoint(commitment.GetHash(), built.HtlcOutputsInTxOrder[i].Vout), tx.Inputs[0].PrevOut);
            Assert.Equal(isSuccess ? 0u : htlcModel.SpentOutput.CltvExpiry, tx.LockTime.Value);
            Assert.Equal(LightningMoney.Zero, htlcModel.Fee);
            Assert.Equal(htlcModel.SpentOutput.Amount.Satoshi, tx.Outputs[0].Value.Satoshi);

            // The remote signature verifies only as SIGHASH_SINGLE|SIGHASH_ANYONECANPAY, ours is the vector's
            Assert.True(TaprootSignatures.Verify(remoteHtlcPubKey, remoteSigHash, desc.RemoteSignature));
            Assert.False(TaprootSignatures.Verify(remoteHtlcPubKey, localSigHash, desc.RemoteSignature));
            var witness = expected.Inputs[0].WitScript.Pushes.ToArray();
            Assert.Equal(witness[1], localSignature);
            Assert.Equal(0x83, witness[0][64]);

            // The whole signed transaction, byte for byte, and valid against the commitment output it spends
            Assert.Equal(desc.ResolutionTxHex, tx.ToHex());
            Assert.Null(tx.CreateValidator([commitment.Outputs[built.HtlcOutputsInTxOrder[i].Vout]])
                          .ValidateInput(0).Error);
        }
    }

    [Fact]
    public void Given_TheTrimmedCase_When_Building_Then_OnlyHtlcsAtOrAboveTheDustLimitHaveOutputs()
    {
        // Arrange: dust 2,500 sat; zero-fee HTLC transactions, so trimming depends on the dust limit alone
        var vectorCase = SimpleTaprootVectors.Transactions[2];

        // Act
        var (_, built, _) = new SimpleTaprootVectorHarness(vectorCase).Build();

        // Assert
        Assert.Equal(2500UL, vectorCase.DustLimitSatoshis);
        Assert.Equal([3_000_000UL, 4_000_000UL],
                     built.HtlcOutputsInTxOrder.Select(h => h.Output.Amount.MilliSatoshi).Order());
        Assert.All(built.HtlcOutputsInTxOrder, h => Assert.IsAssignableFrom<HtlcOutputInfo>(h.Output));
    }
}