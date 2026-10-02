namespace NLightning.Domain.Accounting.Books.Reports;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;

/// <summary>
/// The fees we paid in [<see cref="Since"/>, <see cref="Until"/>) (plan §6.1): every fee expense account, plus the
/// sweep fee bumps that confirmed, which are informational (the resolution's fee already holds the whole fee).
/// </summary>
/// <param name="Since">The start (inclusive), or null for the start of the books.</param>
/// <param name="Until">The end (exclusive), or null for now.</param>
/// <param name="ProjectedLedgerSeq">The books' cursor.</param>
/// <param name="Accounts">Every fee expense account (<see cref="AccountingAccountCategories.FeeAccounts"/>, zero
/// included), debit-positive.</param>
/// <param name="SweepFeeBumps">The confirmed sweep replacements of the period, in ledger order.</param>
public sealed record AccountingFeesReport(
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    long ProjectedLedgerSeq,
    IReadOnlyList<AccountingAccountLine> Accounts,
    IReadOnlyList<AccountingSweepFeeBump> SweepFeeBumps)
{
    public long TotalMsat => Accounts.Sum(a => a.AmountMsat);

    /// <summary>The sum of <see cref="SweepFeeBumps"/> (already in the sweep fee account).</summary>
    public long SweepFeeBumpTotalMsat => SweepFeeBumps.Sum(b => b.FeeMsat);
}

/// <summary>A confirmed sweep replacement (a <c>SweepFeeBump</c> event, or its reversal with a negative fee).</summary>
public sealed record AccountingSweepFeeBump(
    long LedgerSeq,
    DateTimeOffset OccurredAt,
    TxId? TxId,
    ChannelId? ChannelId,
    long FeeMsat,
    string? Purpose);