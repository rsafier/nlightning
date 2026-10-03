using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBitcoin.Crypto;
using NLightning.Integration.Tests.BOLT3.Mocks;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Outputs;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// BOLT 5 plan O3-T1 / §3.5: <see cref="LocalLightningSigner.SignSweepInput"/> derives each of the four key kinds
/// exactly as BOLT 3 (checked against the Appendix C keys) and refuses a key the witness script does not commit to.
/// </summary>
public class LocalLightningSignerSweepTests
{
    private static readonly CompactPubKey s_localPoint = Bolt3TestCommitmentKeyDerivationService.LocalPerCommitmentPoint;

    // Appendix C to_local of node A's commitment 42: revocation key 0212a1..., delay 144, delayed key 03fd59...
    private static readonly Script s_toLocalScript = new ToLocalOutput(Domain.Money.LightningMoney.Satoshis(1),
                                                                       Bolt3AppendixCVectors.NodeADelayedPubkey,
                                                                       Bolt3AppendixCVectors.NodeARevocationPubkey,
                                                                       Bolt3AppendixCVectors.LocalDelay).RedeemScript;

    [Fact]
    public void Given_DelayedPaymentKind_When_Signing_Then_KeyIsAppendixCLocalDelayedPrivkey()
    {
        // Arrange: node A with our (local) per-commitment point
        var signer = new AppendixCSweepSigner(asNodeB: false);
        var (tx, context) = SpendOf(s_toLocalScript, SweepKeyKind.DelayedPayment, point: s_localPoint);

        // Act
        var signature = signer.SignSweepInput(ChannelId.Zero, context);

        // Assert: RFC 6979 makes the signature a function of the key, so it equals one made with local_delayed_privkey
        Assert.Equal(SignWith(AppendixCSweepSigner.LocalDelayedPrivkey, tx, s_toLocalScript), (byte[])signature);
        Assert.Equal(Bolt3AppendixCVectors.NodeADelayedPubkey,
                     new Key(AppendixCSweepSigner.LocalDelayedPrivkey).PubKey);
    }

    [Fact]
    public void Given_HtlcRemotePointKind_When_Signing_Then_KeyIsNodeBHtlcKeyAtNodeAPoint()
    {
        // Arrange: node B claims an HTLC of node A's commitment; the script holds remote_htlcpubkey 0394854a...
        var signer = new AppendixCSweepSigner(asNodeB: true);
        var script = OfferedHtlcScript();
        var (tx, context) = SpendOf(script, SweepKeyKind.HtlcRemotePoint, point: s_localPoint);

        // Act
        var signature = signer.SignSweepInput(ChannelId.Zero, context);

        // Assert: the HTLC basepoint equals the payment basepoint in the vectors, so the key is remote_privkey
        Assert.Equal(SignWith(AppendixCSweepSigner.RemotePrivkey, tx, script), (byte[])signature);
        Assert.Equal(Bolt3AppendixCVectors.NodeBHtlcPubkey, new Key(AppendixCSweepSigner.RemotePrivkey).PubKey);
    }

    [Fact]
    public void Given_PaymentKindWithoutScript_When_Signing_Then_P2WpkhOfPaymentBasepointSigned()
    {
        // Arrange: node B's to_remote on node A's commitment (static_remotekey: the basepoint itself)
        var signer = new AppendixCSweepSigner(asNodeB: true);
        var basepointKey = new Key(AppendixCSweepSigner.RemotePaymentBasepointSecret);
        Assert.Equal(Bolt3AppendixCVectors.NodeBPaymentBasepoint, basepointKey.PubKey);
        var (tx, context) = SpendOf(null, SweepKeyKind.Payment);

        // Act
        var signature = signer.SignSweepInput(ChannelId.Zero, context);

        // Assert: BIP 143 signs the P2PKH script code of the key
        Assert.Equal(SignWith(AppendixCSweepSigner.RemotePaymentBasepointSecret, tx,
                              basepointKey.PubKey.Hash.ScriptPubKey, basepointKey.PubKey.WitHash.ScriptPubKey),
                     (byte[])signature);
    }

    [Fact]
    public void Given_RevocationKind_When_Signing_Then_KeyIsRevocationPrivkeyOfNodeACommitment()
    {
        // Arrange: node B penalizes node A's revoked commitment 42 with x_local_per_commitment_secret
        var signer = new AppendixCSweepSigner(asNodeB: true);
        var (tx, context) = SpendOf(s_toLocalScript, SweepKeyKind.Revocation,
                                    secret: AppendixCSweepSigner.XLocalPerCommitmentSecret, point: s_localPoint);
        var expectedKey = new KeyDerivationService(new Secp256K1Math())
           .DeriveRevocationPrivKey(AppendixCSweepSigner.RemoteRevocationBasepointSecret,
                                    AppendixCSweepSigner.XLocalPerCommitmentSecret);

        // Act
        var signature = signer.SignSweepInput(ChannelId.Zero, context);

        // Assert: the revocation privkey's pubkey is the vectors' revocation key
        Assert.Equal(Bolt3AppendixCVectors.NodeARevocationPubkey, new Key(expectedKey).PubKey);
        Assert.Equal(SignWith(expectedKey, tx, s_toLocalScript), (byte[])signature);
    }

