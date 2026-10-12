using System.Buffers.Binary;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Crypto.Constants;
using NLightning.Domain.Protocol.Constants;
using NLightning.Domain.Protocol.Tlv;

namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// The wire definitions of BOLT 2 "Channel Splicing" and batching: <c>splice_init</c> (80), <c>splice_ack</c> (81),
/// <c>splice_locked</c> (77) and <c>start_batch</c> (127). The s64 funding contributions ride
/// <see cref="WireReader.S64"/>/<see cref="WireWriter.S64"/>; <c>splice_locked</c> carries only the taproot gossip
/// announcement nonces 0 and 2 (BOLTs PR #1059), any other even type fails it (BOLT 1).
/// </summary>
internal static class SpliceInitWire
{
    public static readonly MessageWire<SpliceInitMessage> Def = new(MessageTypes.SpliceInit, Encode, Decode,
        TlvDefs.RequireConfirmedInputs,
        TlvDefs.RequestFunding);

    private static void Encode(ref WireWriter writer, SpliceInitMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.S64(payload.FundingContributionSatoshis);
        writer.U32(payload.FundingFeeratePerKw);
        writer.U32(payload.Locktime);
        writer.CompactPubKey(payload.FundingPubKey);
    }

    private static WireConstruct<SpliceInitMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var fundingContributionSatoshis = reader.S64();
        var fundingFeeratePerKw = reader.U32();
        var locktime = reader.U32();
        var fundingPubKey = reader.CompactPubKey();

        return tlvs => new SpliceInitMessage(
            new SpliceInitPayload(channelId, fundingContributionSatoshis, fundingFeeratePerKw, locktime, fundingPubKey),
            tlvs.Get<RequireConfirmedInputsTlv>(TlvConstants.RequireConfirmedInputs),
            tlvs.Get<RequestFundingTlv>(TlvConstants.LiquidityAds));
    }
}

internal static class SpliceAckWire
{
    public static readonly MessageWire<SpliceAckMessage> Def = new(MessageTypes.SpliceAck, Encode, Decode,
        TlvDefs.RequireConfirmedInputs,
        TlvDefs.ProvideFunding);

    private static void Encode(ref WireWriter writer, SpliceAckMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.S64(payload.FundingContributionSatoshis);
        writer.CompactPubKey(payload.FundingPubKey);
    }

    private static WireConstruct<SpliceAckMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var fundingContributionSatoshis = reader.S64();
        var fundingPubKey = reader.CompactPubKey();

        return tlvs => new SpliceAckMessage(new SpliceAckPayload(channelId, fundingContributionSatoshis, fundingPubKey),
            tlvs.Get<RequireConfirmedInputsTlv>(TlvConstants.RequireConfirmedInputs),
            tlvs.Get<ProvideFundingTlv>(TlvConstants.LiquidityAds));
    }
}

internal static class SpliceLockedWire
{
    public static readonly MessageWire<SpliceLockedMessage> Def =
        new(MessageTypes.SpliceLocked, Encode, Decode,
            TlvDefs.AnnouncementNodeNonce,
            TlvDefs.AnnouncementBitcoinNonce);

    private static void Encode(ref WireWriter writer, SpliceLockedMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.Bytes(payload.SpliceTxId);
    }

    private static WireConstruct<SpliceLockedMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var spliceTxId = new TxId(reader.BytesArray(CryptoConstants.Sha256HashLen));

        return tlvs => new SpliceLockedMessage(new SpliceLockedPayload(channelId, spliceTxId),
            tlvs.Get<AnnouncementNodeNonceTlv>(TaprootTlvConstants.AnnouncementNodeNonce),
            tlvs.Get<AnnouncementBitcoinNonceTlv>(TaprootTlvConstants.AnnouncementBitcoinNonce));
    }
}

internal static class StartBatchWire
{
    public static readonly TlvDef<StartBatchMessageTypeTlv> StartBatchMessageType = TlvDef.Typed<StartBatchMessageTypeTlv>(TlvConstants.StartBatchMessageType,
        baseTlv =>
        {
            if (baseTlv.Type != TlvConstants.StartBatchMessageType)
            {
                throw new InvalidCastException("Invalid TLV type");
            }

            if (baseTlv.Length != StartBatchMessageTypeTlv.ValueLength || baseTlv.Value.Length != baseTlv.Length)
            {
                throw new InvalidCastException("Invalid length");
            }

            return new StartBatchMessageTypeTlv(BinaryPrimitives.ReadUInt16BigEndian(baseTlv.Value));
        },
        tlv => tlv);

    public static readonly MessageWire<StartBatchMessage> Def = new(MessageTypes.StartBatch, Encode, Decode,
        StartBatchWire.StartBatchMessageType);

    private static void Encode(ref WireWriter writer, StartBatchMessage message)
    {
        var payload = message.Payload;
        writer.ChannelId(payload.ChannelId);
        writer.U16(payload.BatchSize);
    }

    private static WireConstruct<StartBatchMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var batchSize = reader.U16();

        return tlvs => new StartBatchMessage(new StartBatchPayload(channelId, batchSize),
            tlvs.Get<StartBatchMessageTypeTlv>(TlvConstants.StartBatchMessageType));
    }
}