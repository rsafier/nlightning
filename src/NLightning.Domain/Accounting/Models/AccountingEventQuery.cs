namespace NLightning.Domain.Accounting.Models;

using Channels.ValueObjects;
using Enums;

/// <summary>
/// A page of sealed accounting events, in ledger order.
/// </summary>
/// <param name="AfterLedgerSeq">Only events after this ledger sequence (0 = from the start).</param>
/// <param name="Take">At most this many events.</param>
/// <param name="Kinds">Only these kinds (null or empty = every kind).</param>
/// <param name="ChannelId">Only events of this channel.</param>
/// <param name="Since">Only events that happened at or after this time.</param>
/// <param name="Until">Only events that happened before this time.</param>
public sealed record AccountingEventQuery(
    long AfterLedgerSeq,
    int Take,
    IReadOnlyCollection<AccountingEventKind>? Kinds = null,
    ChannelId? ChannelId = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null);