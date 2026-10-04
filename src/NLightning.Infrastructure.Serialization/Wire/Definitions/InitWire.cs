namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Node;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Node;

/// <summary>
/// The wire definition of <c>init</c> (16): the feature set serialized twice (global first), then the
/// <c>init_tlvs</c> extension (networks 1, remote_addr 3, liquidity-ads 1339). The advisory odd/even records whose
/// value may be malformed (NL-344, NL-850) decode leniently and surface as the message's undecodable raw values.
/// </summary>
internal static class InitWire
{
    private static readonly FeatureSetSerializer s_featureSetSerializer = new();

    public static readonly MessageWire<InitMessage> Def = new(MessageTypes.Init, Encode, Decode,
        TlvDef.Typed<NetworksTlv>(TlvConstants.Networks),
        TlvDef.Lenient<RemoteAddressTlv>(TlvConstants.RemoteAddress),
        TlvDef.Lenient<WillFundRatesTlv>(TlvConstants.LiquidityAds));

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
                                       tlvs.Get<NetworksTlv>(0), tlvs.Get<RemoteAddressTlv>(1),
                                       tlvs.Get<WillFundRatesTlv>(2))
        {
            UndecodableRemoteAddress = tlvs.RawValue(TlvConstants.RemoteAddress) is { } raw
                                       && tlvs.Get<RemoteAddressTlv>(1) is null ? raw : null,
            UndecodableWillFundRates = tlvs.RawValue(TlvConstants.LiquidityAds) is { } rates
                                       && tlvs.Get<WillFundRatesTlv>(2) is null ? rates : null
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