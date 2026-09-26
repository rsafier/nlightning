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

    private static NodeASigner CreateNodeASigner(bool dataLossDetected = false)
    {
        var signer = new NodeASigner();
        signer.RegisterChannel(s_channelId, SigningInfo(dataLossDetected));
        return signer;
    }

    private static ChannelSigningInfo SigningInfo(bool dataLossDetected) =>
        new(Bolt3AppendixBVectors.ExpectedTxId.ToBytes(), 0, Bolt3AppendixBVectors.FundingSatoshis,
            Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(), Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(), 0,
            dataLossDetected: dataLossDetected);

    private static (SignedTransaction Unsigned, Domain.Crypto.ValueObjects.CompactSignature Remote) UnsignedCommitTx0()
    {
        var tx = Transaction.Parse(SignedCommitTx0Hex, Network.Main);
        tx.Inputs[0].WitScript = WitScript.Empty;
        return (new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()),
                Bolt3AppendixCVectors.NodeBSignature0.ToCompact());
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