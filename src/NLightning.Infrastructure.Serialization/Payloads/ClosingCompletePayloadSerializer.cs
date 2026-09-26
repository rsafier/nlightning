namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;

/// <summary>Serializes the payload of <c>closing_complete</c> (type 40).</summary>
public sealed class ClosingCompletePayloadSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    : SimpleClosingPayloadSerializer<ClosingCompletePayload>(valueObjectSerializerFactory)
{
    protected override ClosingCompletePayload Create(ChannelId channelId, BitcoinScript closerScript,
                                                     BitcoinScript closeeScript, LightningMoney feeSatoshis,
                                                     uint lockTime) =>
        new(channelId, closerScript, closeeScript, feeSatoshis, lockTime);
}