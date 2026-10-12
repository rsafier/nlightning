namespace NLightning.Domain.Accounting.Interfaces;

using Enums;
using Models;

/// <summary>
/// The feed of a unit of work that stores none (test doubles): writes go nowhere, reads are empty.
/// </summary>
public sealed class NullAccountingEventDbRepository : IAccountingEventDbRepository
{
    public static NullAccountingEventDbRepository Instance { get; } = new();

    private NullAccountingEventDbRepository()
    {
    }

    public void Add(AccountingEventModel accountingEvent)
    {
    }

    public Task<bool> ExistsAsync(string eventKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<AccountingEventModel>> GetUnsealedAsync(int max,
                                                                       CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AccountingEventModel>>([]);

    public Task<AccountingChainTip> GetChainTipAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AccountingChainTip.Genesis);

    public Task<IReadOnlySet<string>> GetSealedKeysAsync(IReadOnlyCollection<string> eventKeys,
                                                         CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    public Task ApplySealsAsync(IReadOnlyList<AccountingSeal> seals, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<AccountingEventModel>> ListAsync(AccountingEventQuery query,
                                                               CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AccountingEventModel>>([]);

    public Task<IReadOnlyList<AccountingEventModel>> GetSealedRangeAsync(
        long fromLedgerSeq, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AccountingEventModel>>([]);

    public Task<IReadOnlyList<AccountingEventModel>> GetAtOrAboveHeightAsync(
        uint height, IReadOnlyCollection<AccountingEventKind> kinds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AccountingEventModel>>([]);

    public Task<IReadOnlyList<AccountingEventModel>> GetByKeyPrefixAsync(
        string keyPrefix, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AccountingEventModel>>([]);
}