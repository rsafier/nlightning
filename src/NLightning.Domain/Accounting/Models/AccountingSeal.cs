namespace NLightning.Domain.Accounting.Models;

/// <summary>
/// What the sealer decided for one unsealed row: a ledger sequence and chain hash, or a duplicate mark.
/// </summary>
/// <param name="Id">The row's storage id.</param>
/// <param name="LedgerSeq">The dense ledger sequence, or null for a duplicate.</param>
/// <param name="Hash">The chain hash, or null for a duplicate.</param>
public sealed record AccountingSeal(long Id, long? LedgerSeq, byte[]? Hash)
{
    public bool IsDuplicate => LedgerSeq is null;
}