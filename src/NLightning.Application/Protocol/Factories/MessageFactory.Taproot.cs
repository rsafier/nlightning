namespace NLightning.Application.Protocol.Factories;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;

/// <summary>
/// The simple taproot channels (<c>option_simple_taproot</c>) and BOLTs PR #1324 overloads: the same messages with
/// their MuSig2 nonce and partial signature TLVs (NL-877).
/// </summary>
public partial class MessageFactory
{
    /// <inheritdoc />
    public OpenChannel1Message CreateOpenChannel1Message(ChannelId temporaryChannelId, LightningMoney fundingAmount,
                                                         CompactPubKey fundingPubKey, LightningMoney pushAmount,
                                                         ChannelParty localParams, LightningMoney feeRatePerKw,
                                                         CompactPubKey revocationBasepoint,
                                                         CompactPubKey paymentBasepoint,
                                                         CompactPubKey delayedPaymentBasepoint,
                                                         CompactPubKey htlcBasepoint,
                                                         CompactPubKey firstPerCommitmentPoint,
                                                         ChannelFlags channelFlags,
                                                         ChannelTypeTlv channelTypeTlv,
                                                         UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv,
                                                         MusigPublicNonce nextLocalNonce)
    {
        var message = CreateOpenChannel1Message(temporaryChannelId, fundingAmount, fundingPubKey, pushAmount,
                                                localParams, feeRatePerKw, revocationBasepoint, paymentBasepoint,
                                                delayedPaymentBasepoint, htlcBasepoint, firstPerCommitmentPoint,
                                                channelFlags, channelTypeTlv, upfrontShutdownScriptTlv);

        return new OpenChannel1Message(message.Payload, channelTypeTlv, upfrontShutdownScriptTlv,
                                       new NextLocalNonceTlv(nextLocalNonce));
    }

    /// <inheritdoc />
    public AcceptChannel1Message CreateAcceptChannel1Message(ChannelParty localParams, ChannelTypeTlv channelTypeTlv,
                                                             CompactPubKey delayedPaymentBasepoint,
                                                             CompactPubKey firstPerCommitmentPoint,
                                                             CompactPubKey fundingPubKey, CompactPubKey htlcBasepoint,
                                                             uint minimumDepth, CompactPubKey paymentBasepoint,
                                                             CompactPubKey revocationBasepoint,
                                                             ChannelId temporaryChannelId,
                                                             UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv,
                                                             MusigPublicNonce nextLocalNonce)
    {
        var message = CreateAcceptChannel1Message(localParams, channelTypeTlv, delayedPaymentBasepoint,
                                                  firstPerCommitmentPoint, fundingPubKey, htlcBasepoint, minimumDepth,
                                                  paymentBasepoint, revocationBasepoint, temporaryChannelId,
                                                  upfrontShutdownScriptTlv);

        return new AcceptChannel1Message(message.Payload, channelTypeTlv, upfrontShutdownScriptTlv,
                                         new NextLocalNonceTlv(nextLocalNonce));
    }

    /// <inheritdoc />
    public ChannelReadyMessage CreateChannelReadyMessage(ChannelId channelId, CompactPubKey secondPerCommitmentPoint,
                                                         ShortChannelId? shortChannelId,
                                                         MusigPublicNonce nextLocalNonce)
    {
        var payload = new ChannelReadyPayload(channelId, secondPerCommitmentPoint);

        return new ChannelReadyMessage(payload,
                                       shortChannelId is null ? null : new ShortChannelIdTlv(shortChannelId.Value),
                                       new NextLocalNonceTlv(nextLocalNonce));
    }

    /// <inheritdoc />
    public FundingCreatedMessage CreateFundingCreatedMessage(ChannelId temporaryChannelId, TxId fundingTxId,
                                                             ushort fundingOutputIndex,
                                                             MusigPartialSignatureWithNonce partialSignatureWithNonce)
    {
        var payload = new FundingCreatedPayload(temporaryChannelId, fundingTxId, fundingOutputIndex,
                                                CompactSignature.Zero);

        return new FundingCreatedMessage(payload, new PartialSignatureWithNonceTlv(partialSignatureWithNonce));
    }

