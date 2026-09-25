using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Builders;

using Bitcoin.Builders;
using Bitcoin.Builders.Interfaces;
using Bitcoin.Outputs;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;

public class HtlcTransactionBuilderTests
{
    private static readonly byte[] s_commitmentTxId = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private readonly HtlcTransactionBuilder _builder = new(new OptionsWrapper<NodeOptions>(new NodeOptions()));

    [Fact]
    public void Given_TimeoutModel_When_Build_Then_Bolt3FieldsAreSet()
    {
        // Arrange
        var model = CreateModel(HtlcTransactionType.Timeout, false, 505);

        // Act
        var result = _builder.Build(model);

        // Assert
        var tx = Transaction.Load(result.Transaction.RawTxBytes, Network.Main);
        Assert.Equal(2U, tx.Version);
        Assert.Equal(505U, (uint)tx.LockTime);
        var input = Assert.Single(tx.Inputs);
        Assert.Equal(new uint256(s_commitmentTxId), input.PrevOut.Hash);
        Assert.Equal(3U, input.PrevOut.N);
        Assert.Equal(0U, (uint)input.Sequence);
        var output = Assert.Single(tx.Outputs);
        var expectedOutput = new HtlcResolutionOutput(LightningMoney.Satoshis(4_000),
                                                      Bolt3AppendixCVectors.NodeADelayedPubkey,
                                                      Bolt3AppendixCVectors.NodeARevocationPubkey, 144);
        Assert.Equal(expectedOutput.ScriptPubKey, output.ScriptPubKey);
        Assert.Equal(Money.Satoshis(4_000), output.Value);
        Assert.Equal(LightningMoney.Satoshis(5_000), result.SpentAmount);
        Assert.Equal(tx.GetHash().ToBytes(), (byte[])result.Transaction.TxId);
    }

    [Fact]
    public void Given_AnchorsModel_When_Build_Then_SequenceOneAndSpentScriptHasCsv1()
    {
        // Arrange
        var model = CreateModel(HtlcTransactionType.Success, true, 0);

        // Act
        var result = _builder.Build(model);

        // Assert
        var tx = Transaction.Load(result.Transaction.RawTxBytes, Network.Main);
        Assert.Equal(1U, (uint)tx.Inputs[0].Sequence);
        Assert.Equal(0U, (uint)tx.LockTime);
        var script = new Script((byte[])result.SpentWitnessScript).ToString();
        Assert.EndsWith("1 OP_CSV OP_DROP OP_ENDIF", script);
    }

    [Fact]
    public void Given_Anchors_When_AddWitness_Then_RemoteSigIsSingleAnyoneCanPayAndLocalSigIsAll()
    {
        // Arrange
        var model = CreateModel(HtlcTransactionType.Timeout, true, 505);
        var built = _builder.Build(model);
        var signature = CreateSignature();

        // Act
        var signed = _builder.AddWitness(model, built, signature, signature);

        // Assert - 0 <remotesig> <localsig> <> <witnessScript>
        var witness = Transaction.Load(signed.RawTxBytes, Network.Main).Inputs[0].WitScript;
        Assert.Equal(5, witness.PushCount);
        Assert.Empty(witness[0]);
        Assert.Equal(0x83, witness[1][^1]);
        Assert.Equal(0x01, witness[2][^1]);
        Assert.Empty(witness[3]);
        Assert.Equal((byte[])built.SpentWitnessScript, witness[4]);
    }

    [Fact]
    public void Given_NoAnchorsSuccess_When_AddWitness_Then_BothSigsAreAllAndPreimageIsPushed()
    {
        // Arrange
        var model = CreateModel(HtlcTransactionType.Success, false, 0);
        var built = _builder.Build(model);
        var signature = CreateSignature();

        // Act
        var signed = _builder.AddWitness(model, built, signature, signature, Bolt3AppendixCVectors.Htlc4Preimage);

        // Assert
        var witness = Transaction.Load(signed.RawTxBytes, Network.Main).Inputs[0].WitScript;
        Assert.Equal(0x01, witness[1][^1]);
        Assert.Equal(0x01, witness[2][^1]);
        Assert.Equal(Bolt3AppendixCVectors.Htlc4Preimage, witness[3]);
        Assert.Equal(built.Transaction.TxId, signed.TxId);
    }

    [Fact]
    public void Given_SuccessWithoutPreimage_When_AddWitness_Then_Throws()
    {
        // Arrange
        var model = CreateModel(HtlcTransactionType.Success, false, 0);
        var built = _builder.Build(model);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => _builder.AddWitness(model, built, CreateSignature(),
                                                                   CreateSignature()));
    }

    [Fact]
    public void Given_TimeoutWithPreimage_When_AddWitness_Then_Throws()
    {
        // Arrange
        var model = CreateModel(HtlcTransactionType.Timeout, false, 505);
        var built = _builder.Build(model);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => _builder.AddWitness(model, built, CreateSignature(),
                                                                   CreateSignature(), new byte[32]));
    }

    [Fact]
    public void Given_BitcoinInfrastructure_When_ResolvingHtlcTransactionBuilder_Then_ReturnsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<NodeOptions>>(new OptionsWrapper<NodeOptions>(new NodeOptions()));
        services.AddBitcoinInfrastructure();
        using var provider = services.BuildServiceProvider();

        // Act
        var first = provider.GetService<IHtlcTransactionBuilder>();
        var second = provider.GetService<IHtlcTransactionBuilder>();

        // Assert
        Assert.IsType<HtlcTransactionBuilder>(first);
        Assert.Same(first, second);
    }

    private static CompactSignature CreateSignature()
    {
        var key = new Key(Enumerable.Repeat((byte)9, 32).ToArray());
        return key.Sign(uint256.One).MakeCanonical().ToCompact();
    }

    private static HtlcTransactionModel CreateModel(HtlcTransactionType type, bool hasAnchors, uint lockTime)
    {
        var htlc = new Htlc(LightningMoney.Satoshis(5_000), null!, HtlcDirection.Outgoing, 505, 5, 0,
                            Bolt3AppendixCVectors.Htlc5PaymentHash, HtlcState.Offered);
        var localHtlcKey = Bolt3AppendixCVectors.NodeAHtlcPubkey.ToBytes();
        var remoteHtlcKey = Bolt3AppendixCVectors.NodeBHtlcPubkey.ToBytes();
        var revocationKey = Bolt3AppendixCVectors.NodeARevocationPubkey.ToBytes();
        HtlcOutputInfo output = type == HtlcTransactionType.Timeout
                                    ? new OfferedHtlcOutputInfo(htlc, localHtlcKey, remoteHtlcKey, revocationKey)
                                    : new ReceivedHtlcOutputInfo(htlc, localHtlcKey, remoteHtlcKey, revocationKey);
        return new HtlcTransactionModel(type, s_commitmentTxId, 3, output, hasAnchors, LightningMoney.Satoshis(1_000),
                                        LightningMoney.Satoshis(4_000), lockTime, hasAnchors ? 1U : 0U,
                                        revocationKey, Bolt3AppendixCVectors.NodeADelayedPubkey.ToBytes(), 144);
    }
}