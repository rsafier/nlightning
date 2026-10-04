namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;

/// <summary>
/// The wire definitions of <c>commitment_signed</c> (132: channel_id, signature, u16 num_htlcs, num_htlcs x
/// 64-byte HTLC signatures; TLVs funding_txid 1 and the taproot partial_signature_with_nonce 2),
/// <c>revoke_and_ack</c> (133: channel_id, 32-byte per_commitment_secret, 33-byte next_per_commitment_point;
/// taproot next_local_nonces 22), <c>channel_reestablish</c> (136; TLVs 1, 5, 22, 24) and <c>tx_add_input</c>
/// (66; TLVs 0, 2/1111).
/// </summary>
internal static class CommitmentSignedWire
{
    public static readonly MessageWire<CommitmentSignedMessage> Def = new(MessageTypes.CommitmentSigned, Encode,
        Decode, TlvDef.Typed<FundingTxIdTlv>(TlvConstants.FundingTxId),
        TlvDef.Typed<PartialSignatureWithNonceTlv>(TaprootTlvConstants.PartialSignatureWithNonce));

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
            tlvs.Get<FundingTxIdTlv>(0), tlvs.Get<PartialSignatureWithNonceTlv>(1));
    }
}

internal static class RevokeAndAckWire
{
    public static readonly MessageWire<RevokeAndAckMessage> Def = new(MessageTypes.RevokeAndAck, Encode, Decode,
        TlvDef.Typed<NextLocalNoncesTlv>(TaprootTlvConstants.NextLocalNonces));

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
            tlvs.Get<NextLocalNoncesTlv>(0));
    }
}

internal static class ChannelReestablishWire
{
    public static readonly MessageWire<ChannelReestablishMessage> Def = new(MessageTypes.ChannelReestablish, Encode,
        Decode, TlvDef.Typed<NextFundingTlv>(TlvConstants.NextFunding),
        TlvDef.Typed<MyCurrentFundingLockedTlv>(TlvConstants.MyCurrentFundingLocked),
        TlvDef.Typed<NextLocalNoncesTlv>(TaprootTlvConstants.NextLocalNonces),
        TlvDef.Typed<CurrentCommitNonceTlv>(TaprootTlvConstants.CurrentCommitNonce));

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
            tlvs.Get<NextFundingTlv>(0), tlvs.Get<MyCurrentFundingLockedTlv>(1),
            tlvs.Get<NextLocalNoncesTlv>(2), tlvs.Get<CurrentCommitNonceTlv>(3));
    }
}

internal static class TxAddInputWire
{
    private const int SharedInputTxIdIndex = 0;
    private const int PrevTxDetailsIndex = 1;
    private const int PrevTxDetailsEclairIndex = 2;

    public static readonly MessageWire<TxAddInputMessage> Def = new(MessageTypes.TxAddInput, Encode, Decode,
        TlvDef.Typed<SharedInputTxIdTlv>(InteractiveTxTlvConstants.SharedInputTxId),
        TlvDef.Typed<PrevTxDetailsTlv>(InteractiveTxTlvConstants.PrevTxDetails),
        TlvDef.Typed<PrevTxDetailsTlv>(InteractiveTxTlvConstants.PrevTxDetailsEclair));

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
            var prevTxDetails = tlvs.Get<PrevTxDetailsTlv>(PrevTxDetailsIndex)
                             ?? tlvs.Get<PrevTxDetailsTlv>(PrevTxDetailsEclairIndex);

            return new TxAddInputMessage(new TxAddInputPayload(channelId, serialId, prevTx, prevTxVout, sequence),
                                         tlvs.Get<SharedInputTxIdTlv>(SharedInputTxIdIndex), prevTxDetails);
        };
    }
}