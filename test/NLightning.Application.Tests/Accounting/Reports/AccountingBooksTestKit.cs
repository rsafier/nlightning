using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Accounting.Reports;

using Application.Accounting;
using Application.Accounting.Export;
using Application.Accounting.Reports;
using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// The report and export services over in-memory books and feed (NL-602 A2): <see cref="Add"/> puts a sealed event and
/// its entry in both, <see cref="Books"/> is the mocked <see cref="IAccountingBooks"/>.
/// </summary>
internal sealed class AccountingBooksTestKit
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public InMemoryBooks BooksRepository { get; } = new();
    public InMemoryFeed Feed { get; } = new();
    public Mock<IAccountingBooks> Books { get; } = new();
    public FixedClock Clock { get; } = new();
    public AccountingOptions Options { get; } = new();

    private readonly ServiceProvider _provider;

    public AccountingBooksTestKit()
    {
        Books.SetupGet(b => b.IsEnabled).Returns(true);
        Books.Setup(b => b.ProjectNowAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        Clock.Now = T0.AddDays(100);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.AccountingBooksDbRepository).Returns(BooksRepository);
        unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(Feed);
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        _provider = services.BuildServiceProvider();
    }

    public AccountingReportService CreateReports(bool withBooks = true) =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), withBooks ? Books.Object : null,
            NullLogger<AccountingReportService>.Instance, Microsoft.Extensions.Options.Options.Create(Options), null,
            Clock);

    public AccountingExportService CreateExports() =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), Books.Object,
            NullLogger<AccountingExportService>.Instance, Microsoft.Extensions.Options.Options.Create(Options));

    /// <summary>Adds a sealed event (the next ledger sequence) and its entry with <paramref name="postings"/>.</summary>
    public AccountingEventModel Add(AccountingEventKind kind, DateTimeOffset at, long amountMsat = 0, long feeMsat = 0,
                                    ChannelId? channelId = null, CompactPubKey? counterparty = null,
                                    IReadOnlyDictionary<string, string>? details = null, string? key = null,
                                    params (AccountRole Account, long AmountMsat)[] postings)
    {
        var seq = Feed.Events.Count + 1L;
        var accountingEvent = new AccountingEventModel
        {
            EventKey = key ?? $"test:{seq}",
            Kind = kind,
            OccurredAt = at,
            ChannelId = channelId,
            Counterparty = counterparty,
            AmountMsat = amountMsat,
            FeeMsat = feeMsat,
            Details = details ?? new SortedDictionary<string, string>(StringComparer.Ordinal),
            LedgerSeq = seq,
            Hash = new byte[32]
        };
        Feed.Events.Add(accountingEvent);
        BooksRepository.Entries.Add(new AccountingEntry(seq, accountingEvent.EventKey, kind, at, channelId, null,
                                                        postings.Select(p => new AccountingPosting(p.Account,
                                                                                                   p.AmountMsat))
                                                                .ToList()));
        return accountingEvent;
    }

    public static ChannelId Channel(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    public static CompactPubKey Peer(byte seed)
    {
        var bytes = Enumerable.Repeat(seed, 33).ToArray();
        bytes[0] = 0x02;
        return new CompactPubKey(bytes);
    }

    /// <summary>A clock that reads <see cref="Now"/>.</summary>
    internal sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The books' tables in memory: balances and sums computed from the entries.</summary>
    internal sealed class InMemoryBooks : IAccountingBooksDbRepository
    {
        public List<AccountingEntry> Entries { get; } = [];
        public long Cursor { get; set; }

        public AccountingEntryQuery? LastQuery { get; private set; }

        public Task<long> GetCursorAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Cursor == 0 && Entries.Count > 0 ? Entries[^1].LedgerSeq : Cursor);

        public Task SetCursorAsync(long ledgerSeq, CancellationToken cancellationToken = default)
        {
            Cursor = ledgerSeq;
            return Task.CompletedTask;
        }

        public Task AddEntryAsync(AccountingEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<AccountingEntry?> GetEntryByKeyAsync(string eventKey,
                                                         CancellationToken cancellationToken = default) =>
            Task.FromResult(Entries.FirstOrDefault(e => e.EventKey == eventKey));

        public Task<IReadOnlyDictionary<AccountRole, long>> GetBalancesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Sum(Entries));

        public Task<IReadOnlyDictionary<AccountRole, long>> SumPostingsAsync(
            DateTimeOffset? since, DateTimeOffset? until, CancellationToken cancellationToken = default) =>
            Task.FromResult(Sum(Entries.Where(e => (since is null || e.OccurredAt >= since)
                                                && (until is null || e.OccurredAt < until))));

        public Task<IReadOnlyList<AccountingEntry>> ListEntriesAsync(AccountingEntryQuery query,
                                                                     CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            IReadOnlyList<AccountingEntry> page =
                Entries.Where(e => e.LedgerSeq > query.AfterLedgerSeq
                                && (query.Since is null || e.OccurredAt >= query.Since)
                                && (query.Until is null || e.OccurredAt < query.Until)
                                && (query.Kinds is not { Count: > 0 } kinds || kinds.Contains(e.Kind))
                                && (query.ChannelId is not { } channel || e.ChannelId == channel)
                                && (query.Account is not { } account || e.Postings.Any(p => p.Account == account)))
                       .OrderBy(e => e.LedgerSeq)
                       .Take(query.Take)
                       .ToList();
            return Task.FromResult(page);
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Entries.Clear();
            Cursor = 0;
            return Task.CompletedTask;
        }

        private static IReadOnlyDictionary<AccountRole, long> Sum(IEnumerable<AccountingEntry> entries) =>
            entries.SelectMany(e => e.Postings)
                   .GroupBy(p => p.Account)
                   .ToDictionary(g => g.Key, g => g.Sum(p => p.AmountMsat));
    }

    /// <summary>The sealed feed in memory (only the reads the reports use).</summary>
    internal sealed class InMemoryFeed : IAccountingEventDbRepository
    {
        public List<AccountingEventModel> Events { get; } = [];

        public void Add(AccountingEventModel accountingEvent) => Events.Add(accountingEvent);

        public Task<bool> ExistsAsync(string eventKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Events.Any(e => e.EventKey == eventKey));

        public Task<IReadOnlyList<AccountingEventModel>> GetUnsealedAsync(int max,
                                                                          CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountingEventModel>>([]);

        public Task<AccountingChainTip> GetChainTipAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountingChainTip(Events.Count, new byte[32]));

        public Task<IReadOnlySet<string>> GetSealedKeysAsync(IReadOnlyCollection<string> eventKeys,
                                                             CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task ApplySealsAsync(IReadOnlyList<AccountingSeal> seals, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AccountingEventModel>> ListAsync(AccountingEventQuery query,
                                                                   CancellationToken cancellationToken = default)
        {
            IReadOnlyList<AccountingEventModel> page =
                Events.Where(e => e.LedgerSeq > query.AfterLedgerSeq
                               && (query.Kinds is not { Count: > 0 } kinds || kinds.Contains(e.Kind))
                               && (query.ChannelId is not { } channel || e.ChannelId == channel)
                               && (query.Since is null || e.OccurredAt >= query.Since)
                               && (query.Until is null || e.OccurredAt < query.Until))
                      .OrderBy(e => e.LedgerSeq)
                      .Take(query.Take)
                      .ToList();
            return Task.FromResult(page);
        }

        public Task<IReadOnlyList<AccountingEventModel>> GetSealedRangeAsync(
            long fromLedgerSeq, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountingEventModel>>(
                Events.Where(e => e.LedgerSeq >= fromLedgerSeq).OrderBy(e => e.LedgerSeq).Take(take).ToList());

        public Task<IReadOnlyList<AccountingEventModel>> GetAtOrAboveHeightAsync(
            uint height, IReadOnlyCollection<AccountingEventKind> kinds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountingEventModel>>([]);

        public Task<IReadOnlyList<AccountingEventModel>> GetByKeyPrefixAsync(
            string keyPrefix, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountingEventModel>>([]);
    }
}