using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Crypto;
using NLightning.Tests.Utils.Vectors;

#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.

namespace NLightning.Integration.Tests.BOLT3;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Protocol.Services;
using Vectors;

public class Bolt3IntegrationTests
{
    #region Appendix B Vectors

    [Fact]
    public void Given_Bolt3Specifications_When_CreatingFundingTransaction_Then_ShouldBeEqualToTestVector()
    {
        // Given - BOLT 3 Appendix B: 50 BTC P2PKH coinbase input, 10M sat funding, feerate 15000 sat/kw.
        // The builder is handed the spec fee; the Then block re-derives it from the signed tx weight.
        // AddressType has no P2PKH member and the builder does not use it, so the UTXO is tagged P2Wpkh. The input is
        // signed by hand below (legacy sighash, no low-R grinding): the wallet signing path is not covered here.
        var feeRatePerKw = LightningMoney.Satoshis(15_000);
        var nodeOptions = Options.Create(new NodeOptions());
        var builder = new FundingTransactionBuilder(nodeOptions, new Mock<IServiceProvider>().Object,
                                                    new Mock<ILogger<FundingTransactionBuilder>>().Object);
        var inputTxOut = Bolt3AppendixBVectors.InputTx.Outputs[Bolt3AppendixBVectors.InputIndex];
        var utxo = new UtxoModel(Bolt3AppendixBVectors.InputTxId.ToBytes(), Bolt3AppendixBVectors.InputIndex,
                                 LightningMoney.Satoshis(inputTxOut.Value.Satoshi), 0, 0, false, AddressType.P2Wpkh);
        var changeAddress = Bolt3AppendixBVectors.ChangeScript.GetDestinationAddress(Network.Main)!.ToString();
        var fundingOutputInfo = new FundingOutputInfo(Bolt3AppendixBVectors.FundingSatoshis,
                                                      Bolt3AppendixBVectors.LocalPubKey.ToBytes(),
                                                      Bolt3AppendixBVectors.RemotePubKey.ToBytes());
        var fundingTransactionModel =
            new FundingTransactionModel([utxo], fundingOutputInfo, LightningMoney.Satoshis(13_920))
            {
                ChangeAddress = new WalletAddressModel(AddressType.P2Wpkh, 0, true, changeAddress)
            };

        // When
        var unsignedTransaction = builder.Build(fundingTransactionModel);
        var fundingTx = Transaction.Load(unsignedTransaction.Transaction.RawTxBytes, Network.Main);
        // Sign the P2PKH input without low-R grinding, as the spec vector was generated
        var inputKey = Bolt3AppendixBVectors.InputSigningPrivKey;
        var sigHash = fundingTx.GetSignatureHash(inputTxOut.ScriptPubKey, 0, SigHash.All, inputTxOut,
                                                 HashVersion.Original);
        var inputSignature = inputKey.Sign(sigHash, new SigningOptions(SigHash.All, false));
        fundingTx.Inputs[0].ScriptSig =
            PayToPubkeyHashTemplate.Instance.GenerateScriptSig(inputSignature, inputKey.PubKey);

        // Then
        // fee = feerate_per_kw * weight / 1000, computed as in FundingTransactionModelFactory (msat = weight * sat/kw)
        var weight = (ulong)(fundingTx.GetSerializedSize(TransactionOptions.None) * 3 + fundingTx.GetSerializedSize());
        Assert.Equal(928UL, weight);
        Assert.Equal(LightningMoney.Satoshis(13_920), LightningMoney.MilliSatoshis(weight * (ulong)feeRatePerKw.Satoshi));
        Assert.Equal(LightningMoney.Satoshis(fundingTx.Outputs[1].Value.Satoshi),
                     Bolt3AppendixBVectors.ExpectedChangeSatoshis);
        Assert.Equal(Bolt3AppendixBVectors.ExpectedTx.ToHex(), fundingTx.ToHex());
        Assert.Equal(Bolt3AppendixBVectors.ExpectedTxId, fundingTx.GetHash());
        Assert.Equal((ushort)0, unsignedTransaction.FundingOutputIndex);
    }

