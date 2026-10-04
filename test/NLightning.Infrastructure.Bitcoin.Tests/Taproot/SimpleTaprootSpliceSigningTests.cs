using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;

/// <summary>
/// NL-965 (taproot wave t03 lane SPL): the signer's half of splicing a simple taproot channel (BOLTs PR #1324): the
/// verification nonces of a splice funding before it is registered, the shared input's JIT signing nonce
/// (<c>funding_nonce</c>), the MuSig2 partial signature of the shared input and its aggregation into a key-path witness
/// that passes script execution against the previous funding output, with every spent output in the sighash.
/// </summary>
public class SimpleTaprootSpliceSigningTests
{
    private const long WalletInputSat = 200_000;
    private const ulong NewCapacitySat = TaprootSignerKit.CapacitySat + 150_000;

    [Fact]
    public void Given_BothSignersPartialSignatures_When_Aggregated_Then_TheSharedInputVerifiesByScriptExecution()
    {
        // Arrange
        var kit = new TaprootSignerKit();
        var (splice, spliceTxId, walletOutput) = RegisterSplice(kit);
        var aliceNonce = kit.Alice.CreateSpliceFundingNonce(TaprootSignerKit.ChannelId);
        var bobNonce = kit.Bob.CreateSpliceFundingNonce(TaprootSignerKit.ChannelId);
        var unsigned = new SignedTransaction(spliceTxId, splice.ToBytes());
        var spent = SpentOutputs(kit, walletOutput);

        // Act
        var alice = kit.Alice.SignSpliceSharedInputPartial(TaprootSignerKit.ChannelId, spliceTxId, unsigned, 0, spent,
                                                           aliceNonce, bobNonce);
        var bob = kit.Bob.SignSpliceSharedInputPartial(TaprootSignerKit.ChannelId, spliceTxId, unsigned, 0, spent,
                                                       bobNonce, aliceNonce);
        var byAlice = kit.Alice.AggregateSpliceSharedInputSignature(TaprootSignerKit.ChannelId, unsigned, 0, spent,
                                                                    alice, bob);
        var byBob = kit.Bob.AggregateSpliceSharedInputSignature(TaprootSignerKit.ChannelId, unsigned, 0, spent, bob,
                                                                alice);

        // Assert: one signature, a valid key-path spend of the previous P2TR funding output
        Assert.Equal(aliceNonce, alice.PublicNonce);
        Assert.Equal(byAlice, byBob);
        Assert.Equal(64, byAlice.Length);
        splice.Inputs[0].WitScript = new WitScript([byAlice]);
        var validator = splice.CreateValidator([kit.FundingTxOut, walletOutput]);
        Assert.Null(validator.ValidateInput(0).Error);
    }

    [Fact]
    public void Given_AFundingNonceThatSigned_When_UsedAgain_Then_Refused()
    {
        // Arrange (D-T4: a signing nonce signs once; a second session with it would reveal the funding key)
        var kit = new TaprootSignerKit();
        var (splice, spliceTxId, walletOutput) = RegisterSplice(kit);
        var aliceNonce = kit.Alice.CreateSpliceFundingNonce(TaprootSignerKit.ChannelId);
        var bobNonce = kit.Bob.CreateSpliceFundingNonce(TaprootSignerKit.ChannelId);
        var otherBobNonce = kit.Bob.CreateSpliceFundingNonce(TaprootSignerKit.ChannelId);
        var unsigned = new SignedTransaction(spliceTxId, splice.ToBytes());
        var spent = SpentOutputs(kit, walletOutput);
        kit.Alice.SignSpliceSharedInputPartial(TaprootSignerKit.ChannelId, spliceTxId, unsigned, 0, spent,
                                               aliceNonce, bobNonce);

        // Act & Assert: the same nonce, even against another nonce of the peer, and an unknown nonce
        Assert.Throws<SignerException>(() => kit.Alice.SignSpliceSharedInputPartial(
                                           TaprootSignerKit.ChannelId, spliceTxId, unsigned, 0, spent, aliceNonce,
                                           otherBobNonce));
        Assert.Throws<SignerException>(() => kit.Alice.SignSpliceSharedInputPartial(
                                           TaprootSignerKit.ChannelId, spliceTxId, unsigned, 0, spent, bobNonce,
                                           otherBobNonce));
    }

