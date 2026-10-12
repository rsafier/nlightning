using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Builders;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Constants;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// BOLT 5 plan O7-T2: <see cref="AnchorChildTransactionBuilder"/> (the CPFP child of our commitment and the anchor
/// sweep after 16 blocks) and <see cref="LocalLightningSigner.SignAnchorInput"/>, checked with NBitcoin's script
/// interpreter against the BOLT 3 anchor script (<c>&lt;funding_pubkey&gt; OP_CHECKSIG OP_IFDUP OP_NOTIF OP_16
/// OP_CHECKSEQUENCEVERIFY OP_ENDIF</c>).
/// </summary>
public class AnchorChildTransactionBuilderTests
{
    private static readonly ChannelId s_channelId = ChannelId.Zero;
    private static readonly CompactPubKey s_ourFundingPubKey = Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes();
    private static readonly CompactPubKey s_theirFundingPubKey = Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes();
    private static readonly Key s_walletKey = new(Enumerable.Repeat((byte)0x42, 32).ToArray());
    private static readonly Key s_walletKey2 = new(Enumerable.Repeat((byte)0x43, 32).ToArray());
    private static readonly byte[] s_changeScript = new Key(Enumerable.Repeat((byte)0x44, 32).ToArray())
                                                    .PubKey.WitHash.ScriptPubKey.ToBytes();

    private readonly AnchorChildTransactionBuilder _builder = new();

    [Fact]
    public void Given_FundingPubKey_When_GettingAnchorScript_Then_ItIsTheBolt3AnchorScript()
    {
        // Act
        var script = _builder.GetAnchorWitnessScript(s_ourFundingPubKey);

        // Assert: 21 <pubkey> OP_CHECKSIG OP_IFDUP OP_NOTIF OP_16 OP_CHECKSEQUENCEVERIFY OP_ENDIF
        var expected = "21" + Convert.ToHexString(s_ourFundingPubKey) + "ac736460b268";
        Assert.Equal(expected, Convert.ToHexString(script), ignoreCase: true);
        Assert.Equal(40, script.Length);
        Assert.Equal(new Script(script).WitHash.ScriptPubKey.ToBytes(),
                     _builder.GetAnchorScriptPubKey(s_ourFundingPubKey));
    }

    [Fact]
    public void Given_CommitmentWithBothAnchors_When_FindingAnchors_Then_EachKeyGetsItsOwnOutput()
    {
        // Arrange
        var commitment = CommitmentWithAnchors(out var ourVout, out var theirVout);

        // Act
        var ours = _builder.FindAnchorOutput(commitment.ToBytes(), s_ourFundingPubKey);
        var theirs = _builder.FindAnchorOutput(commitment.ToBytes(), s_theirFundingPubKey);
        var none = _builder.FindAnchorOutput(commitment.ToBytes(), s_walletKey.PubKey.ToBytes());

        // Assert
        Assert.Equal(ourVout, ours);
        Assert.Equal(theirVout, theirs);
        Assert.Null(none);
    }

    [Fact]
    public void Given_AnchorScriptWithAnotherAmount_When_FindingAnchor_Then_NotFound()
    {
        // Arrange: the anchor script paying 331 sat is not a BOLT 3 anchor
        var tx = Transaction.Create(Network.Main);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(new TxOut(Money.Satoshis(331), new Script(_builder.GetAnchorScriptPubKey(s_ourFundingPubKey))));

        // Act / Assert
        Assert.Null(_builder.FindAnchorOutput(tx.ToBytes(), s_ourFundingPubKey));
    }

