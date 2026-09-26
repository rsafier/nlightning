using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// BOLT2 plan N9-T4: <see cref="LocalLightningSigner.SignLocalCommitmentForBroadcast"/> (the fully signed commitment
/// for the fail-the-channel broadcast, byte-exact against BOLT 3 Appendix C) and the signer's refusals after data loss
/// and for revoked commitments (I4, I12).
/// </summary>
public class LocalLightningSignerBroadcastTests
{
    /// <summary>BOLT 3 Appendix C "simple commitment tx with no HTLCs", fully signed (node A holds it).</summary>
    private const string SignedCommitTx0Hex =
        "02000000000101bef67e4e2fb9ddeeb3461973cd4c62abb35050b1add772995b820b584a488489000000000038b02b8002c0c62d0000000000160014cc1b07838e387deacd0e5232e1e8b49f4c29e48454a56a00000000002200204adb4e2f00643db396dd120d4e7dc17625f5f2c11a40d857accc862d6b7dd80e04004730440220616210b2cc4d3afb601013c373bbd8aac54febd9f15400379a8cb65ce7deca60022034236c010991beb7ff770510561ae8dc885b8d38d1947248c38f2ae05564714201483045022100c3127b33dcc741dd6b05b1e63cbd1a9a7d816f37af9b6756fa2376b056f032370220408b96279808fe57eb7e463710804cdf4f108388bc5cf722d8c848d2c7f9f3b001475221023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb21030e9f7b623d2ccc7c9bd44d66d5ce21ce504c0acf6385a132cec6d3c39fa711c152ae3e195220";

    private static readonly ChannelId s_channelId = ChannelId.Zero;

    [Fact]
    public void Given_AppendixCCommitment_When_SigningForBroadcast_Then_SignedTxMatchesVectorByteForByte()
    {
        // Arrange
        var signer = CreateNodeASigner();
        var (unsigned, remoteSignature) = UnsignedCommitTx0();

        // Act
        var signed = signer.SignLocalCommitmentForBroadcast(s_channelId, 0, unsigned, remoteSignature);

        // Assert: the witness is 0 <sig A> <sig B> <2-of-2 script> in key order, and the tx is the spec's
        Assert.Equal(SignedCommitTx0Hex, Convert.ToHexString(signed.RawTxBytes).ToLowerInvariant());
        Assert.Equal(unsigned.TxId, signed.TxId);
    }

    [Fact]
    public void Given_AppendixCCommitment_When_SigningForBroadcast_Then_WitnessSpendsFundingOutput()
    {
        // Arrange
        var signer = CreateNodeASigner();
        var (unsigned, remoteSignature) = UnsignedCommitTx0();

        // Act
        var signed = signer.SignLocalCommitmentForBroadcast(s_channelId, 0, unsigned, remoteSignature);

        // Assert: script evaluation of the 2-of-2 P2WSH funding output succeeds with both signatures
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        var fundingScript = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(
            2, Bolt3AppendixCVectors.NodeAFundingPubkey, Bolt3AppendixCVectors.NodeBFundingPubkey);
        var spent = new TxOut(Money.Satoshis(10_000_000), fundingScript.WitHash.ScriptPubKey);
        Assert.True(tx.Inputs.AsIndexedInputs().First().VerifyScript(spent, out var error), error.ToString());
        Assert.Equal(4, tx.Inputs[0].WitScript.PushCount);
    }

    [Fact]
    public void Given_DataLossMarked_When_Signing_Then_EverySignatureRefused()
    {
        // Arrange
        var signer = CreateNodeASigner();
        var (unsigned, remoteSignature) = UnsignedCommitTx0();

        // Act
        signer.MarkDataLoss(s_channelId);

        // Assert (I12): no broadcast, no commitment signature, no HTLC signatures for new updates
        Assert.Throws<SignerException>(() => signer.SignLocalCommitmentForBroadcast(s_channelId, 0, unsigned,
                                                                                    remoteSignature));
        Assert.Throws<SignerException>(() => signer.SignChannelTransaction(s_channelId, unsigned));
        Assert.Throws<SignerException>(() => signer.SignRemoteHtlcTransactions(s_channelId, []));
    }