    [Fact]
    public void Given_ASpliceThatDoesNotCreateTheRegisteredFunding_When_Signing_Then_RefusedAndTheNonceKept()
    {
        // Arrange
        var kit = new TaprootSignerKit();
        var (splice, spliceTxId, walletOutput) = RegisterSplice(kit);
        var aliceNonce = kit.Alice.CreateSpliceFundingNonce(TaprootSignerKit.ChannelId);
        var bobNonce = kit.Bob.CreateSpliceFundingNonce(TaprootSignerKit.ChannelId);
        var other = splice.Clone();
        other.Outputs[0].Value = Money.Satoshis((long)NewCapacitySat - 1);
        TxId otherTxId = other.GetHash().ToBytes();
        var spent = SpentOutputs(kit, walletOutput);

        // Act & Assert: not the registered pending funding (its txid), nor its output
        Assert.Throws<SignerException>(() => kit.Alice.SignSpliceSharedInputPartial(
                                           TaprootSignerKit.ChannelId, otherTxId,
                                           new SignedTransaction(otherTxId, other.ToBytes()), 0, spent, aliceNonce,
                                           bobNonce));
        Assert.Throws<SignerException>(() => kit.Alice.SignSpliceSharedInputPartial(
                                           TaprootSignerKit.ChannelId, spliceTxId,
                                           new SignedTransaction(otherTxId, other.ToBytes()), 0, spent, aliceNonce,
                                           bobNonce));

        // The nonce was not consumed by a refusal
        var partial = kit.Alice.SignSpliceSharedInputPartial(TaprootSignerKit.ChannelId, spliceTxId,
                                                             new SignedTransaction(spliceTxId, splice.ToBytes()), 0,
                                                             spent, aliceNonce, bobNonce);
        Assert.Equal(aliceNonce, partial.PublicNonce);
    }

    [Fact]
    public void Given_ABadPeerPartialSignature_When_Aggregated_Then_SignerException()
    {
        // Arrange
        var kit = new TaprootSignerKit();
        var (splice, spliceTxId, walletOutput) = RegisterSplice(kit);
        var aliceNonce = kit.Alice.CreateSpliceFundingNonce(TaprootSignerKit.ChannelId);
        var bobNonce = kit.Bob.CreateSpliceFundingNonce(TaprootSignerKit.ChannelId);
        var unsigned = new SignedTransaction(spliceTxId, splice.ToBytes());
        var spent = SpentOutputs(kit, walletOutput);
        var alice = kit.Alice.SignSpliceSharedInputPartial(TaprootSignerKit.ChannelId, spliceTxId, unsigned, 0, spent,
                                                           aliceNonce, bobNonce);
        var bob = kit.Bob.SignSpliceSharedInputPartial(TaprootSignerKit.ChannelId, spliceTxId, unsigned, 0, spent,
                                                       bobNonce, aliceNonce);
        var bytes = bob.ToBytes();
        bytes[31] ^= 0x01;

        // Act & Assert: a corrupted signature, and the right one over another spent amount (the BIP 341 sighash
        // commits to every spent output)
        Assert.Throws<SignerException>(() => kit.Alice.AggregateSpliceSharedInputSignature(
                                           TaprootSignerKit.ChannelId, unsigned, 0, spent, alice,
                                           new MusigPartialSignatureWithNonce(bytes)));
        var otherAmount = new List<SpentOutput>(spent)
        {
            [1] = spent[1] with { Amount = LightningMoney.Satoshis(WalletInputSat + 1) }
        };
        Assert.Throws<SignerException>(() => kit.Alice.AggregateSpliceSharedInputSignature(
                                           TaprootSignerKit.ChannelId, unsigned, 0, otherAmount, alice, bob));
    }

