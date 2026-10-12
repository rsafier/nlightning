using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Bitcoin.Builders;
using Bitcoin.Builders.Interfaces;
using Bitcoin.Crypto.Functions;
using Bitcoin.Outputs;
using Bitcoin.Services;
using Domain.Bitcoin.Transactions.Constants;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;

/// <summary>
/// NL-966 (6): the spends of simple taproot anchors (bolt-simple-taproot.md §Anchor Outputs). Our CPFP child spends
/// our anchor by key path (the owner's key tweaked with the <c>OP_16 OP_CSV</c> root, BIP 340 <c>SIGHASH_DEFAULT</c>
/// over every spent output): our <c>to_local_anchor</c> on our commitment (our delayed key at that commitment's point)
/// and our <c>to_remote_anchor</c> on the peer's (our payment basepoint). Anyone sweeps any anchor by the leaf after 16
/// blocks. Every input is executed by NBitcoin's interpreter against all the outputs it spends.
/// </summary>
public class SimpleTaprootAnchorSpendTests
{
    private const ulong Number = 9;

    private static readonly byte[] s_walletScript = new Key(Enumerable.Repeat((byte)0x41, 32).ToArray())
                                                   .PubKey.WitHash.ScriptPubKey.ToBytes();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_OurTaprootAnchor_When_TheChildIsSigned_Then_EveryInputVerifies(bool onOurCommitment)
    {
        // Arrange: Bob's anchor on his commitment (delayed key at his point) or on Alice's (his payment basepoint)
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var (internalKey, point) = BobsAnchorKey(kit, onOurCommitment);
        var (commitment, vout) = CommitmentWithAnchor(internalKey);
        var builder = new AnchorChildTransactionBuilder();
        var anchor = new AnchorOutpoint(commitment.GetHash().ToBytes(), vout, internalKey, true, point);
        var (wallet, walletKey) = WalletOutput(0x42, 30_000);
        var walletInput = new AnchorWalletInput(wallet.GetHash().ToBytes(), 0, 30_000,
                                                wallet.Outputs[0].ScriptPubKey.ToBytes(), 273);

        // Act
        var unsigned = builder.BuildChild(anchor, [walletInput], s_walletScript, 2_000);
        var spent = SpentOutputs(commitment, vout, wallet);
        var signature = kit.Bob.SignTaprootAnchorInput(TaprootSignerKit.ChannelId, unsigned.Transaction, 0,
                                                       point, spent);
        var tx = Transaction.Load(builder.AddTaprootAnchorWitness(unsigned.Transaction.RawTxBytes, 0, signature)
                                         .RawTxBytes, Network.Main);
        SignP2Wpkh(tx, 1, walletKey, wallet.Outputs[0]);

        // Assert: the key-path witness is the 64-byte signature alone; the child is valid and its weight bounded
        Assert.Equal(64, Assert.Single(tx.Inputs[0].WitScript.Pushes).Length);
        Assert.Equal(new OutPoint(commitment, vout), tx.Inputs[0].PrevOut);
        var validator = tx.CreateValidator(spent.Select(ToTxOut).ToArray());
        for (var i = 0; i < tx.Inputs.Count; i++)
            Assert.True(validator.ValidateInput(i).Error is null, $"input {i}: {validator.ValidateInput(i).Error}");
        Assert.True(tx.GetVirtualSize() * 4 <= unsigned.Weight);
        Assert.Equal(builder.EstimateChildWeight([walletInput], s_walletScript.Length, true), unsigned.Weight);
    }

    [Fact]
    public void Given_AnOutputThatIsNotOurAnchor_When_SigningTheAnchorInput_Then_TheSignerRefuses()
    {
        // Arrange: the peer's anchor (keyed to Alice's payment basepoint), a wrong point, a wrong amount
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var (ourKey, ourPoint) = BobsAnchorKey(kit, onOurCommitment: true);
        var (commitment, vout) = CommitmentWithAnchor(kit.AliceBasepoints.PaymentBasepoint);
        var builder = new AnchorChildTransactionBuilder();
        var (wallet, _) = WalletOutput(0x43, 30_000);
        var walletInput = new AnchorWalletInput(wallet.GetHash().ToBytes(), 0, 30_000,
                                                wallet.Outputs[0].ScriptPubKey.ToBytes(), 273);
        var theirs = builder.BuildChild(new AnchorOutpoint(commitment.GetHash().ToBytes(), vout,
                                                           kit.AliceBasepoints.PaymentBasepoint, true),
                                        [walletInput], s_walletScript, 2_000);

        // Act / Assert: no key of Bob's derives that anchor
        Assert.Throws<SignerException>(() => kit.Bob.SignTaprootAnchorInput(
                                           TaprootSignerKit.ChannelId, theirs.Transaction, 0, null,
                                           SpentOutputs(commitment, vout, wallet)));
        Assert.Throws<SignerException>(() => kit.Bob.SignTaprootAnchorInput(
                                           TaprootSignerKit.ChannelId, theirs.Transaction, 0, ourPoint,
                                           SpentOutputs(commitment, vout, wallet)));

        // Our own anchor, but declared at another amount, or without every spent output
        var (ourCommitment, ourVout) = CommitmentWithAnchor(ourKey);
        var ours = builder.BuildChild(new AnchorOutpoint(ourCommitment.GetHash().ToBytes(), ourVout, ourKey, true,
                                                         ourPoint), [walletInput], s_walletScript, 2_000);
        var spent = SpentOutputs(ourCommitment, ourVout, wallet);
        Assert.Throws<SignerException>(() => kit.Bob.SignTaprootAnchorInput(
                                           TaprootSignerKit.ChannelId, ours.Transaction, 0, ourPoint,
                                           [spent[0] with { Amount = LightningMoney.Satoshis(331) }, spent[1]]));
        Assert.Throws<SignerException>(() => kit.Bob.SignTaprootAnchorInput(
                                           TaprootSignerKit.ChannelId, ours.Transaction, 0, ourPoint, [spent[0]]));

        // A P2WSH anchors channel never signs a taproot anchor
        var anchorsKit = new TaprootSignerKit(bobLocalNumber: Number, isSimpleTaproot: false);
        Assert.Throws<SignerException>(() => anchorsKit.Bob.SignTaprootAnchorInput(
                                           TaprootSignerKit.ChannelId, ours.Transaction, 0, ourPoint, spent));
    }

