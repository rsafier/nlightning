namespace NLightning.Domain.Accounting.Financial;
/// <summary>
/// The cost-basis lots and their reliefs (<c>AccountingLots</c>, <c>AccountingLotReliefs</c>, A3-T4). Writes are staged
/// and committed by the unit of work's save, so a projector saves its lots and reliefs with its entries and cursor.
/// </summary>
/// <remarks>
/// Lot ids are assigned here, not by the database (<see cref="AddLotAsync"/>), so a relief can name a lot opened in the
/// same save; the financial projector is the only writer of lots.
/// </remarks>
public interface IAccountingLotDbRepository
{
    /// <summary>Stages a lot and returns its id: the lot's own when not 0, else one past the highest saved or staged.</summary>
    Task<long> AddLotAsync(AccountingLot lot, CancellationToken cancellationToken = default);

    /// <summary>Stages a lot's mutable fields (remaining amount, cost, currency, price, account, closed period).</summary>
    Task UpdateLotAsync(AccountingLot lot, CancellationToken cancellationToken = default);

    /// <summary>The lot (saved or staged), or null.</summary>
    Task<AccountingLot?> GetLotAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>The lots and debts with something left (saved, overlaid with what this unit of work staged), oldest first
    /// (by <see cref="AccountingLot.AcquiredAt"/>, then id); only those held by <paramref name="bucket"/> when
    /// set.</summary>
    Task<IReadOnlyList<AccountingLot>> ListOpenLotsAsync(AccountingLotBucket? bucket = null,
                                                         CancellationToken cancellationToken = default);

    /// <summary>
    /// What identifies the saved open lots and debts cheaply (NL-658): one aggregate read, so the financial projector can
    /// keep its pool across rounds and read every open lot again only when the saved ones changed. The default (test
    /// doubles) returns null: no fingerprint, the pool is read every round.
    /// </summary>
    Task<AccountingLotsFingerprint?> GetOpenLotsFingerprintAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<AccountingLotsFingerprint?>(null);

    /// <summary>Stages a relief (its <see cref="AccountingLotRelief.Id"/> is ignored and assigned by the save).</summary>
    void AddRelief(AccountingLotRelief relief);

    /// <summary>The saved reliefs of one lot, in disposal order.</summary>
    Task<IReadOnlyList<AccountingLotRelief>> ListReliefsByLotAsync(long lotId,
                                                                   CancellationToken cancellationToken = default);

    /// <summary>The saved reliefs of one disposing entry.</summary>
    Task<IReadOnlyList<AccountingLotRelief>> ListReliefsByEntryAsync(long ledgerSeq, int adjustment,
                                                                     CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the closed period of every lot acquired and every relief made before <paramref name="end"/> that has none
    /// yet (A3-T5); returns how many rows.
    /// </summary>
    Task<int> MarkClosedAsync(string periodId, DateTimeOffset end, CancellationToken cancellationToken = default);

    /// <summary>A page of the saved lots acquired before <paramref name="end"/>, whatever is left of them, in id order
    /// after <paramref name="afterId"/> (A3-T5: the lots open at a close).</summary>
    Task<IReadOnlyList<AccountingLot>> ListLotsAcquiredBeforeAsync(DateTimeOffset end, long afterId, int take,
                                                                   CancellationToken cancellationToken = default);

    /// <summary>
    /// A page of the saved reliefs, in id order after <paramref name="afterId"/> (A3-T5): with
    /// <paramref name="periodId"/>, the ones that closed period holds; without it, the ones in no closed period made
    /// before <paramref name="end"/> (what a close of a period ending there takes in).
    /// </summary>
    Task<IReadOnlyList<AccountingLotRelief>> ListPeriodReliefsAsync(string? periodId, DateTimeOffset end, long afterId,
                                                                    int take,
                                                                    CancellationToken cancellationToken = default);

    /// <summary>The msat relieved per lot by the saved reliefs made at or after <paramref name="since"/> (A3-T5: a lot's
    /// remaining amount at a close is its current one plus these).</summary>
    Task<IReadOnlyDictionary<long, long>> SumReliefsSinceAsync(DateTimeOffset since,
                                                               CancellationToken cancellationToken = default);

    /// <summary>Deletes every lot and relief at once (a rebuild before any close; not staged).</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>The saved lots of <paramref name="origin"/> that are no moved part (no <c>ParentLotId</c>), by id,
    /// whatever is left of them (A3-T4: the imported lots of D-A9). The default (test doubles) throws
    /// <see cref="NotSupportedException"/>.</summary>
    Task<IReadOnlyList<AccountingLot>> ListLotsByOriginAsync(AccountingLotOrigin origin,
                                                             CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository does not list lots by origin.");

    /// <summary>Deletes every lot of <paramref name="origin"/> (the parts moved from them included) with its reliefs at
    /// once (not staged; A3-T4: an import replaces the earlier one). The default (test doubles) throws <see cref="NotSupportedException"/>.</summary>
    Task<int> DeleteLotsByOriginAsync(AccountingLotOrigin origin, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository does not delete lots by origin.");

    /// <summary>
    /// The saved reliefs made in [<paramref name="since"/>, <paramref name="until"/>) with an id above
    /// <paramref name="afterId"/>, by id, at most <paramref name="take"/> (A3-T6's realized gains, paged by id). The
    /// default (test doubles) throws <see cref="NotSupportedException"/>.
    /// </summary>
    Task<IReadOnlyList<AccountingLotRelief>> ListReliefsAsync(DateTimeOffset? since, DateTimeOffset? until,
                                                              long afterId, int take,
                                                              CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository does not list reliefs by time.");
}

/// <summary>
/// The saved open lots and debts in a few numbers (NL-658): how many, the highest id, the msat they hold and how many of
/// them a close recorded. Equal fingerprints mean the financial projector's pool is the saved one.
/// </summary>
public sealed record AccountingLotsFingerprint(int OpenLots, long MaxId, long RemainingMsat, int ClosedLots);