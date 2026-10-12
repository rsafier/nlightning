namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// An accounting period and its close (<c>AccountingPeriods</c>, A3-T5, D-A13).
/// </summary>
/// <param name="PeriodId">The period's name: <c>YYYY-MM</c>, or a date range <c>YYYY-MM-DD..YYYY-MM-DD</c>.</param>
/// <param name="Start">The first instant of the period (UTC).</param>
/// <param name="End">The first instant after it (exclusive).</param>
/// <param name="State">Open or closed.</param>
/// <param name="ClosedAt">When it was closed.</param>
/// <param name="LastLedgerSeq">The feed's last ledger sequence the close covers.</param>
/// <param name="ChainHash">The feed's 32-byte chain hash at <paramref name="LastLedgerSeq"/>.</param>
/// <param name="Digest">The close digest (32-byte SHA-256, D-A13).</param>
/// <param name="Signature">The node key's signature of <paramref name="Digest"/>.</param>
/// <param name="Forced">Closed with <c>--force</c> although it held unvalued or unclassified rows.</param>
/// <param name="ClosingState">The financial book's state at the close (running balances and the like, as the
/// closing lane serializes it), so a rebuild starts from it (D-A8); null while open.</param>
public sealed record AccountingPeriod(
    string PeriodId,
    DateTimeOffset Start,
    DateTimeOffset End,
    AccountingPeriodState State,
    DateTimeOffset? ClosedAt,
    long LastLedgerSeq,
    byte[]? ChainHash,
    byte[]? Digest,
    byte[]? Signature,
    bool Forced,
    string? ClosingState);