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
/// The wire definitions of BOLT 2 <c>open_channel</c> (32) and <c>accept_channel</c> (33), with their
/// <c>open_channel_tlvs</c>/<c>accept_channel_tlvs</c> (upfront_shutdown_script 0, channel_type 1 and the simple
/// taproot next_local_nonce 4).
/// </summary>
internal static class OpenChannelWire
{
    public static readonly MessageWire<OpenChannel1Message> Def = new(MessageTypes.OpenChannel, Encode, Decode,
        TlvDef.Typed<UpfrontShutdownScriptTlv>(TlvConstants.UpfrontShutdownScript),
        TlvDef.Typed<ChannelTypeTlv>(TlvConstants.ChannelType),
        TlvDef.Typed<NextLocalNonceTlv>(TaprootTlvConstants.NextLocalNonce));

    private static void Encode(ref WireWriter writer, OpenChannel1Message message)
    {
        var payload = message.Payload;
        writer.Bytes(payload.ChainHash);
        writer.ChannelId(payload.ChannelId);
        writer.U64((ulong)payload.FundingAmount.Satoshi);
        writer.U64(payload.PushAmount.MilliSatoshi);
        writer.U64((ulong)payload.DustLimitAmount.Satoshi);
        writer.U64(payload.MaxHtlcValueInFlight.MilliSatoshi);
        writer.U64((ulong)payload.ChannelReserveAmount.Satoshi);
        writer.U64(payload.HtlcMinimumAmount.MilliSatoshi);
        writer.U32((uint)payload.FeeRatePerKw.Satoshi);
        writer.U16(payload.ToSelfDelay);
        writer.U16(payload.MaxAcceptedHtlcs);
        writer.CompactPubKey(payload.FundingPubKey);
        writer.CompactPubKey(payload.RevocationBasepoint);
        writer.CompactPubKey(payload.PaymentBasepoint);
        writer.CompactPubKey(payload.DelayedPaymentBasepoint);
        writer.CompactPubKey(payload.HtlcBasepoint);
        writer.CompactPubKey(payload.FirstPerCommitmentPoint);
        writer.U8(payload.ChannelFlags);
    }

    private static WireConstruct<OpenChannel1Message> Decode(ref WireReader reader)
    {
        var chainHash = new ChainHash(reader.BytesArray(CryptoConstants.Sha256HashLen));
        var channelId = reader.ChannelId();
        var fundingAmount = LightningMoney.Satoshis(reader.U64());
        var pushAmount = LightningMoney.MilliSatoshis(reader.U64());
        var dustLimitAmount = LightningMoney.Satoshis(reader.U64());
        var maxHtlcValueInFlight = LightningMoney.MilliSatoshis(reader.U64());
        var channelReserveAmount = LightningMoney.Satoshis(reader.U64());
        var htlcMinimumAmount = LightningMoney.MilliSatoshis(reader.U64());
        var feeRatePerKw = LightningMoney.Satoshis(reader.U32());
        var toSelfDelay = reader.U16();
        var maxAcceptedHtlcs = reader.U16();
        var fundingPubKey = reader.CompactPubKey();
        var revocationBasepoint = reader.CompactPubKey();
        var paymentBasepoint = reader.CompactPubKey();
        var delayedPaymentBasepoint = reader.CompactPubKey();
        var htlcBasepoint = reader.CompactPubKey();
        var firstPerCommitmentPoint = reader.CompactPubKey();
        var channelFlags = new ChannelFlags(reader.U8());

        return tlvs => new OpenChannel1Message(
            new OpenChannel1Payload(chainHash, channelFlags, channelId, channelReserveAmount, delayedPaymentBasepoint,
                                    dustLimitAmount, feeRatePerKw, firstPerCommitmentPoint, fundingAmount,
                                    fundingPubKey, htlcBasepoint, htlcMinimumAmount, maxAcceptedHtlcs,
                                    maxHtlcValueInFlight, paymentBasepoint, pushAmount, revocationBasepoint,
                                    toSelfDelay),
            tlvs.Get<ChannelTypeTlv>(TlvConstants.ChannelType),
            tlvs.Get<UpfrontShutdownScriptTlv>(TlvConstants.UpfrontShutdownScript),
            tlvs.Get<NextLocalNonceTlv>(TaprootTlvConstants.NextLocalNonce));
    }
}

internal static class AcceptChannelWire
{
    public static readonly MessageWire<AcceptChannel1Message> Def = new(MessageTypes.AcceptChannel, Encode, Decode,
        TlvDef.Typed<UpfrontShutdownScriptTlv>(TlvConstants.UpfrontShutdownScript),
        TlvDef.Typed<ChannelTypeTlv>(TlvConstants.ChannelType),
        TlvDef.Typed<NextLocalNonceTlv>(TaprootTlvConstants.NextLocalNonce));

    private static void Encode(ref WireWriter writer, AcceptChannel1Message message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.U64((ulong)payload.DustLimitAmount.Satoshi);
        writer.U64(payload.MaxHtlcValueInFlightAmount.MilliSatoshi);
        writer.U64((ulong)payload.ChannelReserveAmount.Satoshi);
        writer.U64(payload.HtlcMinimumAmount.MilliSatoshi);
        writer.U32(payload.MinimumDepth);
        writer.U16(payload.ToSelfDelay);
        writer.U16(payload.MaxAcceptedHtlcs);
        writer.CompactPubKey(payload.FundingPubKey);
        writer.CompactPubKey(payload.RevocationBasepoint);
        writer.CompactPubKey(payload.PaymentBasepoint);
        writer.CompactPubKey(payload.DelayedPaymentBasepoint);
        writer.CompactPubKey(payload.HtlcBasepoint);
        writer.CompactPubKey(payload.FirstPerCommitmentPoint);
    }

    private static WireConstruct<AcceptChannel1Message> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var dustLimitAmount = LightningMoney.Satoshis(reader.U64());
        var maxHtlcValueInFlight = LightningMoney.MilliSatoshis(reader.U64());
        var channelReserveAmount = LightningMoney.Satoshis(reader.U64());
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

        return tlvs => new AcceptChannel1Message(
            new AcceptChannel1Payload(channelId, channelReserveAmount, delayedPaymentBasepoint, dustLimitAmount,
                                      firstPerCommitmentPoint, fundingPubKey, htlcBasepoint, htlcMinimumAmount,
                                      maxAcceptedHtlcs, maxHtlcValueInFlight, minimumDepth, paymentBasepoint,
                                      revocationBasepoint, toSelfDelay),
            tlvs.Get<ChannelTypeTlv>(TlvConstants.ChannelType),
            tlvs.Get<UpfrontShutdownScriptTlv>(TlvConstants.UpfrontShutdownScript),
            tlvs.Get<NextLocalNonceTlv>(TaprootTlvConstants.NextLocalNonce));
    }
}