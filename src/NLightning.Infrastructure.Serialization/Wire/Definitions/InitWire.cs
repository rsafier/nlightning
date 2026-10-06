using NLightning.Domain.Crypto.Constants;
using NLightning.Domain.Gossip.Addresses;
using NLightning.Domain.LiquidityAds;
using NLightning.Domain.Protocol.Constants;
using NLightning.Domain.Protocol.Tlv;
using NLightning.Domain.Protocol.ValueObjects;

namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Node;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Node;

/// <summary>
/// The wire definition of <c>init</c> (16): the feature set serialized twice (global first), then the
/// <c>init_tlvs</c> extension (networks 1, remote_addr 3, liquidity-ads 1339). The advisory odd/even records whose
/// value may be malformed (NL-344, NL-850) decode leniently and surface as the message's undecodable raw values.
/// </summary>
internal static class InitWire
{
    public static readonly TlvDef<NetworksTlv> Networks = TlvDef.Typed<NetworksTlv>(TlvConstants.Networks,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.Networks)
            {
                throw new InvalidCastException("Invalid TLV type");
            }

            if (baseTlv.Length % CryptoConstants.Sha256HashLen != 0)
            {
                throw new InvalidCastException("Invalid length");
            }

            var chainHashes = new List<ChainHash>();
            // split the Value into 32 bytes chunks and add it to the list
            for (var i = 0; i < baseTlv.Length; i += CryptoConstants.Sha256HashLen)
            {
                chainHashes.Add(baseTlv.Value[i..(i + CryptoConstants.Sha256HashLen)]);
            }

            return new NetworksTlv(chainHashes);
        },
        tlv => tlv);

    public static readonly TlvDef<RemoteAddressTlv> RemoteAddress = TlvDef.Typed<RemoteAddressTlv>(TlvConstants.RemoteAddress,
        baseTlv =>
        {
            ArgumentNullException.ThrowIfNull(baseTlv);
            if (baseTlv.Type != TlvConstants.RemoteAddress)
                throw new InvalidCastException("Invalid TLV type");

            try
            {
                return new RemoteAddressTlv(AddressDescriptorCodec.DecodeSingle(baseTlv.Value));
            }
            catch (FormatException e)
            {
                throw new InvalidCastException($"Invalid remote_addr: {e.Message}", e);
            }
        },
        tlv =>
        {
            ArgumentNullException.ThrowIfNull(tlv);
            return new BaseTlv(tlv.Type, AddressDescriptorCodec.Encode(tlv.Descriptor));
        });

    public static readonly TlvDef<WillFundRatesTlv> WillFundRates = TlvDef.Typed<WillFundRatesTlv>(TlvConstants.LiquidityAds,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.LiquidityAds)
                throw new InvalidCastException("Invalid TLV type");

            if (!LiquidityAdsCodec.TryDecodeWillFundRates(baseTlv.Value, out var rates) || rates is null)
                throw new InvalidCastException("Invalid option_will_fund value");

            return new WillFundRatesTlv(rates);
        },
        tlv => new BaseTlv(tlv.Type, tlv.Value));

    private static readonly FeatureSetSerializer s_featureSetSerializer = new();

    public static readonly MessageWire<InitMessage> Def = new(MessageTypes.Init, Encode, Decode,
        InitWire.Networks,
        InitWire.RemoteAddress.AsLenient(),
        InitWire.WillFundRates.AsLenient());

    private static void Encode(ref WireWriter writer, InitMessage message)
    {
        var featureSet = message.Payload.FeatureSet;
        writer.Bytes(FeatureBytes(featureSet, asGlobal: true));
        writer.Bytes(FeatureBytes(featureSet, asGlobal: false));
    }

    private static WireConstruct<InitMessage> Decode(ref WireReader reader)
    {
        var globalFeatures = ReadFeatures(ref reader);
        var features = ReadFeatures(ref reader);

        return tlvs => new InitMessage(new InitPayload(FeatureSet.Combine(globalFeatures, features)),
                                       tlvs.Get<NetworksTlv>(TlvConstants.Networks),
                                       tlvs.Get<RemoteAddressTlv>(TlvConstants.RemoteAddress),
                                       tlvs.Get<WillFundRatesTlv>(TlvConstants.LiquidityAds))
        {
            UndecodableRemoteAddress = tlvs.RawValue(TlvConstants.RemoteAddress) is { } raw
                                       && tlvs.Get<RemoteAddressTlv>(TlvConstants.RemoteAddress) is null ? raw : null,
            UndecodableWillFundRates = tlvs.RawValue(TlvConstants.LiquidityAds) is { } rates
                                       && tlvs.Get<WillFundRatesTlv>(TlvConstants.LiquidityAds) is null ? rates : null
        };
    }

    private static byte[] FeatureBytes(FeatureSet featureSet, bool asGlobal)
    {
        using var stream = new MemoryStream();
        s_featureSetSerializer.SerializeAsync(featureSet, stream, asGlobal).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private static FeatureSet ReadFeatures(ref WireReader reader)
    {
        var length = reader.U16();
        var bytes = reader.BytesArray(length);
        using var stream = new MemoryStream();
        stream.Write([(byte)(length >> 8), (byte)length]);
        stream.Write(bytes);
        stream.Position = 0;
        return s_featureSetSerializer.DeserializeAsync(stream).GetAwaiter().GetResult();
    }
}