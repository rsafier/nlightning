using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Crypto.Constants;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Protocol.Constants;
using NLightning.Domain.Protocol.Tlv;

namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// The wire definitions of <c>commitment_signed</c> (132: channel_id, signature, u16 num_htlcs, num_htlcs x
/// 64-byte HTLC signatures; TLVs funding_txid 1 and the taproot partial_signature_with_nonce 2),
/// <c>revoke_and_ack</c> (133: channel_id, 32-byte per_commitment_secret, 33-byte next_per_commitment_point;
/// taproot next_local_nonces 22), <c>channel_reestablish</c> (136; TLVs 1, 5, 22, 24) and <c>tx_add_input</c>
/// (66; TLVs 0, 2/1111).
/// </summary>
internal static class CommitmentSignedWire
{
    public static readonly TlvDef<FundingTxIdTlv> FundingTxId = TlvDef.Typed<FundingTxIdTlv>(TlvConstants.FundingTxId,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.FundingTxId)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != FundingTxIdTlv.ValueLength)
                throw new InvalidCastException("Invalid length");

            return new FundingTxIdTlv(baseTlv.Value[..FundingTxIdTlv.ValueLength]);
        },
        tlv => tlv);

    public static readonly MessageWire<CommitmentSignedMessage> Def = new(MessageTypes.CommitmentSigned, Encode,
        Decode, CommitmentSignedWire.FundingTxId,
        TlvDefs.PartialSignatureWithNonce);

    private static void Encode(ref WireWriter writer, CommitmentSignedMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.Bytes(payload.Signature);
        writer.U16(payload.NumHtlcs);
        foreach (var htlcSignature in payload.HtlcSignatures)
            writer.Bytes(htlcSignature);
    }

    private static WireConstruct<CommitmentSignedMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var signature = new CompactSignature(reader.BytesArray(CryptoConstants.MaxSignatureSize));
        var numHtlcs = reader.U16();
        var htlcSignatures = new List<CompactSignature>(numHtlcs);
        for (var i = 0; i < numHtlcs; i++)
            htlcSignatures.Add(new CompactSignature(reader.BytesArray(CryptoConstants.MaxSignatureSize)));

        return tlvs => new CommitmentSignedMessage(
            new CommitmentSignedPayload(channelId, htlcSignatures, signature),
            tlvs.Get<FundingTxIdTlv>(TlvConstants.FundingTxId),
            tlvs.Get<PartialSignatureWithNonceTlv>(TaprootTlvConstants.PartialSignatureWithNonce));
    }
}

internal static class RevokeAndAckWire
{
    public static readonly MessageWire<RevokeAndAckMessage> Def = new(MessageTypes.RevokeAndAck, Encode, Decode,
        TlvDefs.NextLocalNonces);

    private static void Encode(ref WireWriter writer, RevokeAndAckMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.Bytes(payload.PerCommitmentSecret.Span);
        writer.CompactPubKey(payload.NextPerCommitmentPoint);
    }

    private static WireConstruct<RevokeAndAckMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var perCommitmentSecret = reader.BytesArray(CryptoConstants.Sha256HashLen);
        var nextPerCommitmentPoint = reader.CompactPubKey();

        return tlvs => new RevokeAndAckMessage(
            new RevokeAndAckPayload(channelId, nextPerCommitmentPoint, perCommitmentSecret),
            tlvs.Get<NextLocalNoncesTlv>(TaprootTlvConstants.NextLocalNonces));
    }
}