    #endregion

    #region Appendix C and F Vectors

    // The Appendix C commitment and HTLC vectors are in Bolt3CommitmentVectorTests and Bolt3HtlcTxVectorTests, and the
    // Appendix F (anchors) vectors in Bolt3AnchorVectorTests. All are built from the spec's balances and HTLC set.

    #endregion

    #region Appendix C HTLC Transaction Vectors

    [Fact]
    public void Given_Bolt3Specifications_When_CreatingHtlcTransactionsFor5HtlcsUntrimmed_Then_ShouldBeEqualToTestVector()
    {
        // Given - "commitment tx with all five HTLCs untrimmed (minimum feerate)": feerate 0, to_self_delay 144
        Transaction[] expectedHtlcTxs =
        [
            Bolt3AppendixCVectors.ExpectedCommitTx1Htlc0SuccessTx,
            Bolt3AppendixCVectors.ExpectedCommitTx1Htlc2TimeoutTx,
            Bolt3AppendixCVectors.ExpectedCommitTx1Htlc1SuccessTx,
            Bolt3AppendixCVectors.ExpectedCommitTx1Htlc3TimeoutTx,
            Bolt3AppendixCVectors.ExpectedCommitTx1Htlc4SuccessTx
        ];
        var vector = Bolt3SpecVectors.GetAppendixC("commitment tx with all five HTLCs untrimmed (minimum feerate)");
        var harness = new Bolt3VectorHarness(vector, false);

        // When
        var (commitment, htlcModels) = harness.BuildHtlcModels();
        var signedHtlcTxs = htlcModels.Select((model, i) =>
        {
            var built = harness.HtlcBuilder.Build(model);
            var remoteSignature = new ECDSASignature(Convert.FromHexString(vector.HtlcTxs[i].RemoteSigHex));
            var localSignature = Bolt3VectorHarness.SignLocalHtlc(built);
            var preimage = model.Type == HtlcTransactionType.Success
                               ? Bolt3VectorHarness.Preimages[model.SpentOutput.Htlc.Id]
                               : null;
            return harness.HtlcBuilder.AddWitness(model, built, remoteSignature.ToCompact(),
                                                  localSignature.ToCompact(), preimage);
        }).ToList();

        // Then
        Assert.Equal(Bolt3AppendixCVectors.ExpectedCommitTx1.GetHash().ToBytes(), (byte[])commitment.Transaction.TxId);
        Assert.Equal(expectedHtlcTxs.Length, signedHtlcTxs.Count);
        for (var i = 0; i < expectedHtlcTxs.Length; i++)
            Assert.Equal(expectedHtlcTxs[i].ToHex(), Convert.ToHexString(signedHtlcTxs[i].RawTxBytes).ToLowerInvariant());
    }

    [Fact]
    public void Given_Bolt3HtlcTransactionVectors_When_Inspected_Then_TheySpendTheMatchingCommitmentOutputs()
    {
        // Given - guards the vector data the NL-056 test consumes
        Transaction[] htlcTxs =
        [
            Bolt3AppendixCVectors.ExpectedCommitTx1Htlc0SuccessTx,
            Bolt3AppendixCVectors.ExpectedCommitTx1Htlc2TimeoutTx,
            Bolt3AppendixCVectors.ExpectedCommitTx1Htlc1SuccessTx,
            Bolt3AppendixCVectors.ExpectedCommitTx1Htlc3TimeoutTx,
            Bolt3AppendixCVectors.ExpectedCommitTx1Htlc4SuccessTx
        ];
        var commitTx = Bolt3AppendixCVectors.ExpectedCommitTx1;

        // When / Then - feerate 0: each HTLC tx spends output i in full, with the BOLT 3 version and sequence
        for (var i = 0; i < htlcTxs.Length; i++)
        {
            var htlcTx = htlcTxs[i];
            Assert.Equal(2U, htlcTx.Version);
            Assert.Single(htlcTx.Inputs);
            Assert.Equal(commitTx.GetHash(), htlcTx.Inputs[0].PrevOut.Hash);
            Assert.Equal((uint)i, htlcTx.Inputs[0].PrevOut.N);
            Assert.Equal(0U, (uint)htlcTx.Inputs[0].Sequence);
            Assert.Equal(commitTx.Outputs[i].Value, htlcTx.Outputs[0].Value);
        }
    }