    [Fact]
    public void Given_AnchorAndWalletInputs_When_BuildingChild_Then_ShapeIsReplaceableWithOneChangeOutput()
    {
        // Arrange
        var commitment = CommitmentWithAnchors(out var ourVout, out _);
        var anchor = new AnchorOutpoint(commitment.GetHash().ToBytes(), ourVout, s_ourFundingPubKey);
        var inputs = new[] { WalletInput(s_walletKey, 1, 50_000), WalletInput(s_walletKey2, 2, 30_000) };

        // Act
        var child = _builder.BuildChild(anchor, inputs, s_changeScript, 5_000);

        // Assert
        var tx = Transaction.Load(child.Transaction.RawTxBytes, Network.Main);
        Assert.Equal(2u, tx.Version);
        Assert.Equal(LockTime.Zero, tx.LockTime);
        Assert.Equal(3, tx.Inputs.Count);
        Assert.Equal(new OutPoint(commitment.GetHash(), ourVout), tx.Inputs[0].PrevOut);
        Assert.All(tx.Inputs, i => Assert.Equal(0xFFFFFFFDu, (uint)i.Sequence));
        var output = Assert.Single(tx.Outputs);
        Assert.Equal(330 + 50_000 + 30_000 - 5_000, output.Value.Satoshi);
        Assert.Equal(s_changeScript, output.ScriptPubKey.ToBytes());
        Assert.Equal(0, child.AnchorInputIndex);
        Assert.Equal(5_000UL, child.FeeSat);
        Assert.Equal((ulong)output.Value.Satoshi, child.ChangeSat);
    }

    [Fact]
    public void Given_ChildSignedBySignerAndWallet_When_Verified_Then_EveryInputSpendsItsOutput()
    {
        // Arrange
        var signer = CreateSigner();
        var commitment = CommitmentWithAnchors(out var ourVout, out _);
        var anchor = new AnchorOutpoint(commitment.GetHash().ToBytes(), ourVout, s_ourFundingPubKey);
        var inputs = new[] { WalletInput(s_walletKey, 1, 50_000), WalletInput(s_walletKey2, 2, 30_000) };
        var child = _builder.BuildChild(anchor, inputs, s_changeScript, 5_000);

        // Act: the anchor with the funding key through the signer, the wallet inputs as a wallet would
        var anchorSignature = signer.SignAnchorInput(s_channelId, child.Transaction, 0,
                                                     TransactionConstants.AnchorOutputAmount);
        var walletSigned = SignWalletInputs(child.Transaction.RawTxBytes, inputs);
        var signed = _builder.AddAnchorWitness(walletSigned, 0, anchorSignature, s_ourFundingPubKey);

        // Assert
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        Assert.Equal(child.Transaction.TxId, signed.TxId);
        var spent = SpentOutputs(commitment, ourVout, inputs);
        for (var i = 0; i < tx.Inputs.Count; i++)
            Assert.True(tx.Inputs.AsIndexedInputs().ElementAt(i).VerifyScript(spent[i], out var error),
                        $"input {i}: {error}");

        // The estimate never undercounts the signed weight, and is at most a few bytes above it
        var weight = Weight(tx);
        Assert.InRange(child.Weight - weight, 0, 8);
    }

    [Fact]
    public void Given_AnchorOfThePeer_When_SignedWithOurKey_Then_ScriptFails()
    {
        // Arrange: the signer only ever signs for our own anchor script
        var signer = CreateSigner();
        var commitment = CommitmentWithAnchors(out _, out var theirVout);
        var anchor = new AnchorOutpoint(commitment.GetHash().ToBytes(), theirVout, s_theirFundingPubKey);
        var inputs = new[] { WalletInput(s_walletKey, 1, 50_000) };
        var child = _builder.BuildChild(anchor, inputs, s_changeScript, 5_000);

        // Act
        var signature = signer.SignAnchorInput(s_channelId, child.Transaction, 0,
                                               TransactionConstants.AnchorOutputAmount);
        var signed = _builder.AddAnchorWitness(child.Transaction.RawTxBytes, 0, signature, s_theirFundingPubKey);

        // Assert
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        var spent = new TxOut(Money.Satoshis(330), commitment.Outputs[theirVout].ScriptPubKey);
        Assert.False(tx.Inputs.AsIndexedInputs().First().VerifyScript(spent, out _));
    }