    /// <inheritdoc />
    public FundingSignedMessage CreateFundingSignedMessage(ChannelId channelId,
                                                           MusigPartialSignatureWithNonce partialSignatureWithNonce)
    {
        var payload = new FundingSignedPayload(channelId, CompactSignature.Zero);

        return new FundingSignedMessage(payload, new PartialSignatureWithNonceTlv(partialSignatureWithNonce));
    }

    /// <inheritdoc />
    public CommitmentSignedMessage CreateCommitmentSignedMessage(ChannelId channelId,
                                                                 MusigPartialSignatureWithNonce
                                                                     partialSignatureWithNonce,
                                                                 IEnumerable<CompactSignature> htlcSignatures,
                                                                 TxId fundingTxId)
    {
        var payload = new CommitmentSignedPayload(channelId, htlcSignatures, CompactSignature.Zero);

        return new CommitmentSignedMessage(payload, new FundingTxIdTlv(fundingTxId),
                                           new PartialSignatureWithNonceTlv(partialSignatureWithNonce));
    }

    /// <inheritdoc />
    public RevokeAndAckMessage CreateRevokeAndAckMessage(ChannelId channelId, ReadOnlyMemory<byte> perCommitmentSecret,
                                                         CompactPubKey nextPerCommitmentPoint,
                                                         FundingNonces nextLocalNonces)
    {
        var payload = new RevokeAndAckPayload(channelId, nextPerCommitmentPoint, perCommitmentSecret);

        return new RevokeAndAckMessage(payload, new NextLocalNoncesTlv(nextLocalNonces));
    }

    /// <inheritdoc />
    public ChannelReestablishMessage CreateChannelReestablishMessage(ChannelId channelId, ulong nextCommitmentNumber,
                                                                     ulong nextRevocationNumber,
                                                                     ReadOnlyMemory<byte> yourLastPerCommitmentSecret,
                                                                     CompactPubKey myCurrentPerCommitmentPoint,
                                                                     FundingNonces? nextLocalNonces,
                                                                     MusigPublicNonce? currentCommitNonce = null,
                                                                     NextFundingTlv? nextFundingTlv = null,
                                                                     MyCurrentFundingLockedTlv?
                                                                         myCurrentFundingLockedTlv = null)
    {
        var payload = new ChannelReestablishPayload(channelId, myCurrentPerCommitmentPoint, nextCommitmentNumber,
                                                    nextRevocationNumber, yourLastPerCommitmentSecret);

        return new ChannelReestablishMessage(payload, nextFundingTlv, myCurrentFundingLockedTlv,
                                             nextLocalNonces is null ? null : new NextLocalNoncesTlv(nextLocalNonces),
                                             currentCommitNonce is null
                                                 ? null
                                                 : new CurrentCommitNonceTlv(currentCommitNonce.Value));
    }

    /// <inheritdoc />
    public ShutdownMessage CreateShutdownMessage(ChannelId channelId, BitcoinScript scriptPubkey,
                                                 MusigPublicNonce shutdownNonce)
    {
        var payload = new ShutdownPayload(channelId, scriptPubkey);

        return new ShutdownMessage(payload, new ShutdownNonceTlv(shutdownNonce));
    }

    /// <inheritdoc />
    public TxCompleteMessage CreateTxCompleteMessage(ChannelId channelId, MusigPublicNonce commitNonce,
                                                     MusigPublicNonce nextCommitNonce,
                                                     MusigPublicNonce? fundingNonce = null)
    {
        var payload = new TxCompletePayload(channelId);

        return new TxCompleteMessage(payload, new CommitNoncesTlv(commitNonce, nextCommitNonce),
                                     fundingNonce is null ? null : new FundingNonceTlv(fundingNonce.Value));
    }

    /// <inheritdoc />
    public TxSignaturesMessage CreateTxSignaturesMessage(ChannelId channelId, byte[] txId, List<Witness> witnesses,
                                                         MusigPartialSignatureWithNonce sharedInputPartialSignature)
    {
        var payload = new TxSignaturesPayload(channelId, txId, witnesses);

        return new TxSignaturesMessage(payload, null, new SharedInputPartialSignatureTlv(sharedInputPartialSignature));
    }
}