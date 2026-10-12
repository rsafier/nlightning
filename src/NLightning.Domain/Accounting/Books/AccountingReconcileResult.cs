namespace NLightning.Domain.Accounting.Books;

/// <summary>One bucket of a reconcile: what the books hold against what the node holds.</summary>
/// <param name="Account">The bucket's account.</param>
/// <param name="BooksMsat">The books' running balance.</param>
/// <param name="NodeMsat">The node's live balance.</param>
/// <param name="Note">What the node side counts and what may legitimately differ.</param>
/// <param name="OutstandingMsat">The part of the difference that transactions the node knows to be in flight explain
/// (unconfirmed, or below their depth: the clearing account between a funding's wallet side and its lock, NL-621):
/// expected, reported apart, never a drift.</param>
public sealed record AccountingReconcileLine(AccountRole Account, long BooksMsat, long NodeMsat, string? Note = null,
                                             long OutstandingMsat = 0)
{
    /// <summary>The unexplained remainder: books less node less <see cref="OutstandingMsat"/>.</summary>
    public long DriftMsat => BooksMsat - NodeMsat - OutstandingMsat;
}

/// <summary>The books against a live snapshot (plan §5, §6.1 "Reconcile").</summary>
public sealed record AccountingReconcileResult(
    DateTimeOffset TakenAt,
    uint BlockHeight,
    long LedgerSeq,
    IReadOnlyList<AccountingReconcileLine> Lines)
{
    /// <summary>No line drifts (outstanding amounts are expected and do not count).</summary>
    public bool IsClean => Lines.All(l => l.DriftMsat == 0);

    /// <summary>The outstanding amounts of every line (NL-621).</summary>
    public long OutstandingMsat => Lines.Sum(l => l.OutstandingMsat);
}