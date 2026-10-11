using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// The period close, the lock and the signed close digests of the financial book (NL-602 A3-T5, plan
/// <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.2, D-A8, D-A13).
/// </summary>
/// <remarks>
/// <para><b>Close</b> (<see cref="CloseAsync"/>): the feed is sealed and both books projected first; then, inside the
/// financial projector's exclusive section and the books' write lock (<see cref="EnterAsync"/>), one save writes the
/// <c>AccountingPeriods</c> row (digest, signature, chain hash, closing state) and marks the period's financial entries,
/// lots and reliefs closed. Closes are contiguous: the first may start anywhere no financial entry precedes, each next
/// one starts where the last ended, and a period is closed only once it has ended. Refused while the period has
/// unvalued postings or unclassified entries, or a book lags behind the feed, unless forced (the row records
/// <c>Forced</c>).</para>
/// <para><b>Lock</b> (<see cref="IAccountingAdjustmentSink"/>): every time before the end of the last close is locked;
/// a write that would land there is staged by <see cref="StageAdjustmentAsync"/> as an adjustment entry of the open
/// period, dated now. <see cref="IAccountingBooksDbRepository.SetPostingValueAsync"/> also refuses a closed
/// posting.</para>
/// <para><b>Verify</b> (<see cref="VerifyClosesAsync"/>): every closed period's digest recomputed from the stored rows,
/// its signature checked against our node id, its chain hash against the feed's stored hash, its closing balances
/// against the previous close's plus its entries, and the contiguity of the closes.</para>
/// <para><b>Rebuild</b> (<see cref="RebuildFinancialAsync"/>): the closed periods stay; the book is reset to the last
/// close (<see cref="IAccountingBooksDbRepository.ResetToCloseAsync"/>, one transaction) and the projector replays the
/// open period from the close's replay point.</para>
/// </remarks>
public sealed class AccountingPeriodService : IAccountingPeriods, IAccountingAdjustmentSink, IDisposable
{
    /// <summary>The page size of the close's and verify's reads.</summary>
    public const int PageSize = 500;

    private const AccountingBook Financial = AccountingBook.Financial;

    private readonly IAccountingBooks? _books;
    private readonly Lock _cacheGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ILogger<AccountingPeriodService> _logger;
    private readonly IFinancialBooksProjector _projector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILightningSigner? _signer;
    private readonly TimeProvider _timeProvider;
    private readonly Counter<long> _closedCounter;
    private readonly Counter<long> _adjustmentCounter;

    private IReadOnlyList<AccountingPeriod>? _closedPeriods;
    private long _cacheVersion;

    public AccountingPeriodService(IServiceScopeFactory scopeFactory, ILogger<AccountingPeriodService> logger,
                                   IAccountingBooks? books = null, IFinancialBooksProjector? projector = null,
                                   ILightningSigner? signer = null, TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _books = books;
        _projector = projector ?? new NullFinancialBooksProjector();
        _signer = signer;
        _timeProvider = timeProvider ?? TimeProvider.System;

        Meter = new Meter(AccountingEventSealerService.MeterName);
        _closedCounter = Meter.CreateCounter<long>("nlightning.accounting.periods.closed", "{period}",
                                                   "Accounting periods closed (A3-T5)");
        _adjustmentCounter = Meter.CreateCounter<long>("nlightning.accounting.adjustments", "{entry}",
                                                       "Adjustment entries posted in the open period for a write that "
                                                     + "touched a closed one (D-A8)");
    }

    /// <summary>The meter, for tests that assert the instruments.</summary>
    internal Meter Meter { get; }

