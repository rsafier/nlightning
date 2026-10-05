namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;

/// <summary>
/// The wire definition of BOLT 2 <c>channel_ready</c> (36): <c>channel_id</c> ‖ <c>second_per_commitment_point</c>,
/// with the <c>channel_ready_tlvs</c> (short_channel_id 1 and the simple taproot next_local_nonce 4).
/// </summary>
internal static class ChannelReadyWire
{
    public static readonly MessageWire<ChannelReadyMessage> Def = new(MessageTypes.ChannelReady, Encode, Decode,
        TlvDef.Typed<ShortChannelIdTlv>(TlvConstants.ShortChannelId),
        TlvDef.Typed<NextLocalNonceTlv>(TaprootTlvConstants.NextLocalNonce));

    private static void Encode(ref WireWriter writer, ChannelReadyMessage message)
    {
        writer.ChannelId(message.Payload.ChannelId);
        writer.CompactPubKey(message.Payload.SecondPerCommitmentPoint);
    }

    private static WireConstruct<ChannelReadyMessage> Decode(ref WireReader reader)
    {
        var channelId = reader.ChannelId();
        var secondPerCommitmentPoint = reader.CompactPubKey();

        return tlvs => new ChannelReadyMessage(
            new ChannelReadyPayload(channelId, secondPerCommitmentPoint),
            tlvs.Get<ShortChannelIdTlv>(TlvConstants.ShortChannelId),
            tlvs.Get<NextLocalNonceTlv>(TaprootTlvConstants.NextLocalNonce));
    }
}