namespace NLightning.Domain.Accounting.Books;

using Channels.ValueObjects;
using Enums;

/// <summary>A page of book entries in ledger order.</summary>
public sealed record AccountingEntryQuery(
    long AfterLedgerSeq,
    int Take,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    IReadOnlyCollection<AccountingEventKind>? Kinds = null,
    ChannelId? ChannelId = null,
    AccountRole? Account = null);