    /// <inheritdoc />
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        return new Releaser(_writeGate);
    }

    /// <inheritdoc />
    public async Task<AccountingPeriod?> GetLockingPeriodAsync(DateTimeOffset time,
                                                               CancellationToken cancellationToken = default)
    {
        var closed = await GetClosedPeriodsAsync(cancellationToken);
        if (closed.Count == 0 || time >= closed[^1].End)
            return null;

        return closed.FirstOrDefault(p => p.Start <= time && time < p.End) ?? closed[0];
    }

    /// <inheritdoc />
    public async Task<AccountingEntry?> StageAdjustmentAsync(IUnitOfWork unitOfWork, AccountingAdjustment adjustment,
                                                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(adjustment);
        ArgumentException.ThrowIfNullOrEmpty(adjustment.EventKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(adjustment.LedgerSeq);
        ArgumentNullException.ThrowIfNull(adjustment.Postings);
        if (adjustment.Postings.Sum(p => p.AmountMsat) != 0)
            throw new ArgumentException($"The adjustment of {adjustment.EventKey} does not balance", nameof(adjustment));
        if (adjustment.Postings.Any(p => string.IsNullOrWhiteSpace(p.AccountName)))
            throw new ArgumentException($"A line of the adjustment of {adjustment.EventKey} names no account",
                                        nameof(adjustment));

        var period = await GetLockingPeriodAsync(adjustment.FactOccurredAt, cancellationToken)
                  ?? throw new ArgumentException(
                         $"{adjustment.EventKey} occurred at {adjustment.FactOccurredAt:O}, in no closed period: post it "
                       + "in its own period", nameof(adjustment));

        var books = unitOfWork.AccountingBooksDbRepository;
        var existing = await books.GetEntriesByKeyAsync(Financial, adjustment.EventKey, cancellationToken);
        if (existing.Count > 0 && existing[0].LedgerSeq != adjustment.LedgerSeq)
            throw new ArgumentException(
                $"{adjustment.EventKey} is at ledger sequence {existing[0].LedgerSeq} in the book, not "
              + $"{adjustment.LedgerSeq}", nameof(adjustment));

        var tag = adjustment.DedupeKey is { Length: > 0 } dedupe ? $"[{dedupe}] " : string.Empty;
        if (adjustment.Reason == AccountingAdjustmentReason.LateFact)
        {
            if (existing.Count > 0)
                return null;
        }
        else if (tag.Length > 0
              && existing.Any(e => e.Adjustment > 0 && e.Note?.StartsWith(tag, StringComparison.Ordinal) == true))
        {
            return null;
        }

        var number = existing.Count == 0 ? 1 : Math.Max(1, existing.Max(e => e.Adjustment) + 1);
        var closed = await GetClosedPeriodsAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var openFrom = closed[^1].End;
        var occurredAt = now < openFrom ? openFrom : now;

        var note = $"{tag}adjusts {period.PeriodId}: {adjustment.Reason} of "
                 + adjustment.FactOccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                 + (string.IsNullOrEmpty(adjustment.Note) ? string.Empty : "; " + adjustment.Note);
        if (note.Length > AccountingSchemaLimits.NoteMaxLength)
            note = note[..AccountingSchemaLimits.NoteMaxLength];

        var entry = new AccountingEntry(adjustment.LedgerSeq, adjustment.EventKey, adjustment.Kind, occurredAt,
                                        adjustment.ChannelId, adjustment.PaymentHash, adjustment.Postings, note)
        {
            Book = Financial,
            Adjustment = number,
            Flags = adjustment.Flags | AccountingEntryFlags.Adjustment,
            Classification = adjustment.Classification,
            RuleId = adjustment.RuleId
        };
        await books.AddEntryAsync(entry, cancellationToken);
        _adjustmentCounter.Add(1, new KeyValuePair<string, object?>("reason", adjustment.Reason.ToString()));
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Accounting: {Reason} of {EventKey} ({FactTime:O}) touches the closed period "
                                 + "{PeriodId}; staged as adjustment {Adjustment} dated {OccurredAt:O}",
                                   adjustment.Reason, adjustment.EventKey, adjustment.FactOccurredAt,
                                   period.PeriodId, number, occurredAt);
        return entry;
    }

    /// <inheritdoc />
    public async Task<bool> AdjustLateValuationAsync(IUnitOfWork unitOfWork, AccountingLateValuation valuation,
                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(valuation);
        var key = valuation.Posting.Key;
        if (key.Book != Financial || valuation.Posting.AccountName is not { Length: > 0 } accountName)
            return false;

        var books = unitOfWork.AccountingBooksDbRepository;
        var entry = await FindEntryAsync(books, key, cancellationToken);
        if (entry?.ClosedPeriodId is null
         || await GetLockingPeriodAsync(entry.OccurredAt, cancellationToken) is null)
            return false;

        // The value lands where the line is held now: a reclassification after the close moved it (NL-681)
        if (FinancialChart.IsClassifiable(valuation.Posting.Account)
         && entry.Postings.ElementAtOrDefault(key.Index) is { } line)
        {
            var entries = await books.GetEntriesByKeyAsync(Financial, entry.EventKey, cancellationToken);
            accountName = AccountingReclassification.CurrentAccountOf(entries, line) is { Length: > 0 } current
                              ? current
                              : accountName;
        }

        await StageAdjustmentAsync(unitOfWork, new AccountingAdjustment(
                                       AccountingAdjustmentReason.Price, entry.LedgerSeq, entry.EventKey, entry.Kind,
                                       entry.OccurredAt,
                                       [
                                           new AccountingPosting(valuation.Posting.Account, 0)
                                           {
                                               AccountName = accountName,
                                               FiatAmount = valuation.FiatAmount,
                                               FiatCurrency = valuation.Price.Currency,
                                               PriceId = valuation.Price.Id
                                           }
                                       ])
        {
            ChannelId = entry.ChannelId,
            PaymentHash = entry.PaymentHash,
            DedupeKey = $"price:{key.LedgerSeq}:{key.Adjustment}:{key.Index}",
            Note = $"{valuation.Posting.AmountMsat} msat at {valuation.Price.Price} "
                                            + $"{valuation.Price.Currency}/BTC"
        }, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<AccountingCloseReport> CloseAsync(string period, bool force,
                                                        CancellationToken cancellationToken = default)
    {
        var range = AccountingPeriodRange.Parse(period);
        var books = EnsureFinancialBook();
        if (_signer is null)
            throw new AccountingCloseRefusedException("No node signer is registered: a close must be signed (D-A13).");

        var now = _timeProvider.GetUtcNow();
        if (range.End > now)
            throw new AccountingCloseRefusedException(
                $"The period {range.PeriodId} ends at {Format(range.End)}: close it after that.");

        // Seal and project both books first; what arrives after is checked against the close under the lock
        await books.ProjectNowAsync(cancellationToken);
        await _projector.ProjectAsync(cancellationToken);

        return await _projector.RunExclusiveAsync(ct => CloseCoreAsync(range, force, ct), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingCloseReport>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var periods = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingPeriodDbRepository;
        var list = await periods.ListAsync(cancellationToken);
        return list.Select(ToReport).ToList();
    }

    /// <inheritdoc />
    public async Task<AccountingCloseReport?> GetAsync(string period, CancellationToken cancellationToken = default)
    {
        var range = AccountingPeriodRange.Parse(period);
        using var scope = _scopeFactory.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingPeriodDbRepository
                                .GetAsync(range.PeriodId, cancellationToken);
        return stored is null ? null : ToReport(stored);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingCloseVerification>> VerifyClosesAsync(
        CancellationToken cancellationToken = default)
    {
        // A close digest combines several reads, including remaining lots plus subsequent reliefs. Financial
        // writers must not change those rows between queries or an intact close can appear corrupted.
        using var held = await EnterAsync(cancellationToken);
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var closed = (await unitOfWork.AccountingPeriodDbRepository.ListAsync(cancellationToken))
                    .Where(p => p.State == AccountingPeriodState.Closed)
                    .OrderBy(p => p.End)
                    .ToList();
        if (closed.Count == 0)
            return [];

        var nodeId = _signer is null ? null : (byte[])_signer.GetNodePublicKey();
        var results = new List<AccountingCloseVerification>(closed.Count);
        AccountingPeriod? previous = null;
        var previousState = AccountingClosingState.Empty;
        foreach (var period in closed)
        {
            var problems = new List<string>();
            var contiguous = previous is null || period.Start == previous.End;
            if (!contiguous)
                problems.Add($"starts at {Format(period.Start)}, not where {previous!.PeriodId} ends "
                           + $"({Format(previous.End)})");

            var chainHashMatches = await ChainHashMatchesAsync(unitOfWork, period, cancellationToken);
            if (!chainHashMatches)
                problems.Add($"the chain hash is not the feed's at #{period.LastLedgerSeq}");

            var header = new AccountingCloseHeader(period.PeriodId, period.Start, period.End, period.LastLedgerSeq,
                                                   period.ChainHash is { Length: AccountingEventHasher.HashLength } hash
                                                       ? hash
                                                       : new byte[AccountingEventHasher.HashLength],
                                                   previous?.Digest is { Length: AccountingEventHasher.HashLength } d
                                                       ? d
                                                       : null, nodeId ?? [], period.Forced,
                                                   period.ClosedAt ?? DateTimeOffset.MinValue);
            using var digest = new AccountingCloseDigest(header);
            var balances = AccountingClosingState.ToMap(previousState.Balances);
            await AddClosedEntriesAsync(unitOfWork, digest, period.PeriodId, balances, cancellationToken);
            await AddReliefsAsync(unitOfWork, digest, period.PeriodId, period.End, cancellationToken);
            await AddOpenLotsAsync(unitOfWork, digest, period.End, cancellationToken);
            var (entryCount, reliefCount, lotCount) = digest.Counts;
            var recomputed = digest.Finish(period.ClosingState ?? string.Empty);

            var digestMatches = period.Digest is { } stored && stored.AsSpan().SequenceEqual(recomputed);
            if (!digestMatches)
                problems.Add("the digest does not match the stored entries, reliefs, lots and closing state");

            var signatureValid = nodeId is not null && SignatureValid(period, nodeId);
            if (!signatureValid)
                problems.Add(nodeId is null
                                 ? "no node signer to check the signature"
                                 : "the signature is not our node key's over the stored digest");

            var expected = AccountingClosingState.FromMap(Financial, balances);
            AccountingClosingState? state = null;
            try
            {
                state = AccountingClosingState.Decode(period.ClosingState);
            }
            catch (FormatException)
            {
                // Reported below
            }

            var stateMatches = state is not null && SameBalances(state.Balances, expected);
            if (!stateMatches)
                problems.Add(state is null
                                 ? "the closing state is unreadable"
                                 : "the closing balances are not the previous close's plus the period's entries");

            var stray = await CountStrayEntriesAsync(unitOfWork.AccountingBooksDbRepository, period, cancellationToken);
            if (stray > 0)
                problems.Add($"{stray} financial entries dated in the period are not in its close (a write past the "
                           + "lock)");

            results.Add(new AccountingCloseVerification(period.PeriodId, digestMatches, signatureValid,
                                                        chainHashMatches, stateMatches, contiguous, entryCount,
                                                        reliefCount, lotCount,
                                                        problems.Count == 0 ? null : string.Join("; ", problems))
            {
                StrayEntryCount = stray
            });
            previous = period;
            previousState = state ?? new AccountingClosingState(0, expected);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<int> RebuildFinancialAsync(CancellationToken cancellationToken = default)
    {
        var books = EnsureFinancialBook();
        await books.ProjectNowAsync(cancellationToken);

        var from = await _projector.RunExclusiveAsync(async ct =>
        {
            using (await EnterAsync(ct))
            {
                using var scope = _scopeFactory.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var last = await unitOfWork.AccountingPeriodDbRepository.GetLastClosedAsync(ct);
                AccountingBookReset reset;
                if (last is null)
                {
                    reset = new AccountingBookReset(0, []);
                }
                else
                {
                    AccountingClosingState state;
                    try
                    {
                        state = AccountingClosingState.Decode(last.ClosingState);
                    }
                    catch (FormatException e)
                    {
                        throw new AccountingCloseRefusedException(
                            $"The closing state of {last.PeriodId} is unreadable ({e.Message}): run verify.");
                    }

                    reset = new AccountingBookReset(state.ReplayAfterLedgerSeq, state.Balances);
                }

                await unitOfWork.AccountingBooksDbRepository.ResetToCloseAsync(Financial, reset, ct);
                InvalidateCache();
                return last;
            }
        }, cancellationToken);

        _logger.LogInformation("Rebuilding the financial book from {From}",
                               from is null ? "the start of the books" : $"the close of {from.PeriodId}");
        var projected = await _projector.ProjectAsync(cancellationToken);
        _logger.LogInformation("Rebuilt the financial book: {Count} entries projected", projected);
        return projected;
    }

    public void Dispose()
    {
        _writeGate.Dispose();
        Meter.Dispose();
    }

    /// <summary>The close itself; the caller holds the projector's exclusive section.</summary>
    private async Task<AccountingCloseReport> CloseCoreAsync(AccountingPeriodRange range, bool force,
                                                             CancellationToken cancellationToken)
    {
        using var held = await EnterAsync(cancellationToken);
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var books = unitOfWork.AccountingBooksDbRepository;
        var periods = unitOfWork.AccountingPeriodDbRepository;

        var existing = await periods.GetAsync(range.PeriodId, cancellationToken);
        if (existing is { State: AccountingPeriodState.Closed })
            throw new AccountingCloseRefusedException(
                $"The period {range.PeriodId} was closed at {Format(existing.ClosedAt)}.");

        var closed = (await periods.ListAsync(cancellationToken))
                    .Where(p => p.State == AccountingPeriodState.Closed)
                    .OrderBy(p => p.End)
                    .ToList();
        var last = closed.Count == 0 ? null : closed[^1];
        if (last is not null && range.Start != last.End)
            throw new AccountingCloseRefusedException(
                $"Closes are contiguous: the next period to close starts at {Format(last.End)} (the end of "
              + $"{last.PeriodId}), not at {Format(range.Start)}.");

        if (last is null)
        {
            var before = await books.ListEntriesAsync(new AccountingEntryQuery(0, 1, Until: range.Start)
            {
                Book = Financial
            }, cancellationToken);
            if (before.Count > 0)
                throw new AccountingCloseRefusedException(
                    $"The financial book has entries before {Format(range.Start)} (the first at "
                  + $"{Format(before[0].OccurredAt)}): close the period that holds them first (closes are "
                  + "contiguous).");
        }

        var financialCursor = await books.GetCursorAsync(Financial, cancellationToken);
        var operationalCursor = await books.GetCursorAsync(cancellationToken);
        var tip = await unitOfWork.AccountingEventDbRepository.GetChainTipAsync(cancellationToken);
        var reasons = new List<string>();
        if (operationalCursor < tip.LedgerSeq)
            reasons.Add($"the operational book is at #{operationalCursor}, the feed at #{tip.LedgerSeq}");
        if (financialCursor < operationalCursor)
            reasons.Add($"the financial book is at #{financialCursor}, the operational one at #{operationalCursor}");

        var unvalued = await books.CountOpenUnvaluedPostingsAsync(Financial, range.End, cancellationToken);
        if (unvalued > 0)
            reasons.Add($"{unvalued} postings have no fiat value yet");

        var unclassified = await CountUnclassifiedAsync(books, range, cancellationToken);
        if (unclassified > 0)
            reasons.Add($"{unclassified} entries are unclassified");

        if (reasons.Count > 0 && !force)
            throw new AccountingCloseRefusedException(
                $"The period {range.PeriodId} is not ready to close: {string.Join("; ", reasons)}. Fix them (prices "
              + "fetch, classify) or close with --force.", unvalued, unclassified);

        var chainHash = await GetChainHashAsync(unitOfWork, financialCursor, cancellationToken)
                     ?? throw new AccountingCloseRefusedException(
                            $"The feed has no sealed event #{financialCursor} with a chain hash: run verify.");

        AccountingClosingState previousState;
        try
        {
            previousState = last is null
                                ? AccountingClosingState.Empty
                                : AccountingClosingState.Decode(last.ClosingState);
        }
        catch (FormatException e)
        {
            throw new AccountingCloseRefusedException(
                $"The closing state of {last!.PeriodId} is unreadable ({e.Message}): run verify.");
        }

        var forced = reasons.Count > 0;
        var closedAt = _timeProvider.GetUtcNow();
        var nodeId = (byte[])_signer!.GetNodePublicKey();
        var header = new AccountingCloseHeader(range.PeriodId, range.Start, range.End, financialCursor, chainHash,
                                               last?.Digest, nodeId, forced, closedAt);
        using var digest = new AccountingCloseDigest(header);
        var balances = AccountingClosingState.ToMap(previousState.Balances);
        var priceIds = new HashSet<long>();
        await AddOpenEntriesAsync(books, digest, range, balances, priceIds, cancellationToken);
        await AddReliefsAsync(unitOfWork, digest, null, range.End, cancellationToken);
        await AddOpenLotsAsync(unitOfWork, digest, range.End, cancellationToken);

        // A rebuild from this close replays from the first entry of the open period on
        var firstOpen = await books.ListEntriesAsync(new AccountingEntryQuery(0, 1, range.End)
        {
            Book = Financial
        }, cancellationToken);
        var replayAfter = firstOpen.Count == 0
                              ? financialCursor
                              : Math.Min(financialCursor, firstOpen[0].LedgerSeq - 1);
        var priceSnapshots = new List<AccountingPrice>();
        foreach (var priceId in priceIds.Order())
        {
            var price = await unitOfWork.AccountingPriceDbRepository.GetByIdAsync(priceId, cancellationToken)
                     ?? throw new InvalidOperationException($"Price {priceId} of a closing posting is missing.");
            priceSnapshots.Add(price);
        }
        var state = new AccountingClosingState(replayAfter, AccountingClosingState.FromMap(Financial, balances))
        {
            Prices = priceSnapshots
        };
        var stateText = state.Encode();
        var (entryCount, reliefCount, lotCount) = digest.Counts;
        var digestBytes = digest.Finish(stateText);
        var signature = (byte[])_signer.SignNodeMessage(new Hash(digestBytes));

        var row = new AccountingPeriod(range.PeriodId, range.Start, range.End, AccountingPeriodState.Closed, closedAt,
                                       financialCursor, chainHash, digestBytes, signature, forced, stateText);
        if (existing is null)
            await periods.AddAsync(row, cancellationToken);
        else
            await periods.UpdateAsync(row, cancellationToken);

        var marked = await books.MarkEntriesClosedAsync(Financial, range.PeriodId, range.Start, range.End,
                                                        cancellationToken);
        await unitOfWork.AccountingLotDbRepository.MarkClosedAsync(range.PeriodId, range.End, cancellationToken);
        if (marked != entryCount)
            throw new InvalidOperationException(
                $"The close of {range.PeriodId} read {entryCount} entries but would mark {marked}");

        await unitOfWork.SaveChangesAsync();
        InvalidateCache();
        _closedCounter.Add(1, new KeyValuePair<string, object?>("forced", forced));
        _logger.LogInformation("Accounting period {PeriodId} closed{Forced}: {Entries} financial entries, {Reliefs} "
                             + "reliefs, {Lots} lots open at its end, through ledger sequence {LedgerSeq}; digest "
                             + "{Digest}", range.PeriodId, forced ? " (forced: " + string.Join("; ", reasons) + ")" : "",
                               entryCount, reliefCount, lotCount, financialCursor,
                               Convert.ToHexStringLower(digestBytes));

        return new AccountingCloseReport(row, state)
        {
            NodeId = nodeId,
            EntryCount = entryCount,
            ReliefCount = reliefCount,
            OpenLotCount = lotCount,
            UnvaluedPostings = unvalued,
            UnclassifiedEntries = unclassified
        };
    }

    /// <summary>The entry of a posting (its ledger sequence's page, then the adjustment).</summary>
    private static async Task<AccountingEntry?> FindEntryAsync(IAccountingBooksDbRepository books,
                                                               AccountingPostingKey key,
                                                               CancellationToken cancellationToken)
    {
        var page = await books.ListEntriesAsync(new AccountingEntryQuery(key.LedgerSeq, 1)
        {
            Book = key.Book,
            AfterAdjustment = key.Adjustment - 1
        }, cancellationToken);
        return page.FirstOrDefault(e => e.LedgerSeq == key.LedgerSeq && e.Adjustment == key.Adjustment);
    }

    private IAccountingBooks EnsureFinancialBook()
    {
        if (_books is not { IsEnabled: true } books)
            throw new AccountingCloseRefusedException("The accounting books are off (Accounting:Enabled=false).");
        if (!_projector.IsEnabled)
            throw new AccountingCloseRefusedException(
                "The financial book is off (Accounting:Profile=Financial): there is nothing to close or rebuild.");

        return books;
    }

    private async Task<IReadOnlyList<AccountingPeriod>> GetClosedPeriodsAsync(CancellationToken cancellationToken)
    {
        long version;
        lock (_cacheGate)
        {
            if (_closedPeriods is { } cached)
                return cached;

            version = _cacheVersion;
        }

        using var scope = _scopeFactory.CreateScope();
        var periods = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingPeriodDbRepository
                                 .ListAsync(cancellationToken);
        var closed = periods.Where(p => p.State == AccountingPeriodState.Closed).OrderBy(p => p.End).ToList();

        // A close or rebuild that committed while this read ran invalidated the cache: never store what it read
        lock (_cacheGate)
        {
            if (_cacheVersion == version)
                _closedPeriods ??= closed;
        }

        return closed;
    }

    /// <summary>Forgets the cached closes; called under the write lock right after a close or reset commits, so a
    /// writer's next check (under the same lock) reads the new state.</summary>
    private void InvalidateCache()
    {
        lock (_cacheGate)
        {
            _closedPeriods = null;
            _cacheVersion++;
        }
    }

    private static async Task<int> CountUnclassifiedAsync(IAccountingBooksDbRepository books,
                                                          AccountingPeriodRange range,
                                                          CancellationToken cancellationToken)
    {
        var count = 0;
        long afterSeq = 0;
        var afterAdjustment = -1;
        while (true)
        {
            var page = await books.ListEntriesAsync(new AccountingEntryQuery(afterSeq, PageSize, range.Start, range.End)
            {
                Book = Financial,
                AfterAdjustment = afterAdjustment,
                WithFlags = AccountingEntryFlags.Unclassified
            }, cancellationToken);
            count += page.Count;
            if (page.Count < PageSize)
                return count;

            afterSeq = page[^1].LedgerSeq;
            afterAdjustment = page[^1].Adjustment;
        }
    }

    /// <summary>The financial entries dated in a closed period that its close does not hold.</summary>
    private static async Task<long> CountStrayEntriesAsync(IAccountingBooksDbRepository books, AccountingPeriod period,
                                                           CancellationToken cancellationToken)
    {
        long stray = 0;
        long afterSeq = 0;
        var afterAdjustment = -1;
        while (true)
        {
            var page = await books.ListEntriesAsync(
                           new AccountingEntryQuery(afterSeq, PageSize, period.Start, period.End)
                           {
                               Book = Financial,
                               AfterAdjustment = afterAdjustment
                           }, cancellationToken);
            stray += page.Count(e => e.ClosedPeriodId != period.PeriodId);
            if (page.Count < PageSize)
                return stray;

            afterSeq = page[^1].LedgerSeq;
            afterAdjustment = page[^1].Adjustment;
        }
    }

    /// <summary>The entries the close takes in: the financial book's in the period (all in no closed period yet).</summary>
    private static async Task AddOpenEntriesAsync(
        IAccountingBooksDbRepository books, AccountingCloseDigest digest, AccountingPeriodRange range,
        Dictionary<(AccountRole Account, string Name), (long Msat, decimal Fiat)> balances,
        HashSet<long> priceIds, CancellationToken cancellationToken)
    {
        long afterSeq = 0;
        var afterAdjustment = -1;
        while (true)
        {
            var page = await books.ListEntriesAsync(new AccountingEntryQuery(afterSeq, PageSize, range.Start, range.End)
            {
                Book = Financial,
                AfterAdjustment = afterAdjustment
            }, cancellationToken);
            foreach (var entry in page)
            {
                if (entry.ClosedPeriodId is not null)
                    throw new InvalidOperationException(
                        $"The entry {entry.EventKey}/{entry.Adjustment} of {range.PeriodId} is already in the closed "
                      + $"period {entry.ClosedPeriodId}");

                AddEntry(digest, entry, balances);
                foreach (var posting in entry.Postings)
                    if (posting.PriceId is { } priceId) priceIds.Add(priceId);
            }

            if (page.Count < PageSize)
                return;

            afterSeq = page[^1].LedgerSeq;
            afterAdjustment = page[^1].Adjustment;
        }
    }

    /// <summary>The entries a closed period holds (verify).</summary>
    private static async Task AddClosedEntriesAsync(
        IUnitOfWork unitOfWork, AccountingCloseDigest digest, string periodId,
        Dictionary<(AccountRole Account, string Name), (long Msat, decimal Fiat)> balances,
        CancellationToken cancellationToken)
    {
        long afterSeq = 0;
        var afterAdjustment = -1;
        while (true)
        {
            var page = await unitOfWork.AccountingBooksDbRepository.ListEntriesAsync(
                           new AccountingEntryQuery(afterSeq, PageSize)
                           {
                               Book = Financial,
                               AfterAdjustment = afterAdjustment,
                               ClosedPeriodId = periodId
                           }, cancellationToken);
            foreach (var entry in page)
                AddEntry(digest, entry, balances);

            if (page.Count < PageSize)
                return;

            afterSeq = page[^1].LedgerSeq;
            afterAdjustment = page[^1].Adjustment;
        }
    }

    private static void AddEntry(AccountingCloseDigest digest, AccountingEntry entry,
                                 Dictionary<(AccountRole Account, string Name), (long Msat, decimal Fiat)> balances)
    {
        digest.AddEntry(entry);
        foreach (var posting in entry.Postings)
        {
            var key = (posting.Account, posting.AccountName ?? string.Empty);
            var (msat, fiat) = balances.GetValueOrDefault(key);
            balances[key] = (checked(msat + posting.AmountMsat), fiat + (posting.FiatAmount ?? 0m));
        }
    }

    private static async Task AddReliefsAsync(IUnitOfWork unitOfWork, AccountingCloseDigest digest, string? periodId,
                                              DateTimeOffset end, CancellationToken cancellationToken)
    {
        long afterId = 0;
        while (true)
        {
            var page = await unitOfWork.AccountingLotDbRepository.ListPeriodReliefsAsync(
                           periodId, end, afterId, PageSize, cancellationToken);
            foreach (var relief in page)
                digest.AddRelief(relief);

            if (page.Count < PageSize)
                return;

            afterId = page[^1].Id;
        }
    }

    /// <summary>The lots open at <paramref name="end"/>: acquired before it, with something left then (their current
    /// remaining amount plus what was relieved since).</summary>
    private static async Task AddOpenLotsAsync(IUnitOfWork unitOfWork, AccountingCloseDigest digest,
                                               DateTimeOffset end, CancellationToken cancellationToken)
    {
        var lots = unitOfWork.AccountingLotDbRepository;
        var relievedSince = await lots.SumReliefsSinceAsync(end, cancellationToken);
        long afterId = 0;
        while (true)
        {
            var page = await lots.ListLotsAcquiredBeforeAsync(end, afterId, PageSize, cancellationToken);
            foreach (var lot in page)
            {
                var remaining = checked(lot.RemainingMsat + relievedSince.GetValueOrDefault(lot.Id));
                if (remaining > 0)
                    digest.AddOpenLot(lot, remaining);
            }

            if (page.Count < PageSize)
                return;

            afterId = page[^1].Id;
        }
    }

    private static async Task<byte[]?> GetChainHashAsync(IUnitOfWork unitOfWork, long ledgerSeq,
                                                         CancellationToken cancellationToken)
    {
        if (ledgerSeq == 0)
            return new byte[AccountingEventHasher.HashLength];

        var events = await unitOfWork.AccountingEventDbRepository.GetSealedRangeAsync(ledgerSeq, 1, cancellationToken);
        return events.FirstOrDefault(e => e.LedgerSeq == ledgerSeq)?.Hash is { Length: AccountingEventHasher.HashLength }
                   hash
                   ? hash
                   : null;
    }

    private static async Task<bool> ChainHashMatchesAsync(IUnitOfWork unitOfWork, AccountingPeriod period,
                                                          CancellationToken cancellationToken)
    {
        if (period.ChainHash is not { Length: AccountingEventHasher.HashLength } stored)
            return false;

        var feed = await GetChainHashAsync(unitOfWork, period.LastLedgerSeq, cancellationToken);
        return feed is not null && feed.AsSpan().SequenceEqual(stored);
    }

    private bool SignatureValid(AccountingPeriod period, byte[] nodeId)
    {
        if (period.Digest is not { Length: AccountingEventHasher.HashLength } storedDigest
         || period.Signature is not { Length: 64 } signature)
            return false;

        return _signer!.VerifyNodeMessage(new Hash(storedDigest), new CompactSignature(signature), nodeId);
    }

    private static bool SameBalances(IReadOnlyList<AccountingAccountBalance> stored,
                                     IReadOnlyList<AccountingAccountBalance> expected)
    {
        var left = AccountingClosingState.FromMap(Financial, AccountingClosingState.ToMap(stored));
        if (left.Count != expected.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].Account != expected[i].Account
             || !string.Equals(left[i].AccountName, expected[i].AccountName, StringComparison.Ordinal)
             || left[i].BalanceMsat != expected[i].BalanceMsat || left[i].FiatAmount != expected[i].FiatAmount)
                return false;
        }

        return true;
    }

    private AccountingCloseReport ToReport(AccountingPeriod period)
    {
        AccountingClosingState? state = null;
        if (period.ClosingState is not null)
        {
            try
            {
                state = AccountingClosingState.Decode(period.ClosingState);
            }
            catch (FormatException e)
            {
                _logger.LogWarning(e, "The closing state of accounting period {PeriodId} is unreadable",
                                   period.PeriodId);
            }
        }

        return new AccountingCloseReport(period, state)
        {
            NodeId = _signer is null ? null : (byte[])_signer.GetNodePublicKey()
        };
    }

    private static string Format(DateTimeOffset? time) =>
        time is { } value
            ? value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : "an unknown time";

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}