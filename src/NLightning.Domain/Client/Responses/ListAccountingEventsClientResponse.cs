namespace NLightning.Domain.Client.Responses;

using Accounting.Models;

/// <summary>
/// The answer to <c>listaccountingevents</c> (<c>ClientCommand.ListAccountingEvents</c>, NL-602): a page of sealed
/// events in ledger order and the cursor of the next page.
/// </summary>
/// <param name="Events">The page, in ledger order.</param>
/// <param name="NextAfter">The cursor for the next page: the last event's ledger sequence, or the request's cursor
/// when the page is empty.</param>
/// <param name="HasMore">Whether the page is full (more events may follow the cursor).</param>
/// <param name="ChainTipLedgerSeq">The last sealed ledger sequence of the whole feed.</param>
public sealed record ListAccountingEventsClientResponse(
    IReadOnlyList<AccountingEventModel> Events,
    long NextAfter,
    bool HasMore,
    long ChainTipLedgerSeq);