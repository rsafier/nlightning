namespace NLightning.Domain.Accounting.Books;

/// <summary>One bucket of a reconcile: what the books hold against what the node holds.</summary>
public sealed record AccountingReconcileLine(AccountRole Account, long BooksMsat, long NodeMsat, string? Note = null)
{
    public long DriftMsat => BooksMsat - NodeMsat;
}

/// <summary>The books against a live snapshot (plan §5, §6.1 "Reconcile").</summary>
public sealed record AccountingReconcileResult(
    DateTimeOffset TakenAt,
    uint BlockHeight,
    long LedgerSeq,
    IReadOnlyList<AccountingReconcileLine> Lines)
{
    public bool IsClean => Lines.All(l => l.DriftMsat == 0);
}