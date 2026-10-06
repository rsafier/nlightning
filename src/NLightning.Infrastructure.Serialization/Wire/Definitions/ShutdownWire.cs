using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Crypto.Constants;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Enums;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Constants;
using NLightning.Domain.Protocol.Tlv;
using NLightning.Infrastructure.Converters;

namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using System.Runtime.Serialization;

using Domain.Bitcoin.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// The wire definitions of BOLT 2 <c>shutdown</c> (38; simple taproot <c>shutdown_nonce</c> 8) and
/// <c>closing_signed</c> (39; the <c>closing_signed_tlvs</c> fee_range 1).
/// </summary>
internal static class ShutdownWire
{
    public static readonly TlvDef<ShutdownNonceTlv> ShutdownNonce =
        TlvDefs.PublicNonce(TaprootTlvConstants.ShutdownNonce, value => new ShutdownNonceTlv(value));

    public static readonly MessageWire<ShutdownMessage> Def = new(MessageTypes.Shutdown, Encode, Decode,
        ShutdownWire.ShutdownNonce);

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
    public static readonly TlvDef<FeeRangeTlv> FeeRange = TlvDef.Typed<FeeRangeTlv>(TlvConstants.FeeRange,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.FeeRange)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != sizeof(ulong) * 2) // 2 long (128 bits) is 16 bytes
                throw new InvalidCastException("Invalid length");

            var minFeeAmount = LightningMoney
               .FromUnit(EndianBitConverter.ToUInt64BigEndian(baseTlv.Value[..sizeof(ulong)]), LightningMoneyUnit.Satoshi);
            var maxFeeAmount = LightningMoney
               .FromUnit(EndianBitConverter.ToUInt64BigEndian(baseTlv.Value[sizeof(ulong)..]), LightningMoneyUnit.Satoshi);

            return new FeeRangeTlv(minFeeAmount, maxFeeAmount);
        },
        tlv =>
        {
            var tlvValue = new byte[sizeof(ulong) * 2];
            EndianBitConverter.GetBytesBigEndian(tlv.MinFeeAmount.Satoshi).CopyTo(tlvValue, 0);
            EndianBitConverter.GetBytesBigEndian(tlv.MaxFeeAmount.Satoshi).CopyTo(tlvValue, sizeof(ulong));

            return new BaseTlv(tlv.Type, tlvValue);
        });

    public static readonly MessageWire<ClosingSignedMessage> Def = new(MessageTypes.ClosingSigned, Encode, Decode,
        ClosingSignedWire.FeeRange);

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