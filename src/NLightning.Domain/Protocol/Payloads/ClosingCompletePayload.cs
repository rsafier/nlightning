namespace NLightning.Domain.Protocol.Payloads;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Money;

/// <summary>The payload of <c>closing_complete</c> (type 40).</summary>
public sealed class ClosingCompletePayload(ChannelId channelId, BitcoinScript closerScriptPubKey,
                                           BitcoinScript closeeScriptPubKey, LightningMoney feeSatoshis,
                                           uint lockTime)
    : SimpleClosingPayload(channelId, closerScriptPubKey, closeeScriptPubKey, feeSatoshis, lockTime);