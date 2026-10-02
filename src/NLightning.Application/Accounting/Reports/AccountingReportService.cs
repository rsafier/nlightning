using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting.Reports;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// The reports of the operational books (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.1 "Reports", IPC 43): the
/// balance sheet and income statement from the books' postings, the per-channel, per-peer and fee views from the
/// feed's events and their details, and the register from the books' entries.
/// </summary>
/// <remarks>
/// Every call refuses when the books are off (<see cref="AccountingBooksDisabledException"/>), then seals what was
/// committed and projects it (<see cref="IAccountingBooks.ProjectNowAsync"/>), then reads through a scope of its own.
/// The channel view reads the feed in pages of <see cref="PageSize"/>: it is an on-demand report, never on a hot path.
/// </remarks>
public sealed class AccountingReportService : IAccountingReports
{
    /// <summary>The page size of the feed reads.</summary>
    internal const int PageSize = 500;

    private const string OnchainLostDetail = "lostMsat";

    private static readonly TimeSpan s_minimumYieldPeriod = TimeSpan.FromHours(1);
    private static readonly TimeSpan s_year = TimeSpan.FromDays(365.25);

    private static readonly AccountingEventKind[] s_channelMetadataKinds =
    [
        AccountingEventKind.ChannelFunded, AccountingEventKind.SpliceLocked, AccountingEventKind.ChannelClosedMutual,
        AccountingEventKind.ChannelForceClosed
    ];

