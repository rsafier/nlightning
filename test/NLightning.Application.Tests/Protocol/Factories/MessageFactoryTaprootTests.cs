using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Protocol.Factories;

using Application.Protocol.Factories;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using Domain.Protocol.Models;
using Domain.Protocol.Tlv;

/// <summary>
/// The simple taproot overloads of <see cref="MessageFactory"/> (NL-877): the zero signature and the nonce TLVs.
/// </summary>
public class MessageFactoryTaprootTests
{
    private static readonly CompactPubKey s_pubKey =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private static readonly MusigPublicNonce s_nonce = new(Enumerable.Repeat((byte)0xA1, 66).ToArray());
    private static readonly MusigPublicNonce s_nonce2 = new(Enumerable.Repeat((byte)0xB2, 66).ToArray());

    private static readonly MusigPartialSignatureWithNonce s_partialSignature =
        new(Enumerable.Repeat((byte)0x5A, 98).ToArray());

    private static readonly TxId s_txId = new(Enumerable.Repeat((byte)0x07, 32).ToArray());

    private static readonly ChannelParty s_localParams =
        new(LightningMoney.Satoshis(600), LightningMoney.Satoshis(2_500), LightningMoney.MilliSatoshis(1_234), 42,
            LightningMoney.Satoshis(77_000), 720);

    private readonly MessageFactory _messageFactory = new(Options.Create(new NodeOptions()));

    [Fact]
    public void Given_Nonce_When_CreatingOpenAndAcceptChannel1_Then_NextLocalNonceTlvIsLast()
    {
        // Arrange
        var channelTypeTlv = new ChannelTypeTlv(FeatureSet.NewBasicChannelType());

        // Act
        var open = _messageFactory.CreateOpenChannel1Message(ChannelId.Zero, LightningMoney.Satoshis(100_000),
                                                             s_pubKey, LightningMoney.Zero, s_localParams,
                                                             LightningMoney.Satoshis(253), s_pubKey, s_pubKey,
                                                             s_pubKey, s_pubKey, s_pubKey,
                                                             new ChannelFlags(ChannelFlag.None), channelTypeTlv,
                                                             null, s_nonce);
        var accept = _messageFactory.CreateAcceptChannel1Message(s_localParams, channelTypeTlv, s_pubKey, s_pubKey,
                                                                 s_pubKey, s_pubKey, 3, s_pubKey, s_pubKey,
                                                                 ChannelId.Zero, null, s_nonce);

        // Assert
        Assert.Equal(s_nonce, open.NextLocalNonceTlv!.Nonce);
        Assert.Equal(s_localParams.ToSelfDelay, open.Payload.ToSelfDelay);
        Assert.IsType<NextLocalNonceTlv>(open.Extension!.GetTlvs().Last());
        Assert.Equal(s_nonce, accept.NextLocalNonceTlv!.Nonce);
        Assert.Equal(3U, accept.Payload.MinimumDepth);
    }

    [Fact]
    public void Given_PartialSignature_When_CreatingFundingAndCommitmentMessages_Then_SignatureFieldIsZero()
    {
        // Act
        var created = _messageFactory.CreateFundingCreatedMessage(ChannelId.Zero, s_txId, 1, s_partialSignature);
        var signed = _messageFactory.CreateFundingSignedMessage(ChannelId.Zero, s_partialSignature);
        var commitment = _messageFactory.CreateCommitmentSignedMessage(ChannelId.Zero, s_partialSignature, [],
                                                                       s_txId);

        // Assert
        Assert.True(created.Payload.Signature.IsZero);
        Assert.Equal(s_partialSignature, created.PartialSignatureWithNonceTlv!.PartialSignatureWithNonce);
        Assert.True(signed.Payload.Signature.IsZero);
        Assert.Equal(s_partialSignature, signed.PartialSignatureWithNonceTlv!.PartialSignatureWithNonce);
        Assert.True(commitment.Payload.Signature.IsZero);
        Assert.Equal(s_txId, commitment.FundingTxIdTlv!.FundingTxId);
        Assert.Equal(s_partialSignature, commitment.PartialSignatureWithNonceTlv!.PartialSignatureWithNonce);
    }

    [Fact]
    public void Given_Nonces_When_CreatingRevokeReadyShutdownAndReestablish_Then_TlvsSet()
    {
        // Arrange
        var nonces = FundingNonces.Single(s_txId, s_nonce);

        // Act
        var revoke = _messageFactory.CreateRevokeAndAckMessage(ChannelId.Zero, new byte[32], s_pubKey, nonces);
        var ready = _messageFactory.CreateChannelReadyMessage(ChannelId.Zero, s_pubKey, null, s_nonce);
        var shutdown = _messageFactory.CreateShutdownMessage(ChannelId.Zero, new BitcoinScript([0x51, 0x20, .. new byte[32]]),
                                                             s_nonce2);
        var reestablish = _messageFactory.CreateChannelReestablishMessage(ChannelId.Zero, 1, 0, new byte[32],
                                                                          s_pubKey, nonces, s_nonce2);
        var plainReestablish = _messageFactory.CreateChannelReestablishMessage(ChannelId.Zero, 1, 0, new byte[32],
                                                                               s_pubKey, null);

        // Assert
        Assert.Same(nonces, revoke.NextLocalNoncesTlv!.Nonces);
        Assert.Null(ready.ShortChannelIdTlv);
        Assert.Equal(s_nonce, ready.NextLocalNonceTlv!.Nonce);
        Assert.Equal(s_nonce2, shutdown.ShutdownNonceTlv!.Nonce);
        Assert.Same(nonces, reestablish.NextLocalNoncesTlv!.Nonces);
        Assert.Equal(s_nonce2, reestablish.CurrentCommitNonceTlv!.Nonce);
        Assert.Null(plainReestablish.Extension);
    }

    [Fact]
    public void Given_Nonces_When_CreatingTxCompleteAndTxSignatures_Then_TlvsSet()
    {
        // Act
        var complete = _messageFactory.CreateTxCompleteMessage(ChannelId.Zero, s_nonce, s_nonce2);
        var spliceComplete = _messageFactory.CreateTxCompleteMessage(ChannelId.Zero, s_nonce, s_nonce2, s_nonce);
        var signatures = _messageFactory.CreateTxSignaturesMessage(ChannelId.Zero, new byte[32], [],
                                                                   s_partialSignature);

        // Assert
        Assert.Equal(s_nonce, complete.CommitNoncesTlv!.CommitNonce);
        Assert.Equal(s_nonce2, complete.CommitNoncesTlv.NextCommitNonce);
        Assert.Null(complete.FundingNonceTlv);
        Assert.Equal(s_nonce, spliceComplete.FundingNonceTlv!.Nonce);
        Assert.Null(signatures.SharedInputSignatureTlv);
        Assert.Equal(s_partialSignature,
                     signatures.SharedInputPartialSignatureTlv!.PartialSignatureWithNonce);
    }
}