internal static class ChannelReestablishWire
{
    public static readonly TlvDef<AnnouncementNoncesTlv> AnnouncementNonces = TlvDef.Typed<AnnouncementNoncesTlv>(TaprootTlvConstants.AnnouncementNonces,
        baseTlv =>
        {
            if (baseTlv.Type != TaprootTlvConstants.AnnouncementNonces)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != AnnouncementNoncesTlv.ValueLength || baseTlv.Value.Length != baseTlv.Length)
                throw new InvalidCastException(
                    $"Invalid length: announcement_nonces holds {AnnouncementNoncesTlv.ValueLength} bytes, not "
                  + $"{baseTlv.Value.Length}");

            return new AnnouncementNoncesTlv(new MusigPublicNonce(baseTlv.Value[..MusigConstants.PublicNonceLen]),
                                             new MusigPublicNonce(baseTlv.Value[MusigConstants.PublicNonceLen..]));
        },
        tlv => tlv);

    public static readonly TlvDef<MyCurrentFundingLockedTlv> MyCurrentFundingLocked = TlvDef.Typed<MyCurrentFundingLockedTlv>(TlvConstants.MyCurrentFundingLocked,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.MyCurrentFundingLocked)
            {
                throw new InvalidCastException("Invalid TLV type");
            }

            if (baseTlv.Length != MyCurrentFundingLockedTlv.ValueLength || baseTlv.Value.Length != baseTlv.Length)
            {
                throw new InvalidCastException("Invalid length");
            }

            var txId = new TxId(baseTlv.Value[..CryptoConstants.Sha256HashLen]);
            return new MyCurrentFundingLockedTlv(txId, baseTlv.Value[CryptoConstants.Sha256HashLen]);
        },
        tlv => tlv);

    public static readonly TlvDef<NextFundingTlv> NextFunding = TlvDef.Typed<NextFundingTlv>(TlvConstants.NextFunding,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.NextFunding)
            {
                throw new InvalidCastException("Invalid TLV type");
            }

            if (baseTlv.Length != NextFundingTlv.ValueLength)
            {
                throw new InvalidCastException("Invalid length");
            }

            return new NextFundingTlv(baseTlv.Value[..32], baseTlv.Value[32]);
        },
        tlv => tlv);

    public static readonly TlvDef<CurrentCommitNonceTlv> CurrentCommitNonce =
        TlvDefs.PublicNonce(TaprootTlvConstants.CurrentCommitNonce, value => new CurrentCommitNonceTlv(value));

    public static readonly MessageWire<ChannelReestablishMessage> Def = new(MessageTypes.ChannelReestablish, Encode,
        Decode, ChannelReestablishWire.NextFunding,
        ChannelReestablishWire.MyCurrentFundingLocked,
        TlvDefs.NextLocalNonces,
        ChannelReestablishWire.CurrentCommitNonce,
        ChannelReestablishWire.AnnouncementNonces);

    private static void Encode(ref WireWriter writer, ChannelReestablishMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.U64(payload.NextCommitmentNumber);
        writer.U64(payload.NextRevocationNumber);
        writer.Bytes(payload.YourLastPerCommitmentSecret.Span);
        writer.CompactPubKey(payload.MyCurrentPerCommitmentPoint);
    }

    private static WireConstruct<ChannelReestablishMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var nextCommitmentNumber = reader.U64();
        var nextRevocationNumber = reader.U64();
        var yourLastPerCommitmentSecret = reader.BytesArray(CryptoConstants.Sha256HashLen);
        var myCurrentPerCommitmentPoint = reader.CompactPubKey();

        return tlvs => new ChannelReestablishMessage(
            new ChannelReestablishPayload(channelId, myCurrentPerCommitmentPoint, nextCommitmentNumber,
                                          nextRevocationNumber, yourLastPerCommitmentSecret),
            tlvs.Get<NextFundingTlv>(TlvConstants.NextFunding),
            tlvs.Get<MyCurrentFundingLockedTlv>(TlvConstants.MyCurrentFundingLocked),
            tlvs.Get<NextLocalNoncesTlv>(TaprootTlvConstants.NextLocalNonces),
            tlvs.Get<CurrentCommitNonceTlv>(TaprootTlvConstants.CurrentCommitNonce),
            tlvs.Get<AnnouncementNoncesTlv>(TaprootTlvConstants.AnnouncementNonces));
    }
}

internal static class TxAddInputWire
{
    public static readonly TlvDef<SharedInputTxIdTlv> SharedInputTxId = TlvDef.Typed<SharedInputTxIdTlv>(InteractiveTxTlvConstants.SharedInputTxId,
        baseTlv =>
        {
            if (baseTlv.Type != InteractiveTxTlvConstants.SharedInputTxId)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != SharedInputTxIdTlv.ValueLength || baseTlv.Value.Length != SharedInputTxIdTlv.ValueLength)
                throw new InvalidCastException("Invalid length");

            return new SharedInputTxIdTlv(baseTlv.Value[..SharedInputTxIdTlv.ValueLength]);
        },
        tlv => tlv);

    public static readonly MessageWire<TxAddInputMessage> Def = new(MessageTypes.TxAddInput, Encode, Decode,
        TxAddInputWire.SharedInputTxId,
        TlvDefs.PrevTxDetails,
        TlvDefs.PrevTxDetails.WithType(InteractiveTxTlvConstants.PrevTxDetailsEclair));

    private static void Encode(ref WireWriter writer, TxAddInputMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.U64(payload.SerialId);
        writer.U16((ushort)payload.PrevTx.Length);
        writer.Bytes(payload.PrevTx);
        writer.U32(payload.PrevTxVout);
        writer.U32(payload.Sequence);
    }

    private static WireConstruct<TxAddInputMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var serialId = reader.U64();
        var prevTxLength = reader.U16();
        var prevTx = reader.BytesArray(prevTxLength);
        var prevTxVout = reader.U32();
        var sequence = reader.U32();

        return tlvs =>
        {
            // prevtx_details: the spec's type 2 wins over Eclair's prototype 1111 when a peer sends both (NL-957)
            var prevTxDetails = tlvs.Get<PrevTxDetailsTlv>(InteractiveTxTlvConstants.PrevTxDetails)
                             ?? tlvs.Get<PrevTxDetailsTlv>(InteractiveTxTlvConstants.PrevTxDetailsEclair);

            return new TxAddInputMessage(new TxAddInputPayload(channelId, serialId, prevTx, prevTxVout, sequence),
                                         tlvs.Get<SharedInputTxIdTlv>(InteractiveTxTlvConstants.SharedInputTxId),
                                         prevTxDetails);
        };
    }
}