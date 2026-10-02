using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting.Books;

using Domain.Accounting.Books;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Channels.Enums;
using Domain.Persistence.Interfaces;

/// <summary>
/// The operational books (NL-602 A2, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.1, §6.3, §10): the projector that
/// turns every sealed accounting event after its cursor into one <see cref="AccountingEntry"/>, the rebuild and the
/// reconcile against the node's live balances.
/// </summary>
/// <remarks>
/// <para><b>Projection.</b> Every <see cref="Interval"/> (once at <see cref="Start"/>), and on
/// <see cref="ProjectNowAsync"/>, a round reads the sealed events after the cursor in pages of
/// <see cref="AccountingOptions.SealBatchSize"/>, posts each through the rules
/// (<see cref="AccountingPostingRules.Evaluate"/> by default, with its note kept on the entry), stages the entries (an event that posts nothing still gets
/// one) and the new cursor, and commits them in one save per page: the projection is exactly-once (a save that fails
/// commits nothing, and the next round projects the same events again). Rounds never overlap (one gate for the loop,
/// <see cref="ProjectNowAsync"/>, <see cref="RebuildAsync"/> and <see cref="ReconcileAsync"/>).</para>
/// <para><b>A rules failure</b> (an exception, or postings that do not balance) stops the round at that event: the
/// entries before it are saved with the cursor just before it, the event is never skipped (the books must not
/// silently diverge from the feed), and the next round tries it again. It is logged as critical with the event key
/// the first time (debug afterwards), counted on <c>nlightning.accounting.projection.failures</c> and reported by
/// <see cref="ProjectionError"/> and every reconcile line's note until a round gets past it.</para>
/// <para><b>Books off</b> (<see cref="AccountingOptions.AreBooksEnabled"/> false): nothing runs, the tables and the
/// cursor stay, and a node started with the books on again catches up from the cursor. The feed is never off.</para>
/// <para><b>Reconcile</b> (on demand, and every <see cref="AccountingOptions.SnapshotInterval"/> in the loop, logged
/// only): see <see cref="BuildReconcileLines"/>. A drift is logged as a warning and recorded on the gauge
/// <c>nlightning.accounting.reconcile.drift_msat</c> by account; it is never posted.</para>
/// <para>An exception in the books is logged and metered and stops only the books, never the node.</para>
/// </remarks>
public sealed class AccountingBooksService : IAccountingBooks, IAsyncDisposable, IDisposable
{
    /// <summary>The most pages (saves) one round of the background loop projects; the next round continues.</summary>
    public const int MaxBatchesPerRound = 100;

    /// <summary>The event detail copied into the entry's note.</summary>
    public const string NoteDetail = "note";

    private readonly Lock _stateGate = new();
    private readonly SemaphoreSlim _roundGate = new(1, 1);
    private readonly ILogger<AccountingBooksService> _logger;
    private readonly AccountingOptions _options;
    private readonly Func<AccountingEventModel, Func<string, AccountingEntry?>, AccountingPostingResult> _rules;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAccountingEventSealer? _sealer;
    private readonly INodeSnapshotSource? _snapshotSource;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeProvider _timeProvider;
    private readonly Counter<long> _projectedCounter;
    private readonly Counter<long> _failureCounter;
    private readonly Gauge<long> _driftGauge;

    private Task? _loop;
    private bool _started;
    private bool _stopped;
    private long _totalProjected;
    private string? _reportedFailureKey;
    private volatile string? _projectionError;
    private volatile AccountingReconcileResult? _lastReconcile;

