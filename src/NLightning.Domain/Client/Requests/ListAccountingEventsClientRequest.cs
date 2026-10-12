namespace NLightning.Domain.Client.Requests;

using Accounting.Enums;
using Channels.ValueObjects;

/// <summary>
/// Lists sealed accounting events in ledger order after a cursor (<c>ClientCommand.ListAccountingEvents</c>,
/// NL-602); every filter is optional.
/// </summary>
public sealed class ListAccountingEventsClientRequest
{
    /// <summary>Only events after this ledger sequence (0 = from the start): the previous page's
    /// <c>NextAfter</c>.</summary>
    public long AfterLedgerSeq { get; init; }

    /// <summary>The most events to return.</summary>
    public int Take { get; init; } = 100;

    /// <summary>Only these kinds (null or empty = every kind).</summary>
    public IReadOnlyCollection<AccountingEventKind>? Kinds { get; init; }

    /// <summary>Only events of this channel, or null for all.</summary>
    public ChannelId? ChannelId { get; init; }

    /// <summary>The short channel id the channel filter came as (resolved through the loaded channels).</summary>
    public ShortChannelId? ChannelScid { get; init; }

    /// <summary>Only events that happened at or after this time.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only events that happened before this time.</summary>
    public DateTimeOffset? Until { get; init; }
}