    [Fact]
    public void Given_SecretNotMatchingPoint_When_SigningRevocation_Then_Refused()
    {
        // Arrange
        var signer = new AppendixCSweepSigner(asNodeB: true);
        var (_, context) = SpendOf(s_toLocalScript, SweepKeyKind.Revocation,
                                   secret: AppendixCSweepSigner.XLocalPerCommitmentSecret,
                                   point: Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes());

        // Act / Assert
        var exception = Assert.Throws<SignerException>(() => signer.SignSweepInput(ChannelId.Zero, context));
        Assert.Contains("does not match", exception.Message);
    }

    [Fact]
    public void Given_WrongSecret_When_SigningRevocation_Then_RefusedBecauseKeyNotInScript()
    {
        // Arrange: a secret of another commitment derives another revocation key
        var signer = new AppendixCSweepSigner(asNodeB: true);
        var (_, context) = SpendOf(s_toLocalScript, SweepKeyKind.Revocation, secret: Enumerable.Repeat((byte)7, 32)
                                      .ToArray());

        // Act / Assert
        var exception = Assert.Throws<SignerException>(() => signer.SignSweepInput(ChannelId.Zero, context));
        Assert.Contains("does not contain", exception.Message);
    }

    [Theory]
    [InlineData(SweepKeyKind.Payment)]
    [InlineData(SweepKeyKind.HtlcRemotePoint)]
    public void Given_WrongKeyKind_When_SigningToLocal_Then_Refused(SweepKeyKind kind)
    {
        // Arrange: node A's to_local commits to the delayed and revocation keys only
        var signer = new AppendixCSweepSigner(asNodeB: false);
        var (_, context) = SpendOf(s_toLocalScript, kind, point: s_localPoint);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignSweepInput(ChannelId.Zero, context));
    }

    [Fact]
    public void Given_WrongPoint_When_SigningHtlcClaim_Then_Refused()
    {
        // Arrange: tweak by another point than the commitment's
        var signer = new AppendixCSweepSigner(asNodeB: true);
        var (_, context) = SpendOf(OfferedHtlcScript(), SweepKeyKind.HtlcRemotePoint,
                                   point: Bolt3AppendixCVectors.NodeAPaymentBasepoint.ToBytes());

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignSweepInput(ChannelId.Zero, context));
    }

    [Theory]
    [InlineData(SweepKeyKind.DelayedPayment)]
    [InlineData(SweepKeyKind.HtlcRemotePoint)]
    public void Given_PointMissing_When_Signing_Then_Refused(SweepKeyKind kind)
    {
        // Arrange
        var signer = new AppendixCSweepSigner(asNodeB: kind == SweepKeyKind.HtlcRemotePoint);
        var (_, context) = SpendOf(s_toLocalScript, kind);

        // Act / Assert
        var exception = Assert.Throws<SignerException>(() => signer.SignSweepInput(ChannelId.Zero, context));
        Assert.Contains("per-commitment point", exception.Message);
    }

    [Fact]
    public void Given_SecretMissing_When_SigningRevocation_Then_Refused()
    {
        // Arrange
        var signer = new AppendixCSweepSigner(asNodeB: true);
        var (_, context) = SpendOf(s_toLocalScript, SweepKeyKind.Revocation, point: s_localPoint);

        // Act / Assert
        var exception = Assert.Throws<SignerException>(() => signer.SignSweepInput(ChannelId.Zero, context));
        Assert.Contains("per-commitment secret", exception.Message);
    }

    [Fact]
    public void Given_NoScriptForNonPaymentKind_When_Signing_Then_Refused()
    {
        // Arrange
        var signer = new AppendixCSweepSigner(asNodeB: false);
        var (_, context) = SpendOf(null, SweepKeyKind.DelayedPayment, point: s_localPoint);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignSweepInput(ChannelId.Zero, context));
    }

    [Fact]
    public void Given_InputIndexOutOfRange_When_Signing_Then_Refused()
    {
        // Arrange
        var signer = new AppendixCSweepSigner(asNodeB: false);
        var (_, context) = SpendOf(s_toLocalScript, SweepKeyKind.DelayedPayment, point: s_localPoint);

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignSweepInput(ChannelId.Zero, context with { InputIndex = 1 }));
    }

    [Fact]
    public void Given_UnregisteredChannel_When_Signing_Then_Refused()
    {
        // Arrange
        var signer = new AppendixCSweepSigner(asNodeB: false);
        var (_, context) = SpendOf(s_toLocalScript, SweepKeyKind.DelayedPayment, point: s_localPoint);
        ChannelId otherChannel = Enumerable.Repeat((byte)1, 32).ToArray();

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignSweepInput(otherChannel, context));
    }

    [Fact]
    public void Given_DataLossAndBroadcastMark_When_SigningSweep_Then_StillSigned()
    {
        // Arrange: sweeps move outputs that are already on chain; neither guard applies (B5-RMT-03 salvages to_remote)
        var signer = new AppendixCSweepSigner(asNodeB: false);
        signer.MarkDataLoss(ChannelId.Zero);
        signer.MarkBroadcastSigned(ChannelId.Zero, Bolt3AppendixCVectors.CommitmentNumber);
        var (_, context) = SpendOf(s_toLocalScript, SweepKeyKind.DelayedPayment, point: s_localPoint);

        // Act
        var signature = signer.SignSweepInput(ChannelId.Zero, context);

        // Assert
        Assert.Equal(64, ((byte[])signature).Length);
    }

    [Fact]
    public void Given_Bip32Signer_When_SigningPaymentKind_Then_KeyIsTheChannelPaymentBasepoint()
    {
        // Arrange: the default (non-overridden) basepoint derivation from the channel key
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetChannelKeyAtIndex(0)).Returns(ExtKey.CreateFromSeed(new byte[32]).ToBytes());
        var signer = new LocalLightningSigner(new FundingOutputBuilder(),
                                              new KeyDerivationService(new Secp256K1Math()),
                                              NullLogger<LocalLightningSigner>.Instance, new NodeOptions(),
                                              keyManager.Object, new Mock<IUtxoMemoryRepository>().Object);
        signer.RegisterChannel(ChannelId.Zero,
                               new ChannelSigningInfo(Bolt3AppendixBVectors.ExpectedTxId.ToBytes(), 0,
                                                      Bolt3AppendixBVectors.FundingSatoshis,
                                                      Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                      Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(), 0));
        var basepoints = signer.GetChannelBasepoints(ChannelId.Zero);
        var paymentBasepoint = new PubKey(basepoints.PaymentBasepoint);
        var (tx, context) = SpendOf(null, SweepKeyKind.Payment);

        // Act
        var signature = signer.SignSweepInput(ChannelId.Zero, context);

        // Assert
        var sigHash = tx.GetSignatureHash(paymentBasepoint.Hash.ScriptPubKey, 0, SigHash.All,
                                          new TxOut(Money.Satoshis(10_000), paymentBasepoint.WitHash.ScriptPubKey),
                                          HashVersion.WitnessV0);
        Assert.True(ECDSASignature.TryParseFromCompact(signature, out var ecdsa));
        Assert.True(paymentBasepoint.Verify(sigHash, ecdsa));
    }

    private static Script OfferedHtlcScript() =>
        new OfferedHtlcOutput(Domain.Money.LightningMoney.Satoshis(2000), 502, false,
                              Bolt3AppendixCVectors.NodeAHtlcPubkey, Bolt3AppendixCVectors.Htlc2PaymentHash,
                              Bolt3AppendixCVectors.NodeBHtlcPubkey, Bolt3AppendixCVectors.NodeARevocationPubkey)
           .RedeemScript;

    private static (Transaction Tx, SweepSigningContext Context) SpendOf(Script? witnessScript, SweepKeyKind kind,
                                                                         CompactPubKey? point = null,
                                                                         byte[]? secret = null)
    {
        var tx = Transaction.Create(Network.Main);
        tx.Version = 2;
        tx.Inputs.Add(new OutPoint(uint256.One, 0), null, null, new Sequence(144));
        tx.Outputs.Add(new TxOut(Money.Satoshis(9_000), new Key().PubKey.WitHash.ScriptPubKey));
        return (tx, new SweepSigningContext(tx.ToBytes(), 0, witnessScript?.ToBytes(), 10_000, kind, point,
                                           secret is null ? (Secret?)null : new Secret(secret)));
    }

    private static byte[] SignWith(byte[] privateKey, Transaction tx, Script witnessScript) =>
        SignWith(privateKey, tx, witnessScript, witnessScript.WitHash.ScriptPubKey);

    private static byte[] SignWith(byte[] privateKey, Transaction tx, Script scriptCode, Script scriptPubKey)
    {
        using var key = new Key(privateKey);
        var sigHash = tx.GetSignatureHash(scriptCode, 0, SigHash.All, new TxOut(Money.Satoshis(10_000), scriptPubKey),
                                          HashVersion.WitnessV0);
        return key.Sign(sigHash, new SigningOptions(SigHash.All, false)).Signature.MakeCanonical().ToCompact();
    }
}