    public AccountingBooksService(
        IServiceScopeFactory scopeFactory, ILogger<AccountingBooksService> logger,
        IOptions<AccountingOptions>? options = null, IAccountingEventSealer? sealer = null,
        INodeSnapshotSource? snapshotSource = null, TimeProvider? timeProvider = null,
        Func<AccountingEventModel, Func<string, AccountingEntry?>, IReadOnlyList<AccountingPosting>>? rules = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options?.Value ?? new AccountingOptions();
        _sealer = sealer;
        _snapshotSource = snapshotSource;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rules = rules is null
                     ? AccountingPostingRules.Evaluate
                     : (accountingEvent, find) => new AccountingPostingResult(rules(accountingEvent, find) ?? []);

        Meter = new Meter(AccountingEventSealerService.MeterName);
        _projectedCounter = Meter.CreateCounter<long>("nlightning.accounting.entries.projected", "{entry}",
                                                      "Accounting events posted to the books");
        _failureCounter = Meter.CreateCounter<long>("nlightning.accounting.projection.failures", "{failure}",
                                                    "Accounting events the books could not post (a rules bug); the "
                                                  + "projection waits at the event");
        _driftGauge = Meter.CreateGauge<long>("nlightning.accounting.reconcile.drift_msat", "msat",
                                              "The books' balance minus the node's live balance, by account, at the "
                                            + "last reconcile");
    }

    /// <inheritdoc />
    public bool IsEnabled => _options.AreBooksEnabled;

    /// <summary>The interval between projection rounds (<see cref="AccountingOptions.SealInterval"/>, the default when
    /// not positive).</summary>
    public TimeSpan Interval => _options.SealInterval > TimeSpan.Zero
                                    ? _options.SealInterval
                                    : AccountingOptions.DefaultSealInterval;

    /// <summary>The entries projected since the process started (a rebuild included).</summary>
    public long TotalProjected => Interlocked.Read(ref _totalProjected);

    /// <summary>The event the projection waits at (its key and the rules' error), or null.</summary>
    public string? ProjectionError => _projectionError;

    /// <summary>The last reconcile (on demand or from the loop), or null.</summary>
    public AccountingReconcileResult? LastReconcile => _lastReconcile;

    /// <summary>The meter, for tests that assert the instruments.</summary>
    internal Meter Meter { get; }

    /// <summary>Starts the rounds (the first one at once). Nothing when the books are off; idempotent; nothing after
    /// <see cref="StopAsync"/>.</summary>
    public void Start()
    {
        lock (_stateGate)
        {
            if (_started || _stopped || !IsEnabled)
                return;

            _started = true;
            _loop = Task.Run(RunAsync);
        }
    }

    /// <summary>Stops the rounds; a page in progress finishes first.</summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_stateGate)
        {
            if (_stopped)
                return;

            _stopped = true;
            loop = _loop;
        }

        await _stopping.CancelAsync();
        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // Stopping
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>The sealer seals what was committed first; a sealer failure is logged and what is sealed is
    /// projected.</remarks>
    public async Task<int> ProjectNowAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return 0;

        await SealFirstAsync(cancellationToken);
        await _roundGate.WaitAsync(cancellationToken);
        try
        {
            return await ProjectAsync(int.MaxValue, cancellationToken);
        }
        finally
        {
            _roundGate.Release();
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The books are off.</exception>
    public async Task<int> RebuildAsync(CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        await SealFirstAsync(cancellationToken);
        await _roundGate.WaitAsync(cancellationToken);
        try
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await unitOfWork.AccountingBooksDbRepository.ClearAsync(cancellationToken);
                await unitOfWork.SaveChangesAsync();
            }

            _logger.LogInformation("Rebuilding the accounting books from the start of the feed");
            var projected = await ProjectAsync(int.MaxValue, cancellationToken);
            _logger.LogInformation("Rebuilt the accounting books: {Count} entries", projected);
            return projected;
        }
        finally
        {
            _roundGate.Release();
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The books are off, or no snapshot source is registered.</exception>
    public async Task<AccountingReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (_snapshotSource is null)
            throw new InvalidOperationException("No node snapshot source is registered");

        await SealFirstAsync(cancellationToken);
        await _roundGate.WaitAsync(cancellationToken);
        try
        {
            await ProjectAsync(int.MaxValue, cancellationToken);
            return await ReconcileCoreAsync(cancellationToken);
        }
        finally
        {
            _roundGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose();
        Meter.Dispose();
    }

    /// <summary>Cancels the loop without waiting for it (a container disposed synchronously); prefer
    /// <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        lock (_stateGate)
            _stopped = true;

        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed asynchronously before
        }

