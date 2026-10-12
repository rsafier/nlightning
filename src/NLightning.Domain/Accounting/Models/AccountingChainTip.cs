namespace NLightning.Domain.Accounting.Models;

/// <summary>
/// The last sealed event of the feed: the sealer continues the sequence and the hash chain from it.
/// </summary>
/// <param name="LedgerSeq">Its ledger sequence (0 for an empty feed).</param>
/// <param name="Hash">Its chain hash (32 zero bytes for an empty feed).</param>
public sealed record AccountingChainTip(long LedgerSeq, byte[] Hash)
{
    public static AccountingChainTip Genesis { get; } = new(0, new byte[32]);
}