    [Fact]
    public void Given_ASpliceFundingKey_When_ItsVerificationNonceIsDerivedBeforeRegistration_Then_ItIsTheRegisteredOne()
    {
        // Arrange
        var kit = new TaprootSignerKit();
        TxId spliceTxId = Enumerable.Repeat((byte)0x61, 32).ToArray();

        // Act: before the funding is registered (tx_complete commit_nonces)
        var before = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, 1u, spliceTxId, 5);
        var next = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, 1u, spliceTxId, 6);
        var aliceKey = kit.Alice.GetFundingPubKey(0u, 1u);
        var bobKey = kit.Bob.GetFundingPubKey(0u, 1u);
        kit.Alice.RegisterFunding(TaprootSignerKit.ChannelId,
                                  new ChannelFunding(spliceTxId, 0, NewCapacitySat, aliceKey, bobKey, 1, 0, 0,
                                                     ChannelFundingKind.Splice, ChannelFundingStatus.Pending));
        var registered = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, spliceTxId, 5);

        // Assert: the same nonce, bound to the txid (not the current funding's)
        Assert.Equal(before, registered);
        Assert.NotEqual(before, next);
        Assert.NotEqual(before, kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, 5));
        Assert.Throws<SignerException>(() => kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, 0u,
                                                                                 spliceTxId, 5));
    }

    [Fact]
    public void Given_ATaprootSpliceOnFundingKeyZero_When_Registered_Then_Refused()
    {
        // Arrange (key 0's commitment 0 nonce of a v1 open has no txid: a splice on it would share it, NL-972)
        var kit = new TaprootSignerKit();
        TxId spliceTxId = Enumerable.Repeat((byte)0x62, 32).ToArray();
        var funding = new ChannelFunding(spliceTxId, 0, NewCapacitySat, kit.AliceBasepoints.FundingPubKey,
                                         kit.BobBasepoints.FundingPubKey, 0, 0, 0, ChannelFundingKind.Splice,
                                         ChannelFundingStatus.Pending);

        // Act & Assert
        Assert.Throws<SignerException>(() => kit.Alice.RegisterFunding(TaprootSignerKit.ChannelId, funding));
    }

    [Fact]
    public void Given_ANonTaprootChannel_When_AFundingNonceIsAsked_Then_Refused()
    {
        // Arrange
        var kit = new TaprootSignerKit(isSimpleTaproot: false);

        // Act & Assert
        Assert.Throws<SignerException>(() => kit.Alice.CreateSpliceFundingNonce(TaprootSignerKit.ChannelId));
    }

    /// <summary>
    /// A splice of the kit's channel registered with both signers: input 0 the current P2TR funding, input 1 a P2WPKH
    /// wallet output, output 0 the P2TR funding of both rotated keys (key 1).
    /// </summary>
    private static (Transaction Splice, TxId SpliceTxId, TxOut WalletOutput) RegisterSplice(TaprootSignerKit kit)
    {
        var aliceKey = kit.Alice.GetFundingPubKey(0u, 1u);
        var bobKey = kit.Bob.GetFundingPubKey(0u, 1u);
        var walletOutput = new TxOut(Money.Satoshis(WalletInputSat),
                                     new Key(Enumerable.Repeat((byte)0x44, 32).ToArray()).PubKey.WitHash.ScriptPubKey);
        var splice = Transaction.Create(Network.Main);
        splice.Version = 2;
        splice.Inputs.Add(new OutPoint(kit.FundingTx.GetHash(), 0), Script.Empty, WitScript.Empty,
                          new Sequence(0xFFFFFFFD));
        splice.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat((byte)0x45, 32).ToArray()), 3), Script.Empty,
                          WitScript.Empty, new Sequence(0xFFFFFFFD));
        splice.Outputs.Add(Money.Satoshis((long)NewCapacitySat),
                           new Script(kit.Musig.AggregateTaprootKeyPath(aliceKey, bobKey).GetTaprootScriptPubKey()));
        TxId spliceTxId = splice.GetHash().ToBytes();
        kit.Alice.RegisterFunding(TaprootSignerKit.ChannelId,
                                  new ChannelFunding(spliceTxId, 0, NewCapacitySat, aliceKey, bobKey, 1, 0, 0,
                                                     ChannelFundingKind.Splice, ChannelFundingStatus.Pending));
        kit.Bob.RegisterFunding(TaprootSignerKit.ChannelId,
                                new ChannelFunding(spliceTxId, 0, NewCapacitySat, bobKey, aliceKey, 1, 0, 0,
                                                   ChannelFundingKind.Splice, ChannelFundingStatus.Pending));
        return (splice, spliceTxId, walletOutput);
    }

    /// <summary>Every output the splice spends, in input order (the shared input's from the funding).</summary>
    private static List<SpentOutput> SpentOutputs(TaprootSignerKit kit, TxOut walletOutput) =>
    [
        new(kit.FundingTxId, 0, LightningMoney.Satoshis(TaprootSignerKit.CapacitySat),
            new BitcoinScript(kit.FundingTxOut.ScriptPubKey.ToBytes())),
        new(Enumerable.Repeat((byte)0x45, 32).ToArray(), 3, LightningMoney.Satoshis(WalletInputSat),
            new BitcoinScript(walletOutput.ScriptPubKey.ToBytes()))
    ];
}