        Meter.Dispose();
    }

    /// <summary>
    /// The reconcile lines of the books' running balances against a live snapshot (plan §6.1 "Reconcile"):
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="AccountRole.Channels"/>: the gross local balances (our offered HTLCs in flight included, as the
    /// books keep them) of the channels past their funding confirmation (a short channel id, or a state past the
    /// funding wait) whose funding is not spent: a channel resolving on chain, or failed with outputs of its close
    /// already known, is in <see cref="AccountRole.Pending"/> instead.</item>
    /// <item><see cref="AccountRole.Pending"/>: the snapshot's unspent outputs of our force closes, HTLC outputs
    /// included. Definitional gap: the snapshot counts every output that is ours to take (HTLC outputs either way,
    /// the outputs of a revoked commitment we can punish, our anchor when the peer funded), while the books hold only
    /// the outputs that were ours per the commitment that confirmed (the others are booked when we claim them), so
    /// the node may be above the books while such outputs are unresolved.</item>
    /// <item><see cref="AccountRole.Wallet"/>: every wallet output in a block (the snapshot's confirmed and
    /// unconfirmed amounts: the books post a deposit at its first block, the snapshot calls it confirmed at 3).</item>
    /// <item><see cref="AccountRole.Clearing"/>: 0; see the line's note for what may legitimately be outstanding.</item>
    /// </list>
    /// </remarks>
    internal static IReadOnlyList<AccountingReconcileLine> BuildReconcileLines(
        AccountingSnapshot snapshot, IReadOnlyDictionary<AccountRole, long> balances, string? projectionError = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(balances);

        long channelsMsat = 0;
        var counted = 0;
        var awaitingFunding = 0;
        var onchain = 0;
        foreach (var bucket in snapshot.Channels)
        {
            if (IsOnchain(bucket))
            {
                onchain++;
                continue;
            }

            if (!IsPastFundingConfirmation(bucket))
            {
                awaitingFunding++;
                continue;
            }

            counted++;
            channelsMsat += bucket.LocalBalanceMsat;
        }

        var pendingMsat = snapshot.PendingOnchainMsat + snapshot.PendingHtlcOnchainMsat;
        var walletMsat = snapshot.Wallet.ConfirmedMsat + snapshot.Wallet.UnconfirmedMsat;
        var prefix = projectionError is null ? string.Empty : $"books stopped at {projectionError}; ";

        return
        [
            new AccountingReconcileLine(AccountRole.Channels, balances.GetValueOrDefault(AccountRole.Channels),
                                        channelsMsat,
                                        $"{prefix}gross local balances of {counted} channels past their funding "
                                      + $"confirmation ({awaitingFunding} awaiting their funding and {onchain} on "
                                      + "chain left out)"),
            new AccountingReconcileLine(AccountRole.Pending, balances.GetValueOrDefault(AccountRole.Pending),
                                        pendingMsat,
                                        $"{prefix}{snapshot.PendingSweepCount} unspent outputs of force closes "
                                      + $"({snapshot.PendingHtlcOnchainMsat} msat of HTLC outputs); "
                                      + $"{snapshot.PendingUncountedMsat} msat more of outputs the books book only once "
                                      + "claimed (the peer's HTLCs, a revoked commitment's outputs, a fundee's anchor) "
                                      + "left out"),
            new AccountingReconcileLine(AccountRole.Wallet, balances.GetValueOrDefault(AccountRole.Wallet),
                                        walletMsat,
                                        $"{prefix}wallet outputs in a block ({snapshot.Wallet.UnconfirmedMsat} msat "
                                      + "with fewer than 3 confirmations)"),
            new AccountingReconcileLine(AccountRole.Clearing, balances.GetValueOrDefault(AccountRole.Clearing), 0,
                                        $"{prefix}nets to zero once the transactions settle; may be outstanding: a "
                                      + "funding or splice below its depth (wallet inputs spent, channel not booked "
                                      + "yet), a close, sweep or withdrawal whose wallet side is not in a block yet, "
                                      + "an output to an address outside the wallet")
        ];
    }

    private static bool IsOnchain(ChannelBalanceBucket bucket) =>
        bucket.State is ChannelState.OnchainResolving or ChannelState.Closed or ChannelState.Stale
     || (bucket.State is ChannelState.Failed && bucket.PendingSweepCount > 0);

    private static bool IsPastFundingConfirmation(ChannelBalanceBucket bucket) =>
        bucket.ShortChannelId is not null
     || bucket.State is ChannelState.ReadyForUs or ChannelState.Open or ChannelState.ShuttingDown
                     or ChannelState.Negotiating or ChannelState.Closing;

    private void EnsureEnabled()
    {
        if (!IsEnabled)
            throw new InvalidOperationException("The accounting books are off (Accounting:Enabled=false)");
    }

    private async Task SealFirstAsync(CancellationToken cancellationToken)
    {
        if (_sealer is null)
            return;

        try
        {
            await _sealer.SealNowAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not seal the accounting events first; the books project what is sealed");
        }
    }

    /// <summary>Projects up to <paramref name="maxBatches"/> pages; the caller holds the round gate.</summary>
    private async Task<int> ProjectAsync(int maxBatches, CancellationToken cancellationToken)
    {
        var batchSize = Math.Max(1, _options.SealBatchSize);
        var total = 0;
        for (var batchNumber = 0; batchNumber < maxBatches; batchNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var books = unitOfWork.AccountingBooksDbRepository;
            var cursor = await books.GetCursorAsync(cancellationToken);
            var page = await unitOfWork.AccountingEventDbRepository.ListAsync(
                           new AccountingEventQuery(cursor, batchSize), cancellationToken);
            if (page.Count == 0)
                break;

            // Entries staged in this page first, then the saved ones (prefetched for a reversal's target)
            var staged = new Dictionary<string, AccountingEntry>(StringComparer.Ordinal);
            var known = new Dictionary<string, AccountingEntry?>(StringComparer.Ordinal);

            AccountingEntry? Find(string key)
            {
                if (staged.TryGetValue(key, out var entry))
                    return entry;
                if (known.TryGetValue(key, out var saved))
                    return saved;

                // Not prefetched: the rules looked up a key that is not the event's "reverses" detail
                saved = books.GetEntryByKeyAsync(key, cancellationToken).GetAwaiter().GetResult();
                known[key] = saved;
                return saved;
            }

            var last = cursor;
            AccountingEventModel? failedEvent = null;
            Exception? failure = null;
            foreach (var accountingEvent in page)
            {
                if (accountingEvent.Details.TryGetValue(AccountingConfirmations.ReversesDetail, out var reversed)
                 && !string.IsNullOrEmpty(reversed) && !staged.ContainsKey(reversed) && !known.ContainsKey(reversed))
                    known[reversed] = await books.GetEntryByKeyAsync(reversed, cancellationToken);

                AccountingEntry entry;
                try
                {
                    entry = BuildEntry(accountingEvent, Find);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failedEvent = accountingEvent;
                    failure = e;
                    break;
                }

                await books.AddEntryAsync(entry, cancellationToken);
                staged[entry.EventKey] = entry;
                last = entry.LedgerSeq;
            }

            if (staged.Count > 0)
            {
                // The cursor commits with the entries it covers (exactly-once)
                await books.SetCursorAsync(last, cancellationToken);
                await unitOfWork.SaveChangesAsync();

                total += staged.Count;
                _projectedCounter.Add(staged.Count);
                Interlocked.Add(ref _totalProjected, staged.Count);
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Posted {Count} accounting events to the books up to ledger sequence {LedgerSeq}",
                                     staged.Count, last);
            }

            if (failedEvent is not null)
            {
                ReportFailure(failedEvent, failure!);
                return total;
            }

            ClearFailure();
            if (page.Count < batchSize)
                break;
        }

        return total;
    }

    private AccountingEntry BuildEntry(AccountingEventModel accountingEvent, Func<string, AccountingEntry?> find)
    {
        var ledgerSeq = accountingEvent.LedgerSeq
                     ?? throw new InvalidOperationException($"The event {accountingEvent.EventKey} is not sealed");
        var result = _rules(accountingEvent, find);
        var postings = result.Postings;
        accountingEvent.Details.TryGetValue(NoteDetail, out var eventNote);
        var note = string.Join("; ", new[] { eventNote, result.Note }.Where(n => !string.IsNullOrEmpty(n)));
        var entry = new AccountingEntry(ledgerSeq, accountingEvent.EventKey, accountingEvent.Kind,
                                        accountingEvent.OccurredAt, accountingEvent.ChannelId,
                                        accountingEvent.PaymentHash, postings,
                                        note.Length == 0 ? null : note);
        if (!entry.IsBalanced)
            throw new InvalidOperationException(
                $"The postings of {accountingEvent.EventKey} do not balance ({postings.Sum(p => p.AmountMsat)} msat)");

        return entry;
    }

    private void ReportFailure(AccountingEventModel accountingEvent, Exception failure)
    {
        _failureCounter.Add(1, new KeyValuePair<string, object?>("kind", accountingEvent.Kind.ToString()));
        _projectionError = $"{accountingEvent.EventKey} (ledger sequence {accountingEvent.LedgerSeq}): "
                         + failure.Message;

        if (_reportedFailureKey != accountingEvent.EventKey)
        {
            _reportedFailureKey = accountingEvent.EventKey;
            _logger.LogCritical(failure,
                                "The accounting books cannot post the event {EventKey} ({Kind}, ledger sequence "
                              + "{LedgerSeq}): the books stop there and retry every round; they never skip an "
                              + "event", accountingEvent.EventKey, accountingEvent.Kind, accountingEvent.LedgerSeq);
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("The accounting books still cannot post the event {EventKey}", accountingEvent.EventKey);
        }
    }

    private void ClearFailure()
    {
        if (_reportedFailureKey is not null)
            _logger.LogInformation("The accounting books got past the event {EventKey}", _reportedFailureKey);

        _reportedFailureKey = null;
        _projectionError = null;
    }

    /// <summary>The caller holds the round gate.</summary>
    private async Task<AccountingReconcileResult> ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _snapshotSource!.TakeSnapshotAsync(cancellationToken);

        long ledgerSeq;
        IReadOnlyDictionary<AccountRole, long> balances;
        using (var scope = _scopeFactory.CreateScope())
        {
            var books = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingBooksDbRepository;
            ledgerSeq = await books.GetCursorAsync(cancellationToken);
            balances = await books.GetBalancesAsync(cancellationToken);
        }

        var lines = BuildReconcileLines(snapshot, balances, _projectionError);
        var result = new AccountingReconcileResult(snapshot.TakenAt, snapshot.BlockHeight, ledgerSeq, lines);
        foreach (var line in lines)
        {
            _driftGauge.Record(line.DriftMsat, new KeyValuePair<string, object?>("account", line.Account.ToString()));
            if (line.DriftMsat != 0 && _logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Accounting reconcile at ledger sequence {LedgerSeq}, block {BlockHeight}: "
                                 + "{Account} drifts by {DriftMsat} msat (books {BooksMsat}, node {NodeMsat}; "
                                 + "{Note})", ledgerSeq, snapshot.BlockHeight, line.Account, line.DriftMsat,
                                   line.BooksMsat, line.NodeMsat, line.Note);
        }

        _lastReconcile = result;
        return result;
    }

    private async Task RunAsync()
    {
        var token = _stopping.Token;
        var snapshotInterval = _options.SnapshotInterval;
        var nextReconcile = _timeProvider.GetUtcNow() + snapshotInterval;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _roundGate.WaitAsync(token);
                try
                {
                    await ProjectAsync(MaxBatchesPerRound, token);

                    if (_snapshotSource is not null && snapshotInterval > TimeSpan.Zero
                                                    && _timeProvider.GetUtcNow() >= nextReconcile)
                    {
                        nextReconcile = _timeProvider.GetUtcNow() + snapshotInterval;
                        await ReconcileCoreAsync(token);
                    }
                }
                finally
                {
                    _roundGate.Release();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Only the books stop; the next round tries again
                _logger.LogError(e, "The accounting books' round failed (projection or reconcile)");
            }

            try
            {
                await Task.Delay(Interval, _timeProvider, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}