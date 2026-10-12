namespace NLightning.Domain.Client.Responses;

using Bitcoin.ValueObjects;
using Channels.Splicing.Enums;
using Channels.ValueObjects;
using Money;

/// <summary>
/// One funding of a channel as listed by <c>ListChannels</c> (splicing plan §3.10 <c>fundings[]</c>, SP2-0; filled by
/// lane SP2-D): the current funding, the pending splices and, while their short channel id still resolves, the replaced
/// ones.
/// </summary>
public sealed class ChannelFundingInfoClientResponse
{
    public required TxId FundingTxId { get; init; }
    public ushort OutputIndex { get; init; }
    public required LightningMoney Capacity { get; init; }
    public ChannelFundingStatus Status { get; init; }
    public ChannelFundingKind Kind { get; init; }

    /// <summary>Confirmations at our chain tip, or null while unconfirmed or unknown.</summary>
    public uint? Depth { get; init; }

    /// <summary>The funding's short channel id, once confirmed.</summary>
    public ShortChannelId? ShortChannelId { get; init; }

    public bool SpliceLockedSent { get; init; }
    public bool SpliceLockedReceived { get; init; }
}