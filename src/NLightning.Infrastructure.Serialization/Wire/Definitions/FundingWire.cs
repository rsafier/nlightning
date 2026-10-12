namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;

/// <summary>
/// The wire definitions of BOLT 2 <c>funding_created</c> (34) and <c>funding_signed</c> (35), with the simple
/// taproot <c>partial_signature_with_nonce</c> (funding_created_tlvs/funding_signed_tlvs type 2).
/// </summary>
internal static class FundingWire
{
    public static readonly MessageWire<FundingCreatedMessage> Def = new(MessageTypes.FundingCreated, Encode, Decode,
        TlvDefs.PartialSignatureWithNonce);

    private static void Encode(ref WireWriter writer, FundingCreatedMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.Bytes(payload.FundingTxId);
        writer.U16(payload.FundingOutputIndex);
        writer.Bytes(payload.Signature.Value);
    }

    private static WireConstruct<FundingCreatedMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var fundingTxId = new TxId(reader.BytesArray(CryptoConstants.Sha256HashLen));
        var fundingOutputIndex = reader.U16();
        var signature = new CompactSignature(reader.BytesArray(CryptoConstants.MaxSignatureSize));

        return tlvs => new FundingCreatedMessage(
            new FundingCreatedPayload(channelId, fundingTxId, fundingOutputIndex, signature),
            tlvs.Get<PartialSignatureWithNonceTlv>(TaprootTlvConstants.PartialSignatureWithNonce));
    }
}

internal static class FundingSignedWire
{
    public static readonly MessageWire<FundingSignedMessage> Def = new(MessageTypes.FundingSigned, Encode, Decode,
        TlvDefs.PartialSignatureWithNonce);

    private static void Encode(ref WireWriter writer, FundingSignedMessage message)
    {
        writer.ChannelId(message.Payload.ChannelId);
        writer.Bytes(message.Payload.Signature.Value);
    }

    private static WireConstruct<FundingSignedMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var signature = new CompactSignature(reader.BytesArray(CryptoConstants.MaxSignatureSize));

        return tlvs => new FundingSignedMessage(new FundingSignedPayload(channelId, signature),
                                                tlvs.Get<PartialSignatureWithNonceTlv>(
                                                    TaprootTlvConstants.PartialSignatureWithNonce));
    }
}