    [Fact]
    public void Given_TwoTaprootAnchorsSixteenBlocksDeep_When_Swept_Then_TheLeafSpendsAreValidWithSequence16()
    {
        // Arrange: a commitment with both anchors
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var (bobKey, _) = BobsAnchorKey(kit, onOurCommitment: true);
        var aliceKey = kit.AliceBasepoints.PaymentBasepoint;
        var commitment = Transaction.Create(Network.Main);
        commitment.Inputs.Add(new OutPoint(new uint256(9), 0));
        commitment.Outputs.Add(Anchor(bobKey).ToTxOut());
        commitment.Outputs.Add(Anchor(aliceKey).ToTxOut());
        var builder = new AnchorChildTransactionBuilder();
        TxId txId = commitment.GetHash().ToBytes();

        // Act
        var sweep = Transaction.Load(builder.BuildAnchorSweep([
                                         new AnchorOutpoint(txId, 0, bobKey, true),
                                         new AnchorOutpoint(txId, 1, aliceKey, true)
                                     ], s_walletScript, 200).RawTxBytes, Network.Main);

        // Assert
        Assert.Equal(2, sweep.Inputs.Count);
        var validator = sweep.CreateValidator([commitment.Outputs[0], commitment.Outputs[1]]);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(16u, sweep.Inputs[i].Sequence.Value);
            Assert.Equal(2, sweep.Inputs[i].WitScript.PushCount);
            Assert.True(validator.ValidateInput(i).Error is null, $"input {i}: {validator.ValidateInput(i).Error}");
        }

        Assert.True(sweep.GetVirtualSize() * 4 <= builder.EstimateSweepWeight(2, s_walletScript.Length));

        // Before the 16 blocks (nSequence 15) the leaf refuses
        var early = sweep.Clone();
        early.Inputs[0].Sequence = new Sequence(15);
        Assert.NotNull(early.CreateValidator([commitment.Outputs[0], commitment.Outputs[1]]).ValidateInput(0).Error);
    }

    private static (CompactPubKey InternalKey, CompactPubKey? Point) BobsAnchorKey(TaprootSignerKit kit,
                                                                                  bool onOurCommitment)
    {
        if (!onOurCommitment)
            return (kit.BobBasepoints.PaymentBasepoint, null);

        var keys = new CommitmentKeyDerivationService(new KeyDerivationService(new Secp256K1Math()), kit.Bob)
           .DeriveLocalCommitmentKeys(0u, kit.BobBasepoints, kit.AliceBasepoints, Number);
        return (keys.LocalDelayedPubKey, keys.PerCommitmentPoint);
    }

    private static TaprootAnchorOutput Anchor(CompactPubKey internalKey) =>
        new(TransactionConstants.AnchorOutputAmount, new PubKey(internalKey));

    /// <summary>A stand-in commitment: some other output, then the anchor.</summary>
    private static (Transaction Commitment, uint Vout) CommitmentWithAnchor(CompactPubKey internalKey)
    {
        var commitment = Transaction.Create(Network.Main);
        commitment.Inputs.Add(new OutPoint(new uint256(7), 0));
        commitment.Outputs.Add(Money.Satoshis(50_000), new Script(s_walletScript));
        commitment.Outputs.Add(Anchor(internalKey).ToTxOut());
        Assert.Equal(1u, new AnchorChildTransactionBuilder().FindTaprootAnchorOutput(commitment.ToBytes(), internalKey));
        return (commitment, 1);
    }

    private static (Transaction Funding, Key Key) WalletOutput(byte seed, long amountSat)
    {
        var key = new Key(Enumerable.Repeat(seed, 32).ToArray());
        var funding = Transaction.Create(Network.Main);
        funding.Inputs.Add(new OutPoint(new uint256(seed), 0));
        funding.Outputs.Add(Money.Satoshis(amountSat), key.PubKey.WitHash.ScriptPubKey);
        return (funding, key);
    }

    private static List<SpentOutput> SpentOutputs(Transaction commitment, uint vout, Transaction wallet) =>
    [
        new(commitment.GetHash().ToBytes(), vout, TransactionConstants.AnchorOutputAmount,
            commitment.Outputs[vout].ScriptPubKey.ToBytes()),
        new(wallet.GetHash().ToBytes(), 0, LightningMoney.Satoshis(wallet.Outputs[0].Value.Satoshi),
            wallet.Outputs[0].ScriptPubKey.ToBytes())
    ];

    private static TxOut ToTxOut(SpentOutput output) =>
        new(Money.Satoshis(output.Amount.Satoshi), new Script((byte[])output.ScriptPubKey));

    private static void SignP2Wpkh(Transaction tx, int index, Key key, TxOut spent)
    {
        var hash = tx.GetSignatureHash(key.PubKey.Hash.ScriptPubKey, index, SigHash.All, spent, HashVersion.WitnessV0);
        tx.Inputs[index].WitScript = PayToWitPubKeyHashTemplate.Instance.GenerateWitScript(
            new TransactionSignature(key.Sign(hash), SigHash.All), key.PubKey);
    }
}