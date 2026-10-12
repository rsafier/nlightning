using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.Constants;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Constants;
using NLightning.Domain.Protocol.Tlv;
using NLightning.Domain.Protocol.ValueObjects;

namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using System.Runtime.Serialization;

using Domain.Bitcoin.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// The wire definitions of BOLT 2 <c>option_simple_close</c>'s <c>closing_complete</c> (40) and <c>closing_sig</c>
/// (41): the shared fixed fields, then the <c>closing_tlvs</c> — the ECDSA signatures (types 1, 2 and 3, each a raw
/// 64-byte value) and the simple taproot ones (types 5, 6 and 7: 98-byte partial signatures with the closer's nonce
/// on <c>closing_complete</c>, 32-byte ones on <c>closing_sig</c>), plus <c>closing_sig</c>'s
/// <c>next_closee_nonce</c> (22). Like the hand-written pair, every failure — body or <c>closing_tlvs</c> — surfaces
/// as <see cref="MessageSerializationException"/>, which <paramref name="wrapBodyErrors"/> reproduces.
/// </summary>
internal static class ClosingCompleteWire
{
    public static readonly MessageWire<ClosingCompleteMessage> Def =
        new(MessageTypes.ClosingComplete, Encode, Decode, false, false, wrapBodyErrors: true,
            TlvDef.Raw(ClosingSignatures.CloserOutputOnlyType, CryptoConstants.MaxSignatureSize),
            TlvDef.Raw(ClosingSignatures.CloseeOutputOnlyType, CryptoConstants.MaxSignatureSize),
            TlvDef.Raw(ClosingSignatures.CloserAndCloseeOutputsType, CryptoConstants.MaxSignatureSize),
            TlvDef.Raw(TaprootTlvConstants.CloserNoClosee, MusigConstants.PartialSignatureWithNonceLen),
            TlvDef.Raw(TaprootTlvConstants.NoCloserClosee, MusigConstants.PartialSignatureWithNonceLen),
            TlvDef.Raw(TaprootTlvConstants.CloserAndClosee, MusigConstants.PartialSignatureWithNonceLen));

    private static void Encode(ref WireWriter writer, ClosingCompleteMessage message)
    {
        WriteBody(ref writer, message.Payload);
        // the closing_tlvs: the constructor built the Extension with SimpleClosingTlvs.ToStream
    }

    private static WireConstruct<ClosingCompleteMessage> Decode(ref WireReader reader)
    {
        var (channelId, closerScript, closeeScript, feeSatoshis, lockTime) = ReadBody(ref reader);

        return tlvs => new ClosingCompleteMessage(
            new ClosingCompletePayload(channelId, closerScript, closeeScript, feeSatoshis, lockTime),
            new ClosingSignatures(
                Signature(tlvs, ClosingSignatures.CloserOutputOnlyType),
                Signature(tlvs, ClosingSignatures.CloseeOutputOnlyType),
                Signature(tlvs, ClosingSignatures.CloserAndCloseeOutputsType)),
            new ClosingPartialSignaturesWithNonce(
                PartialSignatureWithNonce(tlvs, TaprootTlvConstants.CloserNoClosee),
                PartialSignatureWithNonce(tlvs, TaprootTlvConstants.NoCloserClosee),
                PartialSignatureWithNonce(tlvs, TaprootTlvConstants.CloserAndClosee)));
    }

    internal static void WriteBody(ref WireWriter writer, SimpleClosingPayload payload)
    {
        writer.ChannelId(payload.ChannelId);
        writer.U16((ushort)payload.CloserScriptPubKey.Length);
        writer.Bytes(payload.CloserScriptPubKey);
        writer.U16((ushort)payload.CloseeScriptPubKey.Length);
        writer.Bytes(payload.CloseeScriptPubKey);
        writer.U64((ulong)payload.FeeSatoshis.Satoshi);
        writer.U32(payload.LockTime);
    }

