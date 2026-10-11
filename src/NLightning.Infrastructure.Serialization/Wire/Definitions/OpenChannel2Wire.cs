namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// The wire definitions of BOLT 2 <c>open_channel2</c> (64) and <c>accept_channel2</c> (65), with their
/// <c>opening_tlvs</c>/<c>accept_tlvs</c> (upfront_shutdown_script 0, channel_type 1, require_confirmed_inputs 2 and
/// the liquidity-ads 1339 request/provide funding records).
/// </summary>
internal static class OpenChannel2Wire
{
    public static readonly MessageWire<OpenChannel2Message> Def = new(MessageTypes.OpenChannel2, Encode, Decode,
        TlvDefs.UpfrontShutdownScript,
        TlvDefs.ChannelType,
        TlvDefs.RequireConfirmedInputs,
        TlvDefs.RequestFunding);

    private static void Encode(ref WireWriter writer, OpenChannel2Message message)
    {
        var payload = message.Payload;
        writer.Bytes(payload.ChainHash);
        writer.ChannelId(payload.ChannelId);
        writer.U32(payload.FundingFeeRatePerKw);
        writer.U32(payload.CommitmentFeeRatePerKw);
        writer.U64((ulong)payload.FundingAmount.Satoshi);
        writer.U64((ulong)payload.DustLimitAmount.Satoshi);
        writer.U64(payload.MaxHtlcValueInFlightAmount.MilliSatoshi);
        writer.U64(payload.HtlcMinimumAmount.MilliSatoshi);
        writer.U16(payload.ToSelfDelay);
        writer.U16(payload.MaxAcceptedHtlcs);
        writer.U32(payload.Locktime);
        writer.CompactPubKey(payload.FundingPubKey);
        writer.CompactPubKey(payload.RevocationBasepoint);
        writer.CompactPubKey(payload.PaymentBasepoint);
        writer.CompactPubKey(payload.DelayedPaymentBasepoint);
        writer.CompactPubKey(payload.HtlcBasepoint);
        writer.CompactPubKey(payload.FirstPerCommitmentPoint);
        writer.CompactPubKey(payload.SecondPerCommitmentPoint);
        writer.U8(payload.ChannelFlags);
    }

    private static WireConstruct<OpenChannel2Message> Decode(ref WireReader reader)
    {
        var chainHash = new ChainHash(reader.BytesArray(CryptoConstants.Sha256HashLen));
        var channelId = reader.ChannelId();
        var fundingFeeRatePerKw = reader.U32();
        var commitmentFeeRatePerKw = reader.U32();
        var fundingAmount = LightningMoney.Satoshis(reader.U64());
        var dustLimitAmount = LightningMoney.Satoshis(reader.U64());
        var maxHtlcValueInFlight = LightningMoney.MilliSatoshis(reader.U64());
        var htlcMinimumAmount = LightningMoney.MilliSatoshis(reader.U64());
        var toSelfDelay = reader.U16();
        var maxAcceptedHtlcs = reader.U16();
        var locktime = reader.U32();
        var fundingPubKey = reader.CompactPubKey();
        var revocationBasepoint = reader.CompactPubKey();
        var paymentBasepoint = reader.CompactPubKey();
        var delayedPaymentBasepoint = reader.CompactPubKey();
        var htlcBasepoint = reader.CompactPubKey();
        var firstPerCommitmentPoint = reader.CompactPubKey();
        var secondPerCommitmentPoint = reader.CompactPubKey();
        var channelFlags = new ChannelFlags(reader.U8());

        return tlvs => new OpenChannel2Message(
            new OpenChannel2Payload(chainHash, channelFlags, commitmentFeeRatePerKw, delayedPaymentBasepoint,
                                    dustLimitAmount, firstPerCommitmentPoint, fundingAmount, fundingFeeRatePerKw,
                                    fundingPubKey, htlcBasepoint, htlcMinimumAmount, locktime, maxAcceptedHtlcs,
                                    maxHtlcValueInFlight, paymentBasepoint, revocationBasepoint,
                                    secondPerCommitmentPoint, toSelfDelay, channelId),
            tlvs.Get<UpfrontShutdownScriptTlv>(TlvConstants.UpfrontShutdownScript),
            tlvs.Get<ChannelTypeTlv>(TlvConstants.ChannelType),
            tlvs.Get<RequireConfirmedInputsTlv>(TlvConstants.RequireConfirmedInputs),
            tlvs.Get<RequestFundingTlv>(TlvConstants.LiquidityAds));
    }
}

