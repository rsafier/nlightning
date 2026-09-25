using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Crypto;
using NLightning.Tests.Utils.Vectors;

#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.

namespace NLightning.Integration.Tests.BOLT3;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Models;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Protocol.Services;
using Mocks;

public class Bolt3IntegrationTests
{
    private static readonly Sha256 s_sha256 = new();

    private readonly CompactPubKey _emptyCompactPubKey =
        new([
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00
        ]);

    private readonly CommitmentNumber _commitmentNumber = new(Bolt3AppendixCVectors.NodeAPaymentBasepoint.ToBytes(),
                                                              Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes(),
                                                              s_sha256,
                                                              Bolt3AppendixCVectors.CommitmentNumber);

    private readonly FundingOutputInfo _fundingOutputInfo = new(Bolt3AppendixBVectors.FundingSatoshis,
                                                                Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                                Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes())
    {
        TransactionId = Bolt3AppendixBVectors.ExpectedTxId.ToBytes(),
        Index = 0
    };

    private Htlc? _offeredHtlc2;
    private Htlc? _offeredHtlc3;
    private Htlc? _offeredHtlc5;
    private Htlc? _offeredHtlc6;
    private Htlc? _receivedHtlc0;
    private Htlc? _receivedHtlc1;
    private Htlc? _receivedHtlc4;

    #region Appendix B Vectors

    [Fact]
    public void Given_Bolt3Specifications_When_CreatingFundingTransaction_Then_ShouldBeEqualToTestVector()
    {
        // Given - BOLT 3 Appendix B: 50 BTC P2PKH coinbase input, 10M sat funding, feerate 15000 sat/kw => 13920 sat fee
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
        var fundingTx = Transaction.Load(unsignedTransaction.RawTxBytes, Network.Main);
        // Sign the P2PKH input without low-R grinding, as the spec vector was generated
        var inputKey = Bolt3AppendixBVectors.InputSigningPrivKey;
        var sigHash = fundingTx.GetSignatureHash(inputTxOut.ScriptPubKey, 0, SigHash.All, inputTxOut,
                                                 HashVersion.Original);
        var inputSignature = inputKey.Sign(sigHash, new SigningOptions(SigHash.All, false));
        fundingTx.Inputs[0].ScriptSig =
            PayToPubkeyHashTemplate.Instance.GenerateScriptSig(inputSignature, inputKey.PubKey);

        // Then
        Assert.Equal(LightningMoney.Satoshis(fundingTx.Outputs[1].Value.Satoshi),
                     Bolt3AppendixBVectors.ExpectedChangeSatoshis);
        Assert.Equal(Bolt3AppendixBVectors.ExpectedTx.ToHex(), fundingTx.ToHex());
        Assert.Equal(Bolt3AppendixBVectors.ExpectedTxId, fundingTx.GetHash());
        Assert.Equal((ushort)0, fundingOutputInfo.Index);
    }

    #endregion

    #region Appendix C Vectors