    [Fact]
    public void Given_AmountOtherThan330_When_SigningAnchor_Then_Refused()
    {
        // Arrange
        var signer = CreateSigner();
        var commitment = CommitmentWithAnchors(out var ourVout, out _);
        var anchor = new AnchorOutpoint(commitment.GetHash().ToBytes(), ourVout, s_ourFundingPubKey);
        var child = _builder.BuildChild(anchor, [WalletInput(s_walletKey, 1, 50_000)], s_changeScript, 5_000);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignAnchorInput(s_channelId, child.Transaction, 0,
                                                                    LightningMoney.Satoshis(1_000_000)));
        Assert.Throws<SignerException>(() => signer.SignAnchorInput(s_channelId, child.Transaction, 5,
                                                                    TransactionConstants.AnchorOutputAmount));
        Assert.Throws<SignerException>(() => signer.SignAnchorInput(new ChannelId(Enumerable.Repeat((byte)1, 32).ToArray()), child.Transaction, 0,
                                                                    TransactionConstants.AnchorOutputAmount));
    }

    [Fact]
    public void Given_ChangeBelowDust_When_BuildingChild_Then_Refused()
    {
        // Arrange
        var anchor = new AnchorOutpoint(new TxId(uint256.One.ToBytes()), 0, s_ourFundingPubKey);
        var inputs = new[] { WalletInput(s_walletKey, 1, 10_000) };

        // Act / Assert: 330 + 10,000 - 10,100 = 230 sat, below the P2WPKH dust limit (294)
        Assert.Throws<ArgumentException>(() => _builder.BuildChild(anchor, inputs, s_changeScript, 10_100));
        Assert.Throws<ArgumentException>(() => _builder.BuildChild(anchor, [], s_changeScript, 100));
        Assert.Throws<ArgumentException>(() => _builder.BuildChild(anchor, [inputs[0], inputs[0]], s_changeScript,
                                                                   100));
    }

    [Fact]
    public void Given_ConfirmedCommitment_When_SweepingBothAnchors_Then_EmptySignaturesSpendThemAfter16Blocks()
    {
        // Arrange
        var commitment = CommitmentWithAnchors(out var ourVout, out var theirVout);
        var txId = new TxId(commitment.GetHash().ToBytes());
        var anchors = new[]
        {
            new AnchorOutpoint(txId, ourVout, s_ourFundingPubKey),
            new AnchorOutpoint(txId, theirVout, s_theirFundingPubKey)
        };
        var weight = _builder.EstimateSweepWeight(2, s_changeScript.Length);

        // Act
        var sweep = _builder.BuildAnchorSweep(anchors, s_changeScript, 150);

        // Assert
        var tx = Transaction.Load(sweep.RawTxBytes, Network.Main);
        Assert.Equal(660 - 150, tx.Outputs.Single().Value.Satoshi);
        Assert.All(tx.Inputs, i => Assert.Equal(16u, (uint)i.Sequence));
        Assert.Equal(weight, Weight(tx));
        Assert.True(tx.Inputs.AsIndexedInputs().ElementAt(0)
                      .VerifyScript(commitment.Outputs[ourVout], out var e0), e0.ToString());
        Assert.True(tx.Inputs.AsIndexedInputs().ElementAt(1)
                      .VerifyScript(commitment.Outputs[theirVout], out var e1), e1.ToString());

        // The same spend with nSequence 15 fails OP_16 OP_CHECKSEQUENCEVERIFY
        tx.Inputs[0].Sequence = new Sequence(15);
        Assert.False(tx.Inputs.AsIndexedInputs().ElementAt(0).VerifyScript(commitment.Outputs[ourVout], out _));
    }

    [Fact]
    public void Given_OneAnchor_When_SweepFeeLeavesDust_Then_Refused()
    {
        // Arrange
        var anchors = new[] { new AnchorOutpoint(new TxId(uint256.One.ToBytes()), 0, s_ourFundingPubKey) };

        // Act / Assert: 330 - 95 = 235 sat, below 294
        Assert.Throws<ArgumentException>(() => _builder.BuildAnchorSweep(anchors, s_changeScript, 95));
    }

    private static Transaction CommitmentWithAnchors(out uint ourVout, out uint theirVout)
    {
        var builder = new AnchorChildTransactionBuilder();
        var tx = Transaction.Create(Network.Main);
        tx.Version = 2;
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(new TxOut(Money.Satoshis(500_000), s_walletKey.PubKey.WitHash.ScriptPubKey));
        tx.Outputs.Add(new TxOut(Money.Satoshis(330), new Script(builder.GetAnchorScriptPubKey(s_theirFundingPubKey))));
        tx.Outputs.Add(new TxOut(Money.Satoshis(330), new Script(builder.GetAnchorScriptPubKey(s_ourFundingPubKey))));
        theirVout = 1;
        ourVout = 2;
        return tx;
    }

    private static AnchorWalletInput WalletInput(Key key, byte tag, ulong amountSat) =>
        new(new TxId(Enumerable.Repeat(tag, 32).ToArray()), tag, amountSat,
            key.PubKey.WitHash.ScriptPubKey.ToBytes(), 273);

    private static byte[] SignWalletInputs(byte[] unsigned, IReadOnlyList<AnchorWalletInput> inputs)
    {
        var tx = Transaction.Load(unsigned, Network.Main);
        for (var i = 0; i < inputs.Count; i++)
        {
            var key = inputs[i].ScriptPubKey.SequenceEqual(s_walletKey.PubKey.WitHash.ScriptPubKey.ToBytes())
                          ? s_walletKey
                          : s_walletKey2;
            var prevOut = new TxOut(Money.Satoshis(inputs[i].AmountSat), new Script(inputs[i].ScriptPubKey));
            var hash = tx.GetSignatureHash(key.PubKey.Hash.ScriptPubKey, i + 1, SigHash.All, prevOut,
                                           HashVersion.WitnessV0);
            var signature = key.Sign(hash, new SigningOptions(SigHash.All, false));
            tx.Inputs[i + 1].WitScript = new WitScript(Op.GetPushOp(signature.ToBytes()),
                                                       Op.GetPushOp(key.PubKey.ToBytes()));
        }

        return tx.ToBytes();
    }

    private static TxOut[] SpentOutputs(Transaction commitment, uint anchorVout,
                                        IReadOnlyList<AnchorWalletInput> inputs) =>
    [
        commitment.Outputs[anchorVout],
        .. inputs.Select(i => new TxOut(Money.Satoshis(i.AmountSat), new Script(i.ScriptPubKey)))
    ];

    private static long Weight(Transaction tx)
    {
        var stripped = tx.Clone();
        foreach (var input in stripped.Inputs)
            input.WitScript = WitScript.Empty;
        return 3L * stripped.ToBytes().Length + tx.ToBytes().Length;
    }

    private static NodeASigner CreateSigner()
    {
        var signer = new NodeASigner();
        signer.RegisterChannel(s_channelId,
                               new ChannelSigningInfo(Bolt3AppendixBVectors.ExpectedTxId.ToBytes(), 0,
                                                      Bolt3AppendixBVectors.FundingSatoshis,
                                                      Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                      Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(), 0));
        return signer;
    }

    /// <summary>A <see cref="LocalLightningSigner"/> whose funding key is Appendix C's node A funding key.</summary>
    private sealed class NodeASigner()
        : LocalLightningSigner(new FundingOutputBuilder(), new KeyDerivationService(new Secp256K1Math()),
                               NullLogger<LocalLightningSigner>.Instance, new NodeOptions(),
                               new Mock<ISecureKeyManager>().Object, new Mock<IUtxoMemoryRepository>().Object)
    {
        protected override Key GenerateFundingPrivateKey(uint channelKeyIndex) =>
            new(Bolt3AppendixCVectors.NodeAFundingPrivkey.ToBytes());
    }
}