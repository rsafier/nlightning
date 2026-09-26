namespace NLightning.Domain.Protocol.Payloads;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Money;

/// <summary>The payload of <c>closing_sig</c> (type 41): the fields of the <c>closing_complete</c> it answers.</summary>
public sealed class ClosingSigPayload(ChannelId channelId, BitcoinScript closerScriptPubKey,
                                      BitcoinScript closeeScriptPubKey, LightningMoney feeSatoshis, uint lockTime)
    : SimpleClosingPayload(channelId, closerScriptPubKey, closeeScriptPubKey, feeSatoshis, lockTime);