    [Fact]
    public void Given_Bolt3Specifications_When_CreatingCommitmentTransaction_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false
        };
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(15_000), false);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature0.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature0.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx0, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith5HTLCsUntrimmed_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Zero, true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature1.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature1.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx1, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith7OutputsUntrimmed_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(647), true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature2.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature2.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx2, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith6OutputsUntrimmed_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false,
            DustLimitAmount = LightningMoney.Satoshis(546)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(648), true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature3.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature3.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx3, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith6OutputsUntrimmedMaxFeeRate_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false,
            DustLimitAmount = LightningMoney.Satoshis(546)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(2_069), true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature4.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature4.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx4, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith5OutputsUntrimmedMinFeeRate_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false,
            DustLimitAmount = LightningMoney.Satoshis(546)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(2_070), true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature5.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature5.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx5, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith5OutputsUntrimmedMaxFeeRate_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false,
            DustLimitAmount = LightningMoney.Satoshis(546)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(2_194), true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature6.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature6.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx6, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith4OutputsUntrimmedMinFeeRate_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false,
            DustLimitAmount = LightningMoney.Satoshis(546)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(2_195), true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature7.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature7.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx7, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith4OutputsUntrimmedMaxFeeRate_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false,
            DustLimitAmount = LightningMoney.Satoshis(546)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(3_702), true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature8.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature8.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx8, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith3OutputsUntrimmedMinFeeRate_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false,
            DustLimitAmount = LightningMoney.Satoshis(546)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(3_703), true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature9.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature9.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx9, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith3OutputsUntrimmedMaxFeeRate_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false,
            DustLimitAmount = LightningMoney.Satoshis(546)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(4_914), true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature10.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature10.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx10, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith2OutputsUntrimmedMinFeeRate_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false,
            DustLimitAmount = LightningMoney.Satoshis(546)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(4_915), true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature11.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature11.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx11, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith2OutputsUntrimmedMaxFeeRate_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false,
            DustLimitAmount = LightningMoney.Satoshis(546)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(9_651_180), true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature12.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature12.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx12, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith1OutputsUntrimmedMinFeeRate_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false,
            DustLimitAmount = LightningMoney.Satoshis(546)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(9_651_181), true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature13.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature13.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx13, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWithFeeGreaterThanFunderAmount_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false,
            DustLimitAmount = LightningMoney.Satoshis(546)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(9_651_936), true);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature14.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature14.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx14, unsignedTransaction);
    }

    [Fact]
    public void
        Given_Bolt3Specifications_When_CreatingCommitmentTransactionWith2SimilarOfferedHtlc_Then_ShouldBeEqualToTestVector()
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = false,
            DustLimitAmount = LightningMoney.Satoshis(546)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var bolt3CommitmentKeyDerivationService = new Bolt3TestCommitmentKeyDerivationService();
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(bolt3CommitmentKeyDerivationService,
                                                  testLightningSigner);
        List<Htlc> offeredHtlcs = [_offeredHtlc5!.Value, _offeredHtlc6!.Value];
        List<Htlc> receivedHtlcs = [_receivedHtlc1!.Value];
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(253), true, offeredHtlcs,
                                          receivedHtlcs);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, Bolt3AppendixCVectors.NodeBSignature15.ToCompact(),
                                             unsignedTransaction));
        var signature = testLightningSigner.SignChannelTransaction(ChannelId.Zero, unsignedTransaction);

        // Then
        Assert.Null(exception);
        Assert.Equal(Bolt3AppendixCVectors.NodeASignature15.ToCompact(), signature);
        AssertUnsignedTxEquals(Bolt3AppendixCVectors.ExpectedCommitTx15, unsignedTransaction);
    }

    private static void AssertUnsignedTxEquals(Transaction expectedSignedTx, SignedTransaction actualUnsignedTx)
    {
        // The spec vectors carry the 2-of-2 witness; the builder output is unsigned, so compare without witnesses.
        var expected = expectedSignedTx.Clone();
        foreach (var input in expected.Inputs)
            input.WitScript = WitScript.Empty;

        Assert.Equal(Convert.ToHexString(expected.ToBytes()), Convert.ToHexString(actualUnsignedTx.RawTxBytes));
        Assert.Equal(expected.GetHash().ToBytes(), (byte[])actualUnsignedTx.TxId);
    }

    private static Bolt3TestLightningSigner GetTestLightningSigner(NodeOptions nodeOptions)
    {
        var logger = new Mock<ILogger<LocalLightningSigner>>();
        var bolt3LightningSigner = new Bolt3TestLightningSigner(nodeOptions, logger.Object);
        bolt3LightningSigner.RegisterChannel(ChannelId.Zero,
                                             new ChannelSigningInfo(Bolt3AppendixBVectors.ExpectedTxId.ToBytes(),
                                                                    Bolt3AppendixBVectors.InputIndex,
                                                                    Bolt3AppendixBVectors.FundingSatoshis,
                                                                    Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                                    Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(),
                                                                    0));
        return bolt3LightningSigner;
    }

    private ChannelModel GetTestChannelModel(NodeOptions nodeOptions, LightningMoney feeRatePerKw, bool addHtlcs,
                                             List<Htlc>? overrideOfferedHtlcs = null,
                                             List<Htlc>? overrideReceivedHtlcs = null,
                                             bool optionAnchorOutputs = false, LightningMoney? localBalance = null,
                                             LightningMoney? remoteBalance = null)
    {
        var channelConfig = new ChannelConfig(LightningMoney.Zero, feeRatePerKw, LightningMoney.Zero,
                                              nodeOptions.DustLimitAmount, 0, LightningMoney.Zero, 0,
                                              optionAnchorOutputs,
                                              nodeOptions.DustLimitAmount, nodeOptions.ToSelfDelay, FeatureSupport.No);
        var localKeySet = new ChannelKeySetModel(0, Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                 _emptyCompactPubKey,
                                                 Bolt3AppendixCVectors.NodeAPaymentBasepoint.ToBytes(),
                                                 _emptyCompactPubKey, _emptyCompactPubKey, _emptyCompactPubKey);
        var remoteKeySet = new ChannelKeySetModel(0, Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(),
                                                  _emptyCompactPubKey,
                                                  Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes(),
                                                  _emptyCompactPubKey, _emptyCompactPubKey, _emptyCompactPubKey);

        var offeredHtlcs = addHtlcs
                               ? overrideOfferedHtlcs ?? [_offeredHtlc2!.Value, _offeredHtlc3!.Value]
                               : null;
        var receivedHtlcs = addHtlcs
                                ? overrideReceivedHtlcs ??
                                [
                                    _receivedHtlc0!.Value, _receivedHtlc1!.Value, _receivedHtlc4!.Value
                                ]
                                : null;

        return new ChannelModel(channelConfig, ChannelId.Zero, _commitmentNumber, _fundingOutputInfo, true, null, null,
                                localBalance ?? Bolt3AppendixCVectors.Tx0ToLocalMsat, localKeySet, 1, 0,
                                remoteBalance ?? Bolt3AppendixCVectors.ToRemoteMsat, remoteKeySet, 1,
                                Bolt3AppendixBVectors.RemotePubKey.ToBytes(), 0, ChannelState.V1Opening,
                                ChannelVersion.V1, offeredHtlcs, null, null, null, receivedHtlcs);
    }

    #endregion

    #region Appendix F Vectors

    private const string Nl061SkipReason =
        "NL-061: option_anchors commitment fee uses weight 1116 instead of 1124 and deducts only one 330 sat anchor from the funder";

    [Fact(Skip = Nl061SkipReason)]
    public void Given_Bolt3AnchorSpecifications_When_CreatingCommitmentTransactionWithNoHtlcs_Then_ShouldBeEqualToTestVector()
    {
        AssertAnchorCommitmentTx(LightningMoney.MilliSatoshis(7_000_000_000), LightningMoney.MilliSatoshis(3_000_000_000),
                                 546, 15_000, false, Bolt3AppendixFVectors.ExpectedCommitTx0,
                                 Bolt3AppendixFVectors.NodeBSignature0);
    }

    [Fact(Skip = Nl061SkipReason)]
    public void
        Given_Bolt3AnchorSpecifications_When_CreatingCommitmentTransactionWithSingleAnchor_Then_ShouldBeEqualToTestVector()
    {
        AssertAnchorCommitmentTx(LightningMoney.MilliSatoshis(10_000_000_000), LightningMoney.Zero, 546, 15_000, false,
                                 Bolt3AppendixFVectors.ExpectedCommitTx1, Bolt3AppendixFVectors.NodeBSignature1);
    }

    [Fact(Skip = Nl061SkipReason)]
    public void
        Given_Bolt3AnchorSpecifications_When_CreatingCommitmentTransactionWith7OutputsUntrimmed_Then_ShouldBeEqualToTestVector()
    {
        AssertAnchorCommitmentTx(LightningMoney.MilliSatoshis(6_988_000_000), LightningMoney.MilliSatoshis(3_000_000_000),
                                 546, 644, true, Bolt3AppendixFVectors.ExpectedCommitTx2,
                                 Bolt3AppendixFVectors.NodeBSignature2);
    }

    private void AssertAnchorCommitmentTx(LightningMoney localBalance, LightningMoney remoteBalance,
                                          ulong dustLimitSatoshis, ulong feeRatePerKw, bool addHtlcs,
                                          Transaction expectedTx, ECDSASignature remoteSignature)
    {
        // Given
        var nodeOptions = new NodeOptions
        {
            HasAnchorOutputs = true,
            DustLimitAmount = LightningMoney.Satoshis(dustLimitSatoshis)
        };
        GenerateHtlcs();
        var testLightningSigner = GetTestLightningSigner(nodeOptions);
        var commitmentTransactionModelFactory =
            new CommitmentTransactionModelFactory(new Bolt3TestCommitmentKeyDerivationService(), testLightningSigner);
        var channel = GetTestChannelModel(nodeOptions, LightningMoney.Satoshis(feeRatePerKw), addHtlcs,
                                          optionAnchorOutputs: true, localBalance: localBalance,
                                          remoteBalance: remoteBalance);
        var commitmentTransactionModel =
            commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local);
        var commitmentTransactionBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));

        // When
        var unsignedTransaction = commitmentTransactionBuilder.Build(commitmentTransactionModel);
        var exception = Record.Exception(() => testLightningSigner.ValidateSignature(
                                             ChannelId.Zero, remoteSignature.ToCompact(), unsignedTransaction));

        // Then
        AssertUnsignedTxEquals(expectedTx, unsignedTransaction);
        Assert.Null(exception);
    }

    #endregion

    #region Appendix C HTLC Transaction Vectors

    [Fact(Skip = "NL-056: HTLC-success / HTLC-timeout second-stage transactions are not implemented")]
    public void Given_Bolt3Specifications_When_CreatingHtlcTransactionsFor5HtlcsUntrimmed_Then_ShouldBeEqualToTestVector()
    {
        // When NL-056 lands: build the HTLC-success/HTLC-timeout transactions for ExpectedCommitTx1 (feerate 0,
        // to_self_delay 144, local delayed/revocation keys from Bolt3AppendixCVectors) and assert they equal the
        // vectors below with witnesses stripped, then validate the remote HTLC signatures.
        Assert.Fail("No HTLC second-stage transaction builder exists yet (NL-056)");
    }

    [Fact]
    public void Given_Bolt3HtlcTransactionVectors_When_Inspected_Then_TheySpendTheMatchingCommitmentOutputs()
    {
        // Given - guards the vector data that the NL-056 test will consume
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

    private void GenerateHtlcs()
    {
        _offeredHtlc2 = new Htlc(LightningMoney.Satoshis(2_000), null, HtlcDirection.Outgoing, 502, 2, 0,
                                 Bolt3AppendixCVectors.Htlc2PaymentHash, HtlcState.Offered);
        _offeredHtlc3 = new Htlc(LightningMoney.Satoshis(3_000), null, HtlcDirection.Outgoing, 503, 3, 0,
                                 Bolt3AppendixCVectors.Htlc3PaymentHash, HtlcState.Offered);
        _offeredHtlc5 = new Htlc(LightningMoney.Satoshis(5_000), null, HtlcDirection.Outgoing, 506, 5, 0,
                                 Bolt3AppendixCVectors.Htlc5PaymentHash, HtlcState.Offered);
        _offeredHtlc6 = new Htlc(LightningMoney.MilliSatoshis(5_000_001), null!, HtlcDirection.Outgoing, 505, 6, 0,
                                 Bolt3AppendixCVectors.Htlc6PaymentHash, HtlcState.Offered);

        _receivedHtlc0 = new Htlc(LightningMoney.Satoshis(1_000), null, HtlcDirection.Incoming, 500, 0, 0,
                                  Bolt3AppendixCVectors.Htlc0PaymentHash, HtlcState.Offered);
        _receivedHtlc1 = new Htlc(LightningMoney.Satoshis(2_000), null, HtlcDirection.Incoming, 501, 1, 0,
                                  Bolt3AppendixCVectors.Htlc1PaymentHash, HtlcState.Offered);
        _receivedHtlc4 = new Htlc(LightningMoney.Satoshis(4_000), null, HtlcDirection.Incoming, 504, 4, 0,
                                  Bolt3AppendixCVectors.Htlc4PaymentHash, HtlcState.Offered);
    }
}