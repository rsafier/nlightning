namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;

/// <summary>Serializes the payload of <c>closing_sig</c> (type 41).</summary>
public sealed class ClosingSigPayloadSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    : SimpleClosingPayloadSerializer<ClosingSigPayload>(valueObjectSerializerFactory)
{
    protected override ClosingSigPayload Create(ChannelId channelId, BitcoinScript closerScript,
                                                BitcoinScript closeeScript, LightningMoney feeSatoshis,
                                                uint lockTime) =>
        new(channelId, closerScript, closeeScript, feeSatoshis, lockTime);
}