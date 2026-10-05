namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using System.Runtime.Serialization;

using Domain.Bitcoin.Constants;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;

/// <summary>
/// The wire definitions of BOLT 2 <c>shutdown</c> (38; simple taproot <c>shutdown_nonce</c> 8) and
/// <c>closing_signed</c> (39; the <c>closing_signed_tlvs</c> fee_range 1).
/// </summary>
internal static class ShutdownWire
{
    public static readonly MessageWire<ShutdownMessage> Def = new(MessageTypes.Shutdown, Encode, Decode,
        TlvDef.Typed<ShutdownNonceTlv>(TaprootTlvConstants.ShutdownNonce));

    private static void Encode(ref WireWriter writer, ShutdownMessage message)
    {
        writer.ChannelId(message.Payload.ChannelId);
        writer.U16(message.Payload.ScriptPubkeyLen);
        writer.Bytes(message.Payload.ScriptPubkey);
    }

    private static WireConstruct<ShutdownMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var scriptPubkey = new BitcoinScript(ReadScript(ref reader));

        return tlvs => new ShutdownMessage(new ShutdownPayload(channelId, scriptPubkey),
                                           tlvs.Get<ShutdownNonceTlv>(TaprootTlvConstants.ShutdownNonce));
    }

    /// <summary>A u16-length-prefixed script, bounded like the hand-written payload serializer was.</summary>
    private static byte[] ReadScript(ref WireReader reader)
    {
        var length = reader.U16();
        if (length > ScriptConstants.MaxScriptSize)
            throw new SerializationException(
                $"Script length {length} exceeds maximum size {ScriptConstants.MaxScriptSize}");

        return reader.BytesArray(length);
    }
}

internal static class ClosingSignedWire
{
    public static readonly MessageWire<ClosingSignedMessage> Def = new(MessageTypes.ClosingSigned, Encode, Decode,
        TlvDef.Typed<FeeRangeTlv>(TlvConstants.FeeRange));

    private static void Encode(ref WireWriter writer, ClosingSignedMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.U64((ulong)payload.FeeAmount.Satoshi);
        writer.Bytes(payload.Signature.Value);
    }

    private static WireConstruct<ClosingSignedMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var feeAmount = LightningMoney.Satoshis(reader.U64());
        var signature = new CompactSignature(reader.BytesArray(CryptoConstants.MaxSignatureSize));

        return tlvs => new ClosingSignedMessage(new ClosingSignedPayload(channelId, feeAmount, signature),
                                                tlvs.Get<FeeRangeTlv>(TlvConstants.FeeRange));
    }
}