    internal static (ChannelId ChannelId, BitcoinScript CloserScript, BitcoinScript CloseeScript,
        LightningMoney FeeSatoshis, uint LockTime) ReadBody(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var closerScript = new BitcoinScript(ReadScript(ref reader));
        var closeeScript = new BitcoinScript(ReadScript(ref reader));
        var feeSatoshis = LightningMoney.Satoshis(reader.U64());
        var lockTime = reader.U32();

        return (channelId, closerScript, closeeScript, feeSatoshis, lockTime);
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

    /// <summary>The 64-byte ECDSA <c>closing_tlvs</c> signature of <paramref name="type"/>, or null when absent.</summary>
    internal static CompactSignature? Signature(WireTlvs tlvs, BigSize type)
    {
        return tlvs.Get<byte[]>(type) is { } value ? new CompactSignature(value) : null;
    }

    /// <summary>The 98-byte MuSig2 <c>closing_tlvs</c> signature of <paramref name="type"/>, or null when absent.</summary>
    internal static MusigPartialSignatureWithNonce? PartialSignatureWithNonce(WireTlvs tlvs, BigSize type)
    {
        return tlvs.Get<byte[]>(type) is { } value ? new MusigPartialSignatureWithNonce(value) : null;
    }

    /// <summary>The 32-byte MuSig2 <c>closing_tlvs</c> signature of <paramref name="type"/>, or null when absent.</summary>
    internal static MusigPartialSignature? PartialSignature(WireTlvs tlvs, BigSize type)
    {
        return tlvs.Get<byte[]>(type) is { } value ? new MusigPartialSignature(value) : null;
    }
}

internal static class ClosingSigWire
{
    public static readonly TlvDef<NextCloseeNonceTlv> NextCloseeNonce =
        TlvDefs.PublicNonce(TaprootTlvConstants.NextCloseeNonce, value => new NextCloseeNonceTlv(value));

    public static readonly MessageWire<ClosingSigMessage> Def =
        new(MessageTypes.ClosingSig, Encode, Decode, false, false, wrapBodyErrors: true,
            TlvDef.Raw(ClosingSignatures.CloserOutputOnlyType, CryptoConstants.MaxSignatureSize),
            TlvDef.Raw(ClosingSignatures.CloseeOutputOnlyType, CryptoConstants.MaxSignatureSize),
            TlvDef.Raw(ClosingSignatures.CloserAndCloseeOutputsType, CryptoConstants.MaxSignatureSize),
            TlvDef.Raw(TaprootTlvConstants.CloserNoClosee, MusigConstants.PartialSignatureLen),
            TlvDef.Raw(TaprootTlvConstants.NoCloserClosee, MusigConstants.PartialSignatureLen),
            TlvDef.Raw(TaprootTlvConstants.CloserAndClosee, MusigConstants.PartialSignatureLen),
            ClosingSigWire.NextCloseeNonce);

    private static void Encode(ref WireWriter writer, ClosingSigMessage message)
    {
        ClosingCompleteWire.WriteBody(ref writer, message.Payload);
        // the closing_tlvs: the constructor built the Extension with SimpleClosingTlvs.ToStream
    }

    private static WireConstruct<ClosingSigMessage> Decode(ref WireReader reader)
    {
        var (channelId, closerScript, closeeScript, feeSatoshis, lockTime) = ClosingCompleteWire.ReadBody(ref reader);

        return tlvs => new ClosingSigMessage(
            new ClosingSigPayload(channelId, closerScript, closeeScript, feeSatoshis, lockTime),
            new ClosingSignatures(
                ClosingCompleteWire.Signature(tlvs, ClosingSignatures.CloserOutputOnlyType),
                ClosingCompleteWire.Signature(tlvs, ClosingSignatures.CloseeOutputOnlyType),
                ClosingCompleteWire.Signature(tlvs, ClosingSignatures.CloserAndCloseeOutputsType)),
            new ClosingPartialSignatures(
                ClosingCompleteWire.PartialSignature(tlvs, TaprootTlvConstants.CloserNoClosee),
                ClosingCompleteWire.PartialSignature(tlvs, TaprootTlvConstants.NoCloserClosee),
                ClosingCompleteWire.PartialSignature(tlvs, TaprootTlvConstants.CloserAndClosee)),
            tlvs.Get<NextCloseeNonceTlv>(TaprootTlvConstants.NextCloseeNonce));
    }
}