    private readonly AccountingBooksAccess _access;
    private readonly AccountNames _names;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public AccountingReportService(IServiceScopeFactory scopeFactory, IAccountingBooks? books,
                                   ILogger<AccountingReportService> logger,
                                   IOptions<AccountingOptions>? options = null,
                                   IAccountingEventSealer? sealer = null, TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _access = new AccountingBooksAccess(books, sealer, logger);
        _names = (options?.Value ?? new AccountingOptions()).GetAccountNames();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<AccountingBalanceSheet> GetBalanceSheetAsync(DateTimeOffset? at,
                                                                   CancellationToken cancellationToken = default)
    {
        await _access.PrepareAsync(cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var books = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository;
        var cursor = await books.GetCursorAsync(cancellationToken);
        var balances = at is { } time
                           ? await books.SumPostingsAsync(null, time, cancellationToken)
                           : await books.GetBalancesAsync(cancellationToken);

        return new AccountingBalanceSheet(at, cursor, Lines(balances, AccountingAccountCategory.Assets, 1),
                                          Lines(balances, AccountingAccountCategory.Liabilities, -1),
                                          Lines(balances, AccountingAccountCategory.Equity, -1),
                                          -Sum(balances, AccountingAccountCategory.Income)
                                        - Sum(balances, AccountingAccountCategory.Expenses));
    }

    /// <inheritdoc/>
    public async Task<AccountingIncomeStatement> GetIncomeStatementAsync(DateTimeOffset? since, DateTimeOffset? until,
                                                                         CancellationToken cancellationToken = default)
    {
        ThrowIfEmptyWindow(since, until);
        await _access.PrepareAsync(cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var books = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository;
        var cursor = await books.GetCursorAsync(cancellationToken);
        var sums = await books.SumPostingsAsync(since, until, cancellationToken);

        return new AccountingIncomeStatement(since, until, cursor, Lines(sums, AccountingAccountCategory.Income, -1),
                                             Lines(sums, AccountingAccountCategory.Expenses, 1));
    }

    /// <inheritdoc/>
    public async Task<AccountingFeesReport> GetFeesReportAsync(DateTimeOffset? since, DateTimeOffset? until,
                                                               CancellationToken cancellationToken = default)
    {
        ThrowIfEmptyWindow(since, until);
        await _access.PrepareAsync(cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var books = unitOfWork.AccountingBooksDbRepository;
        var cursor = await books.GetCursorAsync(cancellationToken);
        var sums = await books.SumPostingsAsync(since, until, cancellationToken);
        var accounts = AccountingAccountCategories.FeeAccounts
                                                  .Select(role => new AccountingAccountLine(
                                                              role, _names[role], sums.GetValueOrDefault(role)))
                                                  .ToList();

        var bumps = new List<AccountingSweepFeeBump>();
        await foreach (var accountingEvent in ReadEventsAsync(unitOfWork.AccountingEventDbRepository,
                                                              [AccountingEventKind.SweepFeeBump, AccountingEventKind.Reversal],
                                                              null, since, until, cancellationToken))
        {
            if (accountingEvent.Kind == AccountingEventKind.Reversal
             && OriginalKindOf(accountingEvent) != AccountingEventKind.SweepFeeBump)
                continue;

            bumps.Add(new AccountingSweepFeeBump(accountingEvent.LedgerSeq ?? 0, accountingEvent.OccurredAt,
                                                 accountingEvent.TxId, accountingEvent.ChannelId,
                                                 accountingEvent.FeeMsat,
                                                 accountingEvent.Details.GetValueOrDefault("purpose")));
        }

        return new AccountingFeesReport(since, until, cursor, accounts, bumps);
    }

    /// <inheritdoc/>
    public async Task<AccountingRegister> GetRegisterAsync(AccountingEntryQuery query,
                                                           CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.AfterLedgerSeq);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Take);
        ThrowIfEmptyWindow(query.Since, query.Until);
        await _access.PrepareAsync(cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var books = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository;
        var cursor = await books.GetCursorAsync(cancellationToken);
        var entries = await books.ListEntriesAsync(query, cancellationToken);
        var nextAfter = entries.Count > 0 ? entries[^1].LedgerSeq : query.AfterLedgerSeq;
        return new AccountingRegister(entries, nextAfter, entries.Count == query.Take, cursor);
    }

    /// <inheritdoc/>
    public async Task<AccountingChannelsReport> GetChannelsReportAsync(DateTimeOffset? since, DateTimeOffset? until,
                                                                       ChannelId? channelId = null,
                                                                       CancellationToken cancellationToken = default)
    {
        ThrowIfEmptyWindow(since, until);
        await _access.PrepareAsync(cancellationToken);
        var asOf = until ?? _timeProvider.GetUtcNow();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var cursor = await unitOfWork.AccountingBooksDbRepository.GetCursorAsync(cancellationToken);
        var events = unitOfWork.AccountingEventDbRepository;
        var channels = new Dictionary<ChannelId, ChannelAccumulator>();

        // The channels' lives (funding, splices, closes) up to the end, whatever the start: a channel opened before the
        // period still has its capacity and its open time
        await foreach (var accountingEvent in ReadEventsAsync(events, s_channelMetadataKinds, channelId, null, until,
                                                              cancellationToken))
            ApplyLifecycle(GetOrAdd(channels, accountingEvent.ChannelId), accountingEvent);

        // The money of the period
        var active = new HashSet<ChannelId>();
        await foreach (var accountingEvent in ReadEventsAsync(events, null, null, since, until, cancellationToken))
            ApplyMoney(channels, active, accountingEvent);

        var lines = channels.Values
                            .Where(c => channelId is { } only
                                            ? c.ChannelId == only
                                            : active.Contains(c.ChannelId) || WasOpenDuring(c, since, asOf))
                            .OrderBy(c => c.ChannelId.ToString(), StringComparer.Ordinal)
                            .Select(c => c.ToLine(since, asOf))
                            .ToList();
        if (channelId is { } requested && lines.Count == 0)
            lines.Add(new ChannelAccumulator(requested).ToLine(since, asOf));

        return new AccountingChannelsReport(since, until, asOf, cursor, lines, SumPerPeer(lines));
    }

    private IReadOnlyList<AccountingAccountLine> Lines(IReadOnlyDictionary<AccountRole, long> balances,
                                                       AccountingAccountCategory category, int sign) =>
        balances.Where(b => AccountingAccountCategories.Of(b.Key) == category)
                .OrderBy(b => (int)b.Key)
                .Select(b => new AccountingAccountLine(b.Key, _names[b.Key], sign * b.Value))
                .ToList();

    private static long Sum(IReadOnlyDictionary<AccountRole, long> balances, AccountingAccountCategory category) =>
        balances.Where(b => AccountingAccountCategories.Of(b.Key) == category).Sum(b => b.Value);

    private static void ThrowIfEmptyWindow(DateTimeOffset? since, DateTimeOffset? until)
    {
        if (since is { } start && until is { } end && end <= start)
            throw new ArgumentException("until must be after since.", nameof(until));
    }

    private static async IAsyncEnumerable<AccountingEventModel> ReadEventsAsync(
        IAccountingEventDbRepository repository, IReadOnlyCollection<AccountingEventKind>? kinds, ChannelId? channelId,
        DateTimeOffset? since, DateTimeOffset? until,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var after = 0L;
        while (true)
        {
            var page = await repository.ListAsync(new AccountingEventQuery(after, PageSize, kinds, channelId, since,
                                                                           until), cancellationToken);
            foreach (var accountingEvent in page)
                yield return accountingEvent;

            if (page.Count < PageSize || page[^1].LedgerSeq is not { } last || last <= after)
                yield break;

            after = last;
        }
    }

    private static void ApplyLifecycle(ChannelAccumulator? channel, AccountingEventModel accountingEvent)
    {
        if (channel is null)
            return;

        channel.Observe(accountingEvent);
        switch (accountingEvent.Kind)
        {
            case AccountingEventKind.ChannelFunded:
                channel.CapacityMsat = SatDetail(accountingEvent, "capacitySat") ?? channel.CapacityMsat;
                channel.OpenedAt ??= accountingEvent.OccurredAt;
                if (bool.TryParse(accountingEvent.Details.GetValueOrDefault("isInitiator"), out var isInitiator))
                    channel.IsInitiator = isInitiator;
                break;
            case AccountingEventKind.SpliceLocked:
                channel.CapacityMsat = SatDetail(accountingEvent, "capacitySat") ?? channel.CapacityMsat;
                break;
            case AccountingEventKind.ChannelClosedMutual:
            case AccountingEventKind.ChannelForceClosed:
                channel.ClosedAt ??= accountingEvent.OccurredAt;
                break;
        }
    }

    private static void ApplyMoney(Dictionary<ChannelId, ChannelAccumulator> channels, HashSet<ChannelId> active,
                                   AccountingEventModel accountingEvent)
    {
        switch (accountingEvent.Kind)
        {
            case AccountingEventKind.ForwardSettled:
                {
                    var fee = accountingEvent.AmountMsat;
                    var incoming = GetOrAdd(channels, ChannelDetail(accountingEvent, "incomingChannelId")
                                                   ?? accountingEvent.ChannelId);
                    if (incoming is not null)
                    {
                        incoming.Observe(accountingEvent);
                        incoming.ObserveScid(accountingEvent.Details.GetValueOrDefault("incomingScid"));
                        incoming.RoutingInMsat += fee;
                        incoming.ForwardsIn++;
                        incoming.ForwardedInMsat += MsatDetail(accountingEvent, "incomingAmountMsat") ?? 0;
                        active.Add(incoming.ChannelId);
                    }

                    var outgoing = GetOrAdd(channels, ChannelDetail(accountingEvent, "outgoingChannelId"));
                    if (outgoing is not null)
                    {
                        outgoing.ObserveScid(accountingEvent.Details.GetValueOrDefault("outgoingScid"));
                        outgoing.RoutingOutMsat += fee;
                        outgoing.ForwardsOut++;
                        outgoing.ForwardedOutMsat += MsatDetail(accountingEvent, "outgoingAmountMsat") ?? 0;
                        active.Add(outgoing.ChannelId);
                    }

                    return;
                }
            case AccountingEventKind.PaymentSucceeded:
                {
                    // The counterparty of a payment is the payee, not the channel's peer: only the channel is taken
                    if (GetOrAdd(channels, accountingEvent.ChannelId) is not { } channel)
                        return;

                    var amount = -accountingEvent.AmountMsat - accountingEvent.FeeMsat;
                    if (accountingEvent.Details.GetValueOrDefault("selfPayment") == "true")
                    {
                        channel.RebalancedOutMsat += amount;
                        channel.RebalanceCostMsat += accountingEvent.FeeMsat;
                    }
                    else
                    {
                        channel.PaymentsSentMsat += amount;
                        channel.PaymentsSent++;
                        channel.RoutingFeesPaidMsat += accountingEvent.FeeMsat;
                    }

                    active.Add(channel.ChannelId);
                    return;
                }
            case AccountingEventKind.InvoiceSettled:
                {
                    if (GetOrAdd(channels, accountingEvent.ChannelId) is not { } channel)
                        return;

                    channel.Observe(accountingEvent);
                    // NL-609: the incoming side of our own rebalance is no payment received (its cost is the
                    // outgoing PaymentSucceeded's fee)
                    if (accountingEvent.Details.GetValueOrDefault("selfPayment") != "true")
                    {
                        channel.PaymentsReceivedMsat += accountingEvent.AmountMsat;
                        channel.PaymentsReceived++;
                    }

                    active.Add(channel.ChannelId);
                    return;
                }
            case AccountingEventKind.PushSent:
            case AccountingEventKind.PushReceived:
                {
                    if (GetOrAdd(channels, accountingEvent.ChannelId) is not { } channel)
                        return;

                    channel.Observe(accountingEvent);
                    channel.PushMsat += accountingEvent.AmountMsat;
                    active.Add(channel.ChannelId);
                    return;
                }
            case AccountingEventKind.ForwardLostOnchain:
                {
                    if (GetOrAdd(channels, accountingEvent.ChannelId) is not { } channel)
                        return;

                    channel.OnchainLossMsat -= accountingEvent.AmountMsat;
                    active.Add(channel.ChannelId);
                    return;
                }
            case AccountingEventKind.Reversal:
                {
                    // A reorg's reversal negates both amounts of an on-chain event: its fees and losses come off again
                    if (OriginalKindOf(accountingEvent) is { } originalKind
                     && GetOrAdd(channels, accountingEvent.ChannelId) is { } channel
                     && ApplyOnchain(channel, originalKind, accountingEvent))
                        active.Add(channel.ChannelId);
                    return;
                }
            default:
                {
                    if (GetOrAdd(channels, accountingEvent.ChannelId) is { } channel
                     && ApplyOnchain(channel, accountingEvent.Kind, accountingEvent))
                    {
                        channel.Observe(accountingEvent);
                        active.Add(channel.ChannelId);
                    }

                    return;
                }
        }
    }

    // The on-chain fees and losses of a channel; false for a kind that has none
    private static bool ApplyOnchain(ChannelAccumulator channel, AccountingEventKind kind,
                                     AccountingEventModel accountingEvent)
    {
        switch (kind)
        {
            case AccountingEventKind.ChannelFunded:
                channel.FundingFeeMsat += accountingEvent.FeeMsat;
                return true;
            case AccountingEventKind.SpliceLocked:
                channel.SpliceFeeMsat += accountingEvent.FeeMsat;
                return true;
            case AccountingEventKind.ChannelClosedMutual:
                channel.CloseFeeMsat += accountingEvent.FeeMsat;
                return true;
            case AccountingEventKind.ChannelForceClosed:
                channel.CommitmentFeeMsat += accountingEvent.FeeMsat;
                // A reversal carries no details: only its negated fee comes off
                channel.OnchainLossMsat += MsatDetail(accountingEvent, OnchainLostDetail) ?? 0;
                return true;
            case AccountingEventKind.OutputResolved:
            case AccountingEventKind.PenaltyClaimed:
                channel.SweepFeeMsat += accountingEvent.FeeMsat;
                return true;
            case AccountingEventKind.BreachLoss:
                channel.SweepFeeMsat += accountingEvent.FeeMsat;
                channel.OnchainLossMsat -= accountingEvent.AmountMsat;
                return true;
            case AccountingEventKind.AnchorCpfpFee:
                channel.CpfpFeeMsat += accountingEvent.FeeMsat;
                return true;
            default:
                return false;
        }
    }

    private static bool WasOpenDuring(ChannelAccumulator channel, DateTimeOffset? since, DateTimeOffset asOf) =>
        channel.OpenedAt is { } opened && opened < asOf && (channel.ClosedAt is not { } closed || since is not { } start
                                                          || closed >= start);

    private static IReadOnlyList<AccountingPeerLine> SumPerPeer(IReadOnlyList<AccountingChannelLine> lines) =>
        lines.GroupBy(l => l.Counterparty?.ToString())
             .OrderBy(g => g.Key is null)
             .ThenBy(g => g.Key, StringComparer.Ordinal)
             .Select(g => new AccountingPeerLine
             {
                 Counterparty = g.First().Counterparty,
                 ChannelCount = g.Count(),
                 CapacityMsat = g.Sum(l => l.CapacityMsat ?? 0),
                 RoutingInMsat = g.Sum(l => l.RoutingInMsat),
                 RoutingOutMsat = g.Sum(l => l.RoutingOutMsat),
                 ForwardsIn = g.Sum(l => l.ForwardsIn),
                 ForwardsOut = g.Sum(l => l.ForwardsOut),
                 PaymentsSentMsat = g.Sum(l => l.PaymentsSentMsat),
                 PaymentsReceivedMsat = g.Sum(l => l.PaymentsReceivedMsat),
                 RoutingFeesPaidMsat = g.Sum(l => l.RoutingFeesPaidMsat),
                 RebalanceCostMsat = g.Sum(l => l.RebalanceCostMsat),
                 OnchainFeesMsat = g.Sum(l => l.OnchainFeesMsat),
                 OnchainLossMsat = g.Sum(l => l.OnchainLossMsat),
                 NetMsat = g.Sum(l => l.NetMsat)
             })
             .ToList();

    private static ChannelAccumulator? GetOrAdd(Dictionary<ChannelId, ChannelAccumulator> channels,
                                                ChannelId? channelId)
    {
        if (channelId is not { } id)
            return null;

        if (!channels.TryGetValue(id, out var channel))
        {
            channel = new ChannelAccumulator(id);
            channels.Add(id, channel);
        }

        return channel;
    }

    private static AccountingEventKind? OriginalKindOf(AccountingEventModel accountingEvent) =>
        Enum.TryParse<AccountingEventKind>(
            accountingEvent.Details.GetValueOrDefault(AccountingConfirmations.OriginalKindDetail), false,
            out var kind)
            ? kind
            : null;

    private static ChannelId? ChannelDetail(AccountingEventModel accountingEvent, string key)
    {
        if (accountingEvent.Details.GetValueOrDefault(key) is not { Length: 64 } hex)
            return null;

        try
        {
            return new ChannelId(Convert.FromHexString(hex));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static long? MsatDetail(AccountingEventModel accountingEvent, string key) =>
        long.TryParse(accountingEvent.Details.GetValueOrDefault(key), NumberStyles.AllowLeadingSign,
                      CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static long? SatDetail(AccountingEventModel accountingEvent, string key) =>
        long.TryParse(accountingEvent.Details.GetValueOrDefault(key), NumberStyles.None, CultureInfo.InvariantCulture,
                      out var value) && value <= long.MaxValue / 1_000
            ? value * 1_000
            : null;

    private sealed class ChannelAccumulator(ChannelId channelId)
    {
        public ChannelId ChannelId { get; } = channelId;
        public string? ShortChannelId { get; private set; }
        public CompactPubKey? Counterparty { get; private set; }
        public long? CapacityMsat { get; set; }
        public bool? IsInitiator { get; set; }
        public DateTimeOffset? OpenedAt { get; set; }
        public DateTimeOffset? ClosedAt { get; set; }
        public long RoutingInMsat { get; set; }
        public long RoutingOutMsat { get; set; }
        public int ForwardsIn { get; set; }
        public int ForwardsOut { get; set; }
        public long ForwardedInMsat { get; set; }
        public long ForwardedOutMsat { get; set; }
        public long PaymentsSentMsat { get; set; }
        public int PaymentsSent { get; set; }
        public long PaymentsReceivedMsat { get; set; }
        public int PaymentsReceived { get; set; }
        public long RoutingFeesPaidMsat { get; set; }
        public long RebalancedOutMsat { get; set; }
        public long RebalanceCostMsat { get; set; }
        public long PushMsat { get; set; }
        public long FundingFeeMsat { get; set; }
        public long SpliceFeeMsat { get; set; }
        public long CloseFeeMsat { get; set; }
        public long CommitmentFeeMsat { get; set; }
        public long SweepFeeMsat { get; set; }
        public long CpfpFeeMsat { get; set; }
        public long OnchainLossMsat { get; set; }

        // The channel's own scid and peer, from an event that is about this channel
        public void Observe(AccountingEventModel accountingEvent)
        {
            if (accountingEvent.ChannelId is not { } id || id != ChannelId)
                return;

            if (accountingEvent.ShortChannelId is { } scid)
                ShortChannelId = scid.ToString();
            if (accountingEvent.Counterparty is { } peer)
                Counterparty = peer;
        }

        public void ObserveScid(string? scid)
        {
            if (!string.IsNullOrEmpty(scid))
                ShortChannelId ??= scid;
        }

        public AccountingChannelLine ToLine(DateTimeOffset? since, DateTimeOffset asOf)
        {
            double? yieldOnCapacity = CapacityMsat is > 0 ? (double)RoutingOutMsat / CapacityMsat.Value : null;
            double? annualized = null;
            if (yieldOnCapacity is { } yield && OpenedAt is { } opened)
            {
                var start = since is { } periodStart && periodStart > opened ? periodStart : opened;
                var end = ClosedAt is { } closed && closed < asOf ? closed : asOf;
                var open = end - start;
                if (open >= s_minimumYieldPeriod)
                    annualized = yield * (s_year / open);
            }

            return new AccountingChannelLine
            {
                ChannelId = ChannelId,
                ShortChannelId = ShortChannelId,
                Counterparty = Counterparty,
                CapacityMsat = CapacityMsat,
                IsInitiator = IsInitiator,
                OpenedAt = OpenedAt,
                ClosedAt = ClosedAt,
                RoutingInMsat = RoutingInMsat,
                RoutingOutMsat = RoutingOutMsat,
                ForwardsIn = ForwardsIn,
                ForwardsOut = ForwardsOut,
                ForwardedInMsat = ForwardedInMsat,
                ForwardedOutMsat = ForwardedOutMsat,
                PaymentsSentMsat = PaymentsSentMsat,
                PaymentsSent = PaymentsSent,
                PaymentsReceivedMsat = PaymentsReceivedMsat,
                PaymentsReceived = PaymentsReceived,
                RoutingFeesPaidMsat = RoutingFeesPaidMsat,
                RebalancedOutMsat = RebalancedOutMsat,
                RebalanceCostMsat = RebalanceCostMsat,
                PushMsat = PushMsat,
                FundingFeeMsat = FundingFeeMsat,
                SpliceFeeMsat = SpliceFeeMsat,
                CloseFeeMsat = CloseFeeMsat,
                CommitmentFeeMsat = CommitmentFeeMsat,
                SweepFeeMsat = SweepFeeMsat,
                CpfpFeeMsat = CpfpFeeMsat,
                OnchainLossMsat = OnchainLossMsat,
                YieldOnCapacity = yieldOnCapacity,
                AnnualizedYield = annualized
            };
        }
    }
}