    [Fact]
    public void Given_RegisteredWithDataLoss_When_SigningForBroadcast_Then_Refused()
    {
        // Arrange: a channel reloaded with its persisted DataLossDetected flag
        var signer = CreateNodeASigner(dataLossDetected: true);
        var (unsigned, remoteSignature) = UnsignedCommitTx0();

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignLocalCommitmentForBroadcast(s_channelId, 0, unsigned,
                                                                                    remoteSignature));
    }

    [Fact]
    public void Given_DataLossMarked_When_RegisteredAgainWithoutFlag_Then_StillRefused()
    {
        // Arrange
        var signer = CreateNodeASigner();
        signer.MarkDataLoss(s_channelId);

        // Act: sticky, a registration never clears it
        signer.RegisterChannel(s_channelId, SigningInfo(dataLossDetected: false));
        var (unsigned, remoteSignature) = UnsignedCommitTx0();

        // Assert
        Assert.Throws<SignerException>(() => signer.SignLocalCommitmentForBroadcast(s_channelId, 0, unsigned,
                                                                                    remoteSignature));
    }

    [Fact]
    public void Given_RevokedCommitment_When_SigningForBroadcast_Then_Refused()
    {
        // Arrange: commitment 1 is persisted, so commitment 0 is revoked (I4)
        var signer = CreateNodeASigner();
        signer.AdvanceLocalCommitment(s_channelId, 1);
        var (unsigned, remoteSignature) = UnsignedCommitTx0();

        // Act
        var exception = Assert.Throws<SignerException>(() => signer.SignLocalCommitmentForBroadcast(
                                                           s_channelId, 0, unsigned, remoteSignature));

        // Assert
        Assert.Contains("revoked", exception.Message);
    }

    [Fact]
    public void Given_WrongRemoteSignature_When_SigningForBroadcast_Then_Refused()
    {
        // Arrange: node A's own signature is not node B's
        var signer = CreateNodeASigner();
        var (unsigned, _) = UnsignedCommitTx0();

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignLocalCommitmentForBroadcast(
                                           s_channelId, 0, unsigned,
                                           Bolt3AppendixCVectors.NodeASignature0.ToCompact()));
    }

    [Fact]
    public void Given_UnregisteredChannel_When_SigningForBroadcast_Then_Refused()
    {
        // Arrange
        var signer = new NodeASigner();
        var (unsigned, remoteSignature) = UnsignedCommitTx0();

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.SignLocalCommitmentForBroadcast(s_channelId, 0, unsigned,
                                                                                    remoteSignature));
    }

    [Fact]
    public void Given_BroadcastSigned_When_RevealingItsSecretOrSigningLater_Then_Refused()
    {
        // Arrange: commitment 0 is current; commitment 1 would supersede it
        var signer = CreateNodeASigner(withChannelKey: true);
        var (unsigned, remoteSignature) = UnsignedCommitTx0();

        // Act
        _ = signer.SignLocalCommitmentForBroadcast(s_channelId, 0, unsigned, remoteSignature);

        // Assert (S1): no later local commitment, so no revoke_and_ack for 0 can ever be built, and no new signatures
        Assert.True(signer.TryGetBroadcastSignedCommitment(s_channelId, out var marked));
        Assert.Equal(0UL, marked);
        Assert.Throws<SignerException>(() => signer.AdvanceLocalCommitment(s_channelId, 1));
        Assert.Throws<SignerException>(() => signer.RevealPerCommitmentSecret(s_channelId, 0));
        Assert.Throws<SignerException>(() => signer.SignChannelTransaction(s_channelId, unsigned));
        Assert.Throws<SignerException>(() => signer.SignRemoteHtlcTransactions(s_channelId, []));
    }

    [Fact]
    public void Given_BroadcastSignedAtN_When_RevealingOlderSecret_Then_StillReleased()
    {
        // Arrange: commitments 0..2 exist, 2 is current and signed for broadcast
        var signer = CreateNodeASigner(withChannelKey: true);
        signer.AdvanceLocalCommitment(s_channelId, 2);
        signer.MarkBroadcastSigned(s_channelId, 2);

        // Act: 0 and 1 are revoked already, their secrets may be sent again (channel_reestablish)
        var secret1 = signer.RevealPerCommitmentSecret(s_channelId, 1);

        // Assert
        Assert.Equal(32, ((byte[])secret1).Length);
        Assert.Throws<SignerException>(() => signer.RevealPerCommitmentSecret(s_channelId, 2));
        Assert.Throws<SignerException>(() => signer.RevealPerCommitmentSecret(s_channelId, 3));
    }

    [Fact]
    public void Given_BroadcastSigned_When_SigningSameCommitmentAgain_Then_SameTransaction()
    {
        // Arrange: a retry of the fail-the-channel broadcast (e.g. after a restart) signs the same number again
        var signer = CreateNodeASigner();
        var (unsigned, remoteSignature) = UnsignedCommitTx0();
        var first = signer.SignLocalCommitmentForBroadcast(s_channelId, 0, unsigned, remoteSignature);

        // Act
        var second = signer.SignLocalCommitmentForBroadcast(s_channelId, 0, unsigned, remoteSignature);

        // Assert
        Assert.Equal(first.RawTxBytes, second.RawTxBytes);
    }

    [Fact]
    public void Given_BroadcastSignedAtN_When_SigningAnotherNumberForBroadcast_Then_Refused()
    {
        // Arrange: the signer was told of a broadcast signature for commitment 3
        var signer = CreateNodeASigner();
        signer.MarkBroadcastSigned(s_channelId, 3);
        var (unsigned, remoteSignature) = UnsignedCommitTx0();

        // Act
        var exception = Assert.Throws<SignerException>(() => signer.SignLocalCommitmentForBroadcast(
                                                           s_channelId, 0, unsigned, remoteSignature));

        // Assert
        Assert.Contains("already signed for broadcast", exception.Message);
    }

    [Fact]
    public void Given_MarkBroadcastSignedAtRegistration_When_Revoking_Then_Refused()
    {
        // Arrange: a restart. A fresh signer is told of the persisted broadcast before the first connection, and the
        // channel is registered at its persisted local commitment number
        var signer = new NodeASigner(withChannelKey: true);
        signer.MarkBroadcastSigned(s_channelId, 5);
        signer.RegisterChannel(s_channelId, SigningInfo(false, localCommitmentNumber: 5));

        // Act / Assert: secret 5 is never released and the channel cannot move past 5; older secrets still are
        Assert.Throws<SignerException>(() => signer.RevealPerCommitmentSecret(s_channelId, 5));
        Assert.Throws<SignerException>(() => signer.AdvanceLocalCommitment(s_channelId, 6));
        Assert.Throws<SignerException>(() => signer.SignChannelTransaction(s_channelId, UnsignedCommitTx0().Unsigned));
        Assert.Equal(32, ((byte[])signer.RevealPerCommitmentSecret(s_channelId, 4)).Length);
    }

    [Fact]
    public void Given_MarkedTwice_When_LowerNumberSecond_Then_LowerKept()
    {
        // Arrange
        var signer = CreateNodeASigner();

        // Act: sticky; a lower mark wins, a higher one never lifts the guard
        signer.MarkBroadcastSigned(s_channelId, 7);
        signer.MarkBroadcastSigned(s_channelId, 4);
        signer.MarkBroadcastSigned(s_channelId, 9);

        // Assert
        Assert.True(signer.TryGetBroadcastSignedCommitment(s_channelId, out var marked));
        Assert.Equal(4UL, marked);
    }

    [Fact]
    public void Given_BroadcastSigned_When_RegisteredAgain_Then_StillRefused()
    {
        // Arrange
        var signer = CreateNodeASigner(withChannelKey: true);
        var (unsigned, remoteSignature) = UnsignedCommitTx0();
        _ = signer.SignLocalCommitmentForBroadcast(s_channelId, 0, unsigned, remoteSignature);

        // Act: registration never clears the mark
        signer.RegisterChannel(s_channelId, SigningInfo(false));

        // Assert
        Assert.Throws<SignerException>(() => signer.AdvanceLocalCommitment(s_channelId, 1));
    }

    [Fact]
    public void Given_RefusedBroadcast_When_Checked_Then_NotMarked()
    {
        // Arrange: a refused signature (wrong peer signature) must not block the channel
        var signer = CreateNodeASigner(withChannelKey: true);
        var (unsigned, _) = UnsignedCommitTx0();

        // Act
        Assert.Throws<SignerException>(() => signer.SignLocalCommitmentForBroadcast(
                                           s_channelId, 0, unsigned,
                                           Bolt3AppendixCVectors.NodeASignature0.ToCompact()));

        // Assert
        Assert.False(signer.TryGetBroadcastSignedCommitment(s_channelId, out _));
        signer.AdvanceLocalCommitment(s_channelId, 1);
        Assert.Equal(32, ((byte[])signer.RevealPerCommitmentSecret(s_channelId, 0)).Length);
    }

    [Fact]
    public void Given_NumberAbove48Bits_When_MarkBroadcastSigned_Then_Throws()
    {
        // Arrange
        var signer = CreateNodeASigner();

        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => signer.MarkBroadcastSigned(s_channelId, 1UL << 48));
    }

    private static NodeASigner CreateNodeASigner(bool dataLossDetected = false, bool withChannelKey = false)
    {
        var signer = new NodeASigner(withChannelKey);
        signer.RegisterChannel(s_channelId, SigningInfo(dataLossDetected));
        return signer;
    }

    private static ChannelSigningInfo SigningInfo(bool dataLossDetected, ulong localCommitmentNumber = 0) =>
        new(Bolt3AppendixBVectors.ExpectedTxId.ToBytes(), 0, Bolt3AppendixBVectors.FundingSatoshis,
            Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(), Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(), 0,
            localCommitmentNumber: localCommitmentNumber, dataLossDetected: dataLossDetected);

    private static (SignedTransaction Unsigned, Domain.Crypto.ValueObjects.CompactSignature Remote) UnsignedCommitTx0()
    {
        var tx = Transaction.Parse(SignedCommitTx0Hex, Network.Main);
        tx.Inputs[0].WitScript = WitScript.Empty;
        return (new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()),
                Bolt3AppendixCVectors.NodeBSignature0.ToCompact());
    }

    private static ISecureKeyManager KeyManager(bool withChannelKey)
    {
        var keyManager = new Mock<ISecureKeyManager>();
        if (withChannelKey)
            keyManager.Setup(k => k.GetChannelKeyAtIndex(0)).Returns(ExtKey.CreateFromSeed(new byte[32]).ToBytes());
        return keyManager.Object;
    }

    /// <summary>A <see cref="LocalLightningSigner"/> whose funding key is Appendix C's node A funding key.</summary>
    private sealed class NodeASigner(bool withChannelKey = false)
        : LocalLightningSigner(new FundingOutputBuilder(), new KeyDerivationService(new Secp256K1Math()),
                               NullLogger<LocalLightningSigner>.Instance, new NodeOptions(), KeyManager(withChannelKey),
                               new Mock<IUtxoMemoryRepository>().Object)
    {
        protected override Key GenerateFundingPrivateKey(uint channelKeyIndex) =>
            new(Bolt3AppendixCVectors.NodeAFundingPrivkey.ToBytes());
    }
}