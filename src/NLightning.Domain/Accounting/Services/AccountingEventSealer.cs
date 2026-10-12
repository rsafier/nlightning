namespace NLightning.Domain.Accounting.Services;

using Models;

/// <summary>
/// Decides the seals of a batch of unsealed accounting events (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §3,
/// principle 3).
/// </summary>
/// <remarks>
/// <para>Rows are sealed in the order they are passed (storage id order, which is the order the rows became visible:
/// a row whose save committed late is simply sealed in a later batch, after the rows sealed before it). Each row that
/// is not a duplicate gets the next dense ledger sequence and the chain hash over the previous one, so a reader that
/// follows the ledger sequence never skips a row that committed late.</para>
/// <para>A row whose key was already sealed, or that repeats a key earlier in the batch, is a duplicate: no ledger
/// sequence, no hash, never read by the books. Duplicates are not rejected at write time on purpose: an accounting row
/// must never fail the core save it rides in.</para>
/// </remarks>
public static class AccountingEventSealer
{
    /// <param name="tip">The last sealed event (<see cref="AccountingChainTip.Genesis"/> for an empty feed).</param>
    /// <param name="batch">The unsealed rows, in storage id order.</param>
    /// <param name="sealedKeys">The keys among <paramref name="batch"/> that are already sealed.</param>
    /// <param name="newTip">The tip after the batch.</param>
    public static IReadOnlyList<AccountingSeal> Seal(AccountingChainTip tip, IReadOnlyList<AccountingEventModel> batch,
                                                     IReadOnlySet<string> sealedKeys, out AccountingChainTip newTip)
    {
        ArgumentNullException.ThrowIfNull(tip);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(sealedKeys);

        var seals = new List<AccountingSeal>(batch.Count);
        var seenKeys = new HashSet<string>(sealedKeys, StringComparer.Ordinal);
        var ledgerSeq = tip.LedgerSeq;
        var previousHash = tip.Hash;

        foreach (var accountingEvent in batch)
        {
            if (accountingEvent.IsSealed)
                throw new ArgumentException($"Event {accountingEvent.Id} is already sealed", nameof(batch));

            if (!seenKeys.Add(accountingEvent.EventKey))
            {
                seals.Add(new AccountingSeal(accountingEvent.Id, null, null));
                continue;
            }

            ledgerSeq++;
            var hash = AccountingEventHasher.ComputeHash(previousHash, ledgerSeq, accountingEvent);
            seals.Add(new AccountingSeal(accountingEvent.Id, ledgerSeq, hash));
            previousHash = hash;
        }

        newTip = new AccountingChainTip(ledgerSeq, previousHash);
        return seals;
    }
}