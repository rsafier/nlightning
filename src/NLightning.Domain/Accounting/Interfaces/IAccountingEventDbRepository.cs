namespace NLightning.Domain.Accounting.Interfaces;

using Enums;
using Models;

/// <summary>
/// The accounting feed's table (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §3-§4).
/// </summary>
/// <remarks>
/// <para>Writers only <see cref="Add"/>: the row is staged on the unit of work and committed by the same
/// <c>SaveChangesAsync</c> as the state change it records. Nothing here may make that save fail: there is no unique
/// constraint on the key (the sealer marks duplicates instead).</para>
/// <para>The sealer reads <see cref="GetUnsealedAsync"/>, <see cref="GetChainTipAsync"/> and
/// <see cref="GetSealedKeysAsync"/> and stages <see cref="ApplySealsAsync"/>; readers page sealed rows with
/// <see cref="ListAsync"/>.</para>
/// </remarks>
public interface IAccountingEventDbRepository
{
    /// <summary>Stages a new, unsealed event.</summary>
    void Add(AccountingEventModel accountingEvent);

    /// <summary>Whether an event with this key is saved or staged in this unit of work (sealed or not, duplicates
    /// excluded).</summary>
    Task<bool> ExistsAsync(string eventKey, CancellationToken cancellationToken = default);

    /// <summary>Saved, unsealed rows in storage id order (duplicates already marked are excluded).</summary>
    Task<IReadOnlyList<AccountingEventModel>> GetUnsealedAsync(int max, CancellationToken cancellationToken = default);

    /// <summary>The last sealed event, or <see cref="AccountingChainTip.Genesis"/>.</summary>
    Task<AccountingChainTip> GetChainTipAsync(CancellationToken cancellationToken = default);

    /// <summary>The keys among <paramref name="eventKeys"/> that are already sealed.</summary>
    Task<IReadOnlySet<string>> GetSealedKeysAsync(IReadOnlyCollection<string> eventKeys,
                                                  CancellationToken cancellationToken = default);

    /// <summary>Stages the sealer's decisions.</summary>
    Task ApplySealsAsync(IReadOnlyList<AccountingSeal> seals, CancellationToken cancellationToken = default);

    /// <summary>Sealed events in ledger order.</summary>
    Task<IReadOnlyList<AccountingEventModel>> ListAsync(AccountingEventQuery query,
                                                        CancellationToken cancellationToken = default);

    /// <summary>
    /// Standing sealed wallet received/spent events in the inclusive height range, paged in ledger order. Reversals
    /// anywhere in the sealed feed cancel their referenced event, independently of the reversal's block height.
    /// </summary>
    Task<IReadOnlyList<AccountingEventModel>> GetWalletHistoryAsync(
        uint startHeight, uint endHeight, long afterLedgerSeq, int take,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AccountingEventModel>>([]);

    /// <summary>Sealed events from <paramref name="fromLedgerSeq"/> on, in ledger order, for a chain check.</summary>
    Task<IReadOnlyList<AccountingEventModel>> GetSealedRangeAsync(long fromLedgerSeq, int take,
                                                                  CancellationToken cancellationToken = default);

    /// <summary>
    /// The events of <paramref name="kinds"/> whose block height is at or above <paramref name="height"/>, saved (sealed
    /// or not) or staged in this unit of work, duplicates excluded: what a reorg's rewind reverses
    /// (<see cref="Services.AccountingConfirmations"/>). Not indexed by height: for the rare rewind only.
    /// </summary>
    Task<IReadOnlyList<AccountingEventModel>> GetAtOrAboveHeightAsync(
        uint height, IReadOnlyCollection<AccountingEventKind> kinds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The events whose key starts with <paramref name="keyPrefix"/> (ordinal), saved (sealed or not) or staged in this
    /// unit of work, duplicates excluded: a fact's confirmations and their reversals
    /// (<see cref="Services.AccountingConfirmations.NextConfirmationKey"/>).
    /// </summary>
    Task<IReadOnlyList<AccountingEventModel>> GetByKeyPrefixAsync(string keyPrefix,
                                                                   CancellationToken cancellationToken = default);

    /// <summary>
    /// The event with this key, staged in this unit of work or saved (sealed or not, duplicates excluded; the first
    /// written when several are), or null. Writers of compensating entries read the original's amounts with it (a
    /// reorg's <see cref="Enums.AccountingEventKind.Reversal"/>). The default (test doubles) knows none.
    /// </summary>
    Task<AccountingEventModel?> GetByKeyAsync(string eventKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<AccountingEventModel?>(null);
}