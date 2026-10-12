using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.Constants;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Onion.Codecs;
using NLightning.Domain.Protocol.Onion.Constants;
using NLightning.Domain.Protocol.Onion.Tlv;
using NLightning.Domain.Protocol.Tlv;

namespace NLightning.Infrastructure.Serialization.Wire;

/// <summary>Value definitions for the dedicated BOLT 4 hop reader, which retains error type and offset.</summary>
public static class HopTlvDefs
{
    public static readonly TlvDef<AmtToForwardTlv> AmtToForward = TlvDef.Typed<AmtToForwardTlv>(OnionPayloadTlvTypes.AmtToForward,
        baseTlv =>
        {
            if (baseTlv.Type != OnionPayloadTlvTypes.AmtToForward)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != (ulong)baseTlv.Value.Length
             || !TruncatedInt.TryDecodeTu64(baseTlv.Value, out var amount))
                throw new InvalidCastException("Invalid length");

            return new AmtToForwardTlv(LightningMoney.MilliSatoshis(amount));
        },
        tlv => new BaseTlv(tlv.Type, TruncatedInt.EncodeTu64(tlv.AmountToForward.MilliSatoshi)));

    public static readonly TlvDef<CurrentPathKeyTlv> CurrentPathKey = TlvDef.Typed<CurrentPathKeyTlv>(OnionPayloadTlvTypes.CurrentPathKey,
        baseTlv =>
        {
            if (baseTlv.Type != OnionPayloadTlvTypes.CurrentPathKey)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != CryptoConstants.CompactPubkeyLen
             || baseTlv.Value.Length != CryptoConstants.CompactPubkeyLen)
                throw new InvalidCastException("Invalid length");

            try
            {
                return new CurrentPathKeyTlv(new CompactPubKey(baseTlv.Value.ToArray()));
            }
            catch (ArgumentException e)
            {
                throw new InvalidCastException("Invalid path key", e);
            }
        },
        tlv =>
        {
            byte[] value = tlv.PathKey;

            return new BaseTlv(tlv.Type, value.ToArray());
        });

    public static readonly TlvDef<EncryptedRecipientDataTlv> EncryptedRecipientData = TlvDef.Typed<EncryptedRecipientDataTlv>(OnionPayloadTlvTypes.EncryptedRecipientData,
        baseTlv =>
        {
            if (baseTlv.Type != OnionPayloadTlvTypes.EncryptedRecipientData)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != (ulong)baseTlv.Value.Length)
                throw new InvalidCastException("Invalid length");

            return new EncryptedRecipientDataTlv(baseTlv.Value);
        },
        tlv => new BaseTlv(tlv.Type, tlv.EncryptedRecipientData.ToArray()));

    public static readonly TlvDef<OnionShortChannelIdTlv> OnionShortChannelId = TlvDef.Typed<OnionShortChannelIdTlv>(OnionPayloadTlvTypes.ShortChannelId,
        baseTlv =>
        {
            if (baseTlv.Type != OnionPayloadTlvTypes.ShortChannelId)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != ShortChannelId.Length || baseTlv.Value.Length != ShortChannelId.Length)
                throw new InvalidCastException("Invalid length");

            return new OnionShortChannelIdTlv(new ShortChannelId(baseTlv.Value.ToArray()));
        },
        tlv =>
        {
            byte[] value = tlv.ShortChannelId;

            return new BaseTlv(tlv.Type, value.ToArray());
        });

    public static readonly TlvDef<OutgoingCltvValueTlv> OutgoingCltvValue = TlvDef.Typed<OutgoingCltvValueTlv>(OnionPayloadTlvTypes.OutgoingCltvValue,
        baseTlv =>
        {
            if (baseTlv.Type != OnionPayloadTlvTypes.OutgoingCltvValue)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != (ulong)baseTlv.Value.Length
             || !TruncatedInt.TryDecodeTu32(baseTlv.Value, out var cltv))
                throw new InvalidCastException("Invalid length");

            return new OutgoingCltvValueTlv(cltv);
        },
        tlv => new BaseTlv(tlv.Type, TruncatedInt.EncodeTu32(tlv.OutgoingCltvValue)));

    public static readonly TlvDef<OutgoingNodeIdTlv> OutgoingNodeId = TlvDef.Typed<OutgoingNodeIdTlv>(OnionPayloadTlvTypes.OutgoingNodeId,
        baseTlv =>
        {
            if (baseTlv.Type != OnionPayloadTlvTypes.OutgoingNodeId)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != CryptoConstants.CompactPubkeyLen
             || baseTlv.Value.Length != CryptoConstants.CompactPubkeyLen)
                throw new InvalidCastException("Invalid length");

            try
            {
                return new OutgoingNodeIdTlv(new CompactPubKey(baseTlv.Value.ToArray()));
            }
            catch (ArgumentException e)
            {
                throw new InvalidCastException("Invalid outgoing node id", e);
            }
        },
        tlv =>
        {
            byte[] value = tlv.OutgoingNodeId;

            return new BaseTlv(tlv.Type, value.ToArray());
        });

    public static readonly TlvDef<PaymentDataTlv> PaymentData = TlvDef.Typed<PaymentDataTlv>(OnionPayloadTlvTypes.PaymentData,
        baseTlv =>
        {
            if (baseTlv.Type != OnionPayloadTlvTypes.PaymentData)
                throw new InvalidCastException("Invalid TLV type");

            var value = baseTlv.Value;
            if (baseTlv.Length != (ulong)value.Length || value.Length < CryptoConstants.SecretLen || value.Length > CryptoConstants.SecretLen + TruncatedInt.MaxTu64Length)
                throw new InvalidCastException("Invalid length");

            if (!TruncatedInt.TryDecodeTu64(value.AsSpan(CryptoConstants.SecretLen), out var totalMsat))
                throw new InvalidCastException("Invalid total_msat encoding");

            return new PaymentDataTlv(new Secret(value[..CryptoConstants.SecretLen]),
                                      LightningMoney.MilliSatoshis(totalMsat));
        },
        tlv =>
        {
            byte[] secret = tlv.PaymentSecret;
            var total = TruncatedInt.EncodeTu64(tlv.TotalMsat.MilliSatoshi);

            var value = new byte[CryptoConstants.SecretLen + total.Length];
            secret.AsSpan(0, CryptoConstants.SecretLen).CopyTo(value);
            total.CopyTo(value, CryptoConstants.SecretLen);

            return new BaseTlv(tlv.Type, value);
        });

    public static readonly TlvDef<PaymentMetadataTlv> PaymentMetadata = TlvDef.Typed<PaymentMetadataTlv>(OnionPayloadTlvTypes.PaymentMetadata,
        baseTlv =>
        {
            if (baseTlv.Type != OnionPayloadTlvTypes.PaymentMetadata)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != (ulong)baseTlv.Value.Length)
                throw new InvalidCastException("Invalid length");

            return new PaymentMetadataTlv(baseTlv.Value);
        },
        tlv => new BaseTlv(tlv.Type, tlv.PaymentMetadata.ToArray()));

    public static readonly TlvDef<RecipientBlindedPathsTlv> RecipientBlindedPaths = TlvDef.Typed<RecipientBlindedPathsTlv>(OnionPayloadTlvTypes.RecipientBlindedPaths,
        baseTlv =>
        {
            if (baseTlv.Type != OnionPayloadTlvTypes.RecipientBlindedPaths)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != (ulong)baseTlv.Value.Length)
                throw new InvalidCastException("Invalid length");

            if (!PaymentBlindedPathCodec.TryReadList(baseTlv.Value, out var paths, out var reason))
                throw new InvalidCastException($"Invalid recipient_blinded_paths: {reason}");

            return new RecipientBlindedPathsTlv(paths);
        },
        tlv => new BaseTlv(tlv.Type, PaymentBlindedPathCodec.EncodeList(tlv.Paths)));

    public static readonly TlvDef<RecipientFeaturesTlv> RecipientFeatures = TlvDef.Typed<RecipientFeaturesTlv>(OnionPayloadTlvTypes.RecipientFeatures,
        baseTlv =>
        {
            if (baseTlv.Type != OnionPayloadTlvTypes.RecipientFeatures)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != (ulong)baseTlv.Value.Length)
                throw new InvalidCastException("Invalid length");

            return new RecipientFeaturesTlv(baseTlv.Value);
        },
        tlv => new BaseTlv(tlv.Type, tlv.Features.ToArray()));

    public static readonly TlvDef<TotalAmountMsatTlv> TotalAmountMsat = TlvDef.Typed<TotalAmountMsatTlv>(OnionPayloadTlvTypes.TotalAmountMsat,
        baseTlv =>
        {
            if (baseTlv.Type != OnionPayloadTlvTypes.TotalAmountMsat)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != (ulong)baseTlv.Value.Length
             || !TruncatedInt.TryDecodeTu64(baseTlv.Value, out var amount))
                throw new InvalidCastException("Invalid length");

            return new TotalAmountMsatTlv(LightningMoney.MilliSatoshis(amount));
        },
        tlv => new BaseTlv(tlv.Type, TruncatedInt.EncodeTu64(tlv.TotalAmount.MilliSatoshi)));

    public static readonly TlvDef<TrampolineOnionPacketTlv> TrampolineOnionPacket = TlvDef.Typed<TrampolineOnionPacketTlv>(OnionPayloadTlvTypes.TrampolineOnionPacket,
        baseTlv =>
        {
            if (baseTlv.Type != OnionPayloadTlvTypes.TrampolineOnionPacket)
                throw new InvalidCastException("Invalid TLV type");

            if (baseTlv.Length != (ulong)baseTlv.Value.Length || baseTlv.Value.Length < TrampolineOnionPacketTlv.MinLength)
                throw new InvalidCastException("Invalid length");

            return new TrampolineOnionPacketTlv(baseTlv.Value);
        },
        tlv => new BaseTlv(tlv.Type, tlv.Packet.ToArray()));

    public static IReadOnlyList<TlvDef> All { get; } =
    [
        AmtToForward,
        CurrentPathKey,
        EncryptedRecipientData,
        OnionShortChannelId,
        OutgoingCltvValue,
        OutgoingNodeId,
        PaymentData,
        PaymentMetadata,
        RecipientBlindedPaths,
        RecipientFeatures,
        TotalAmountMsat,
        TrampolineOnionPacket,
    ];
}