    #endregion

    #region Appendix D Vectors

    #region Generation Tests

    [Fact]
    public void Given_Bolt3Specifications_When_GeneratingFromSeed0FinalNode_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());

        // When
        var result = keyDerivationService.GeneratePerCommitmentSecret(Bolt3AppendixDVectors.Seed0FinalNode,
                                                                      Bolt3AppendixDVectors.I0FinalNode);

        // Then
        Assert.Equal(Bolt3AppendixDVectors.ExpectedOutput0FinalNode, result);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_GeneratingFromSeedFFFinalNode_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());

        // When
        var result = keyDerivationService.GeneratePerCommitmentSecret(Bolt3AppendixDVectors.SeedFfFinalNode,
                                                                      Bolt3AppendixDVectors.IFfFinalNode);

        // Then
        Assert.Equal(Bolt3AppendixDVectors.ExpectedOutputFfFinalNode, result);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_GeneratingFromSeedFFAlternateBits1_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());

        // When
        var result = keyDerivationService.GeneratePerCommitmentSecret(Bolt3AppendixDVectors.SeedFfAlternateBits1,
                                                                      Bolt3AppendixDVectors.IFfAlternateBits1);

        // Then
        Assert.Equal(Bolt3AppendixDVectors.ExpectedOutputFfAlternateBits1, result);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_GeneratingFromSeedFFAlternateBits2_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());

        // When
        var result = keyDerivationService.GeneratePerCommitmentSecret(Bolt3AppendixDVectors.SeedFfAlternateBits2,
                                                                      Bolt3AppendixDVectors.IFfAlternateBits2);

        // Then
        Assert.Equal(Bolt3AppendixDVectors.ExpectedOutputFfAlternateBits2, result);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_GeneratingFromSeed01LastNonTrivialNode_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());

        // When
        var result = keyDerivationService.GeneratePerCommitmentSecret(Bolt3AppendixDVectors.Seed01LastNonTrivialNode,
                                                                      Bolt3AppendixDVectors.I01LastNonTrivialNode);

        // Then
        Assert.Equal(Bolt3AppendixDVectors.ExpectedOutput01LastNonTrivialNode, result);
    }

    #endregion

    #region Storage Tests

    [Fact]
    public void Given_Bolt3Specifications_When_InsertingSecretsInCorrectSequence_Then_ShouldSucceed()
    {
        // Given
        using var storage = new SecretStorageService();

        // When
        var result1 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, Bolt3AppendixDVectors.StorageIndexMax);
        var result2 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret1, Bolt3AppendixDVectors.StorageIndexMax - 1);
        var result3 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret2, Bolt3AppendixDVectors.StorageIndexMax - 2);
        var result4 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret3, Bolt3AppendixDVectors.StorageIndexMax - 3);
        var result5 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret4, Bolt3AppendixDVectors.StorageIndexMax - 4);
        var result6 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret5, Bolt3AppendixDVectors.StorageIndexMax - 5);
        var result7 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret6, Bolt3AppendixDVectors.StorageIndexMax - 6);
        var result8 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret7, Bolt3AppendixDVectors.StorageIndexMax - 7);

        // Then
        Assert.True(result1);
        Assert.True(result2);
        Assert.True(result3);
        Assert.True(result4);
        Assert.True(result5);
        Assert.True(result6);
        Assert.True(result7);
        Assert.True(result8);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_InsertingSecrets1InWrongSequence_Then_ShouldFail()
    {
        // Given
        using var storage = new SecretStorageService();

        var result1 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret8, Bolt3AppendixDVectors.StorageIndexMax);

        // When
        var result2 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret1, Bolt3AppendixDVectors.StorageIndexMax - 1);

        // Then
        Assert.True(result1);
        Assert.False(result2);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_InsertingSecrets2InWrongSequence_Then_ShouldFail()
    {
        // Given
        using var storage = new SecretStorageService();

        var result1 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret8, Bolt3AppendixDVectors.StorageIndexMax);
        var result2 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret9, Bolt3AppendixDVectors.StorageIndexMax - 1);
        var result3 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret2, Bolt3AppendixDVectors.StorageIndexMax - 2);

        // When
        var result4 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret3, Bolt3AppendixDVectors.StorageIndexMax - 3);

        // Then
        Assert.True(result1);
        Assert.True(result2);
        Assert.True(result3);
        Assert.False(result4);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_InsertingSecrets3InWrongSequence_Then_ShouldFail()
    {
        // Given
        using var storage = new SecretStorageService();

        var result1 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, Bolt3AppendixDVectors.StorageIndexMax);
        var result2 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret1, Bolt3AppendixDVectors.StorageIndexMax - 1);
        var result3 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret10, Bolt3AppendixDVectors.StorageIndexMax - 2);

        // When
        var result4 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret3, Bolt3AppendixDVectors.StorageIndexMax - 3);

        // Then
        Assert.True(result1);
        Assert.True(result2);
        Assert.True(result3);
        Assert.False(result4);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_InsertingSecrets4InWrongSequence_Then_ShouldFail()
    {
        // Given
        using var storage = new SecretStorageService();

        var result1 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret8, Bolt3AppendixDVectors.StorageIndexMax);
        var result2 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret9, Bolt3AppendixDVectors.StorageIndexMax - 1);
        var result3 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret10, Bolt3AppendixDVectors.StorageIndexMax - 2);
        var result4 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret11, Bolt3AppendixDVectors.StorageIndexMax - 3);
        var result5 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret4, Bolt3AppendixDVectors.StorageIndexMax - 4);
        var result6 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret5, Bolt3AppendixDVectors.StorageIndexMax - 5);
        var result7 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret6, Bolt3AppendixDVectors.StorageIndexMax - 6);

        // When
        var result8 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret7, Bolt3AppendixDVectors.StorageIndexMax - 7);

        // Then
        Assert.True(result1);
        Assert.True(result2);
        Assert.True(result3);
        Assert.True(result4);
        Assert.True(result5);
        Assert.True(result6);
        Assert.True(result7);
        Assert.False(result8);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_InsertingSecrets5InWrongSequence_Then_ShouldFail()
    {
        // Given
        using var storage = new SecretStorageService();

        var result1 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, Bolt3AppendixDVectors.StorageIndexMax);
        var result2 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret1, Bolt3AppendixDVectors.StorageIndexMax - 1);
        var result3 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret2, Bolt3AppendixDVectors.StorageIndexMax - 2);
        var result4 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret3, Bolt3AppendixDVectors.StorageIndexMax - 3);
        var result5 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret12, Bolt3AppendixDVectors.StorageIndexMax - 4);

        // When
        var result6 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret5, Bolt3AppendixDVectors.StorageIndexMax - 5);

        // Then
        Assert.True(result1);
        Assert.True(result2);
        Assert.True(result3);
        Assert.True(result4);
        Assert.True(result5);
        Assert.False(result6);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_InsertingSecrets6InWrongSequence_Then_ShouldFail()
    {
        // Given
        using var storage = new SecretStorageService();

        var result1 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, Bolt3AppendixDVectors.StorageIndexMax);
        var result2 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret1, Bolt3AppendixDVectors.StorageIndexMax - 1);
        var result3 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret2, Bolt3AppendixDVectors.StorageIndexMax - 2);
        var result4 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret3, Bolt3AppendixDVectors.StorageIndexMax - 3);
        var result5 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret12, Bolt3AppendixDVectors.StorageIndexMax - 4);
        var result6 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret13, Bolt3AppendixDVectors.StorageIndexMax - 5);
        var result7 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret6, Bolt3AppendixDVectors.StorageIndexMax - 6);

        // When
        var result8 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret7, Bolt3AppendixDVectors.StorageIndexMax - 7);

        // Then
        Assert.True(result1);
        Assert.True(result2);
        Assert.True(result3);
        Assert.True(result4);
        Assert.True(result5);
        Assert.True(result6);
        Assert.True(result7);
        Assert.False(result8);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_InsertingSecrets7InWrongSequence_Then_ShouldFail()
    {
        // Given
        using var storage = new SecretStorageService();

        var result1 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, Bolt3AppendixDVectors.StorageIndexMax);
        var result2 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret1, Bolt3AppendixDVectors.StorageIndexMax - 1);
        var result3 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret2, Bolt3AppendixDVectors.StorageIndexMax - 2);
        var result4 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret3, Bolt3AppendixDVectors.StorageIndexMax - 3);
        var result5 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret4, Bolt3AppendixDVectors.StorageIndexMax - 4);
        var result6 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret5, Bolt3AppendixDVectors.StorageIndexMax - 5);
        var result7 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret14, Bolt3AppendixDVectors.StorageIndexMax - 6);

        // When
        var result8 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret7, Bolt3AppendixDVectors.StorageIndexMax - 7);

        // Then
        Assert.True(result1);
        Assert.True(result2);
        Assert.True(result3);
        Assert.True(result4);
        Assert.True(result5);
        Assert.True(result6);
        Assert.True(result7);
        Assert.False(result8);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_InsertingSecrets8InWrongSequence_Then_ShouldFail()
    {
        // Given
        using var storage = new SecretStorageService();

        var result1 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, Bolt3AppendixDVectors.StorageIndexMax);
        var result2 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret1, Bolt3AppendixDVectors.StorageIndexMax - 1);
        var result3 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret2, Bolt3AppendixDVectors.StorageIndexMax - 2);
        var result4 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret3, Bolt3AppendixDVectors.StorageIndexMax - 3);
        var result5 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret4, Bolt3AppendixDVectors.StorageIndexMax - 4);
        var result6 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret5, Bolt3AppendixDVectors.StorageIndexMax - 5);
        var result7 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret6, Bolt3AppendixDVectors.StorageIndexMax - 6);

        // When
        var result8 = storage
           .InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret15, Bolt3AppendixDVectors.StorageIndexMax - 7);

        // Then
        Assert.True(result1);
        Assert.True(result2);
        Assert.True(result3);
        Assert.True(result4);
        Assert.True(result5);
        Assert.True(result6);
        Assert.True(result7);
        Assert.False(result8);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_DerivingOldSecret_Then_ShouldDeriveCorrectly()
    {
        // Given
        using var storage = new SecretStorageService();

        // Insert a valid secret with a known index
        storage.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret0, Bolt3AppendixDVectors.StorageIndexMax);
        storage.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret1, Bolt3AppendixDVectors.StorageIndexMax - 1);
        storage.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret2, Bolt3AppendixDVectors.StorageIndexMax - 2);

        // When
        var derivedSecret = storage.DeriveOldSecret(Bolt3AppendixDVectors.StorageIndexMax);

        // Then
        Assert.Equal(Bolt3AppendixDVectors.StorageExpectedSecret0, derivedSecret);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_DerivingUnknownSecret_Then_ShouldThrowException()
    {
        // Given
        using var storage = new SecretStorageService();

        // Insert a valid secret with a known index
        storage.InsertSecret(Bolt3AppendixDVectors.StorageExpectedSecret1, Bolt3AppendixDVectors.StorageIndexMax);

        // When/Then
        // Cannot derive a secret with a higher index
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = storage.DeriveOldSecret(Bolt3AppendixDVectors.StorageIndexMax - 1);
        });
    }

    [Fact]
    public void Given_Bolt3Specifications_When_StoringAndDeriving48Secrets_Then_ShouldWorkCorrectly()
    {
        // Given
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());
        using var storage = new SecretStorageService();

        // Insert secrets with different number of trailing zeros
        for (var i = 0; i < 48; i++)
        {
            var index = (Bolt3AppendixDVectors.StorageIndexMax - 1) & ~((1UL << i) - 1);
            var secret =
                keyDerivationService.GeneratePerCommitmentSecret(Bolt3AppendixDVectors.StorageCorrectSeed, index);

            storage.InsertSecret(secret, index);
        }

        // When/Then
        // We should be able to derive any secret in the range
        for (var i = 1U; i < 20; i++)
        {
            var index = Bolt3AppendixDVectors.StorageIndexMax - i;
            var expectedSecret = keyDerivationService.GeneratePerCommitmentSecret(
                Bolt3AppendixDVectors.StorageCorrectSeed, index);

            var derivedSecret = storage.DeriveOldSecret(index);

            Assert.Equal(expectedSecret, derivedSecret);
        }
    }

    #endregion

    #endregion

    #region Appendix E Vectors

    [Fact]
    public void Given_Bolt3Specifications_When_DerivingPubKey_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());
        var basepoint = Convert.FromHexString("036d6caac248af96f6afa7f904f550253a0f3ef3f5aa2fe6838a95b216691468e2");
        var perCommitmentPoint =
            Convert.FromHexString("025f7117a78150fe2ef97db7cfc83bd57b2e2c0d0dd25eaf467a4a1c2a45ce1486");
        var expectedLocalPubkey =
            Convert.FromHexString("0235f2dbfaa89b57ec7b055afe29849ef7ddfeb1cefdb9ebdc43f5494984db29e5");

        // When
        var result = keyDerivationService.DerivePublicKey(basepoint, perCommitmentPoint);

        // Then
        Assert.Equal(expectedLocalPubkey, result);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_DerivingPrivateKey_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());
        var baseSecretBytes = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        // var basepointSecret = new Key(baseSecretBytes);
        var perCommitmentPoint =
            Convert.FromHexString("025f7117a78150fe2ef97db7cfc83bd57b2e2c0d0dd25eaf467a4a1c2a45ce1486");
        var expectedPrivkeyBytes = Convert
           .FromHexString("cbced912d3b21bf196a766651e436aff192362621ce317704ea2f75d87e7be0f");

        // When
        var result = keyDerivationService.DerivePrivateKey(baseSecretBytes, perCommitmentPoint);

        // Then
        Assert.Equal(expectedPrivkeyBytes, result);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_DerivingRevocationPubKey_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());
        var revocationBasepoint =
            Convert.FromHexString("036d6caac248af96f6afa7f904f550253a0f3ef3f5aa2fe6838a95b216691468e2");
        var perCommitmentPoint =
            Convert.FromHexString("025f7117a78150fe2ef97db7cfc83bd57b2e2c0d0dd25eaf467a4a1c2a45ce1486");
        var expectedRevocationPubkey =
            Convert.FromHexString("02916e326636d19c33f13e8c0c3a03dd157f332f3e99c317c141dd865eb01f8ff0");

        // When
        var result = keyDerivationService.DeriveRevocationPubKey(revocationBasepoint, perCommitmentPoint);

        // Then
        Assert.Equal(expectedRevocationPubkey, result);
    }

    [Fact]
    public void Given_Bolt3Specifications_When_DerivingRevocationPrivKey_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var keyDerivationService = new KeyDerivationService(new Secp256K1Math());
        var baseSecretBytes = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        // var revocationBasepointSecret = new Key(baseSecretBytes);
        var perCommitmentSecretBytes = Convert
           .FromHexString("1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100");
        // var perCommitmentSecret = new Key(perCommitmentSecretBytes);
        var expectedRevocationPrivkeyBytes = Convert
           .FromHexString("d09ffff62ddb2297ab000cc85bcb4283fdeb6aa052affbc9dddcf33b61078110");

        // When
        var result = keyDerivationService.DeriveRevocationPrivKey(baseSecretBytes, perCommitmentSecretBytes);

        // Then
        Assert.Equal(expectedRevocationPrivkeyBytes, result);
    }

    #endregion
}