internal static class AcceptChannel2Wire
{
    public static readonly MessageWire<AcceptChannel2Message> Def = new(MessageTypes.AcceptChannel2, Encode, Decode,
        TlvDefs.UpfrontShutdownScript,
        TlvDefs.ChannelType,
        TlvDefs.RequireConfirmedInputs,
        TlvDefs.ProvideFunding);

    private static void Encode(ref WireWriter writer, AcceptChannel2Message message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.U64((ulong)payload.FundingAmount.Satoshi);
        writer.U64((ulong)payload.DustLimitAmount.Satoshi);
        writer.U64(payload.MaxHtlcValueInFlightAmount.MilliSatoshi);
        writer.U64(payload.HtlcMinimumAmount.MilliSatoshi);
        writer.U32(payload.MinimumDepth);
        writer.U16(payload.ToSelfDelay);
        writer.U16(payload.MaxAcceptedHtlcs);
        writer.CompactPubKey(payload.FundingCompactPubKey);
        writer.CompactPubKey(payload.RevocationCompactBasepoint);
        writer.CompactPubKey(payload.PaymentCompactBasepoint);
        writer.CompactPubKey(payload.DelayedPaymentCompactBasepoint);
        writer.CompactPubKey(payload.HtlcCompactBasepoint);
        writer.CompactPubKey(payload.FirstPerCommitmentCompactPoint);
        writer.CompactPubKey(payload.SecondPerCommitmentCompactPoint);
    }

    private static WireConstruct<AcceptChannel2Message> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var fundingAmount = LightningMoney.Satoshis(reader.U64());
        var dustLimitAmount = LightningMoney.Satoshis(reader.U64());
        var maxHtlcValueInFlight = LightningMoney.MilliSatoshis(reader.U64());
        var htlcMinimumAmount = LightningMoney.MilliSatoshis(reader.U64());
        var minimumDepth = reader.U32();
        var toSelfDelay = reader.U16();
        var maxAcceptedHtlcs = reader.U16();
        var fundingPubKey = reader.CompactPubKey();
        var revocationBasepoint = reader.CompactPubKey();
        var paymentBasepoint = reader.CompactPubKey();
        var delayedPaymentBasepoint = reader.CompactPubKey();
        var htlcBasepoint = reader.CompactPubKey();
        var firstPerCommitmentPoint = reader.CompactPubKey();
        var secondPerCommitmentPoint = reader.CompactPubKey();

        return tlvs => new AcceptChannel2Message(
            new AcceptChannel2Payload(delayedPaymentBasepoint, dustLimitAmount, firstPerCommitmentPoint,
                                      fundingAmount, fundingPubKey, htlcBasepoint, htlcMinimumAmount,
                                      maxAcceptedHtlcs, maxHtlcValueInFlight, minimumDepth, paymentBasepoint,
                                      revocationBasepoint, channelId, toSelfDelay, secondPerCommitmentPoint),
            tlvs.Get<UpfrontShutdownScriptTlv>(TlvConstants.UpfrontShutdownScript),
            tlvs.Get<ChannelTypeTlv>(TlvConstants.ChannelType),
            tlvs.Get<RequireConfirmedInputsTlv>(TlvConstants.RequireConfirmedInputs),
            tlvs.Get<ProvideFundingTlv>(TlvConstants.LiquidityAds));
    }
}