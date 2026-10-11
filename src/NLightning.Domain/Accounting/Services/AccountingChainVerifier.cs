namespace NLightning.Domain.Accounting.Services;

using Interfaces;
using Models;

/// <summary>
/// Walks the feed's hash chain from the first sealed event (plan §6.2 "Audit", §10: in pages, on demand, never at
/// startup) and reports the first event whose stored hash does not match its recomputed one.
/// </summary>
/// <remarks>
/// Each event's hash is recomputed over the previous <b>stored</b> hash, so a row edited after sealing breaks at that
/// row; a removed row shows as a gap in the ledger sequence, and a row whose hash was rewritten to match its edit breaks
/// at the next row (its stored hash no longer chains to the rewritten one).
/// </remarks>
public static class AccountingChainVerifier
{
    /// <summary>The default page size.</summary>
    public const int DefaultPageSize = 1_000;

    public static async Task<AccountingChainVerification> VerifyAsync(IAccountingEventDbRepository repository,
                                                                       int pageSize = DefaultPageSize,
                                                                       CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var previousHash = AccountingChainTip.Genesis.Hash;
        var expectedSeq = 1L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await repository.GetSealedRangeAsync(expectedSeq, pageSize, cancellationToken);
            foreach (var accountingEvent in page)
            {
                var ledgerSeq = accountingEvent.LedgerSeq ?? 0;
                if (ledgerSeq != expectedSeq)
                    return Break(expectedSeq, previousHash,
                                 ledgerSeq > expectedSeq
                                     ? $"Ledger sequence {expectedSeq} is missing (the next sealed event is {ledgerSeq})."
                                     : $"Ledger sequence {ledgerSeq} appears twice.");

                if (accountingEvent.Hash is not { Length: AccountingEventHasher.HashLength } storedHash)
                    return Break(expectedSeq, previousHash, "The event has no chain hash.");

                var hash = AccountingEventHasher.ComputeHash(previousHash, ledgerSeq, accountingEvent);
                if (!hash.AsSpan().SequenceEqual(storedHash))
                    return Break(expectedSeq, previousHash,
                                 $"The stored hash does not match the event (key {accountingEvent.EventKey}): the row "
                               + "or the one before it was changed after sealing.");

                previousHash = storedHash;
                expectedSeq++;
            }

            if (page.Count < pageSize)
                return new AccountingChainVerification(expectedSeq - 1, expectedSeq - 1, previousHash);
        }
    }

    private static AccountingChainVerification Break(long ledgerSeq, byte[] previousHash, string reason) =>
        new(ledgerSeq - 1, ledgerSeq - 1, previousHash, ledgerSeq, reason);
}