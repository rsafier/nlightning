using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Accounting.Backfill;

using Channels.Accounting;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Payments.Trampoline;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Payments;
using Payments.Trampoline;

/// <summary>
/// The accounting feed's one-shot backfill (NL-602 A1-T6, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §4 "Backfill",
/// §10): a cutover, then memo history.
/// </summary>
/// <remarks>
/// <para><b>Cutover</b> (<see cref="EnsureCutoverAsync"/>, at startup before the peers connect and the chain monitor
/// starts, so nothing changes the database while it reads). Wallet history cannot be rebuilt (a spent UTXO row is
/// deleted), so the feed starts with opening balances read from the database (never from memory), one save: every
/// channel holding a balance (<see cref="AccountingCutoverEvents.HoldsChannelBalance"/>: our gross local balance), every
/// force close being resolved (its counted, unresolved outputs, plus a synthetic <c>ChannelForceClosed</c> of 0 whose
/// counted vouts make the later resolutions leave that pending bucket), the wallet (every UTXO row), then the marker
/// (<see cref="AccountingEventKeys.Cutover"/>). A channel still waiting for its funding confirmation gets nothing: its
/// <c>ChannelFunded</c> moves the contribution later, and the wallet opening still holds the inputs its
/// <c>WalletOutputSpent</c> events remove. When the feed already has events without a marker (a node that ran the feed
/// before the backfill existed) the opening balances would count those facts twice: only the marker is written, with
/// <c>skippedOpening=true</c>, and a warning is logged. Once the marker exists a start costs one indexed key
/// lookup.</para>
/// <para><b>Memo history</b> (<see cref="StartMemoBackfill"/>, in the background after the chain monitor started):
/// the facts before the cutover, written with the live writers' keys, kinds and amounts
/// (<see cref="PaymentAccountingEvents"/>, <see cref="ChannelAccountingEvents"/>), flagged
/// <see cref="AccountingEventFlags.Backfilled"/> with <c>memo=true</c>: P&amp;L statistics the books never apply to a
/// bucket (the opening balances hold their effect). Settled invoices (settled at or before the cutover), succeeded and
/// failed payments (completed at or before it), fulfilled forwards (resolved at or before it), the funding of every
/// channel that held an opening balance or was closed (never one confirmed after the cutover: its live
/// <c>ChannelFunded</c> is the fact) and the mutual close of every closed channel with a closing transaction. Pages of
/// <see cref="BatchSize"/> source rows, one scope and one save per page; a key already in the feed (a live event or an
/// earlier run) is skipped, so a run stopped at any point resumes at the next start, and the completion marker
/// (<see cref="AccountingEventKeys.MemoComplete"/>) ends it for good. A source added after that first pass
/// (<see cref="LaterMemoSources"/>, NL-682: the force closes of NL-624) has its own marker
/// (<see cref="AccountingEventKeys.MemoSourceComplete"/>): a node whose first pass completed before the source existed
/// runs that source alone at its next start, once; a fresh node's first pass covers it and writes both markers.</para>
/// <para>Metrics on <c>Meter("NLightning.Accounting")</c>: <c>nlightning.accounting.backfill.opening</c> (opening
/// balances written, tag <c>bucket</c>) and <c>nlightning.accounting.backfill.memo</c> (memo events written, tag
/// <c>source</c>).</para>
/// </remarks>
public sealed class AccountingBackfillService : IAccountingBackfill, IAsyncDisposable, IDisposable
{
    /// <summary>The source rows read per page (and saved per save) of the memo pass.</summary>
    public const int DefaultBatchSize = 500;

    /// <summary>The memo source of the force closes recorded before the cutover (NL-624), added after the first memo
    /// pass shipped (NL-682).</summary>
    public const string ForceCloseMemoSource = "forceclose";

    /// <summary>
    /// The memo sources added after the first memo pass, each ended by its own
    /// <see cref="AccountingEventKeys.MemoSourceComplete"/> marker (NL-682). Append only: a source listed here runs once
    /// on every node whose <see cref="AccountingEventKeys.MemoComplete"/> predates it.
    /// </summary>
    public static readonly IReadOnlyList<string> LaterMemoSources = [ForceCloseMemoSource];

    private static readonly string[] s_invoiceDetailsUnknown = ["parts", "settledBy"];
    private static readonly string[] s_paymentDetailsUnknown = ["parts"];

    private readonly AccountingFeedGate? _feedGate;
    private readonly Lock _gate = new();
    private readonly ILogger<AccountingBackfillService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeProvider _timeProvider;
    private readonly Counter<long> _openingCounter;
    private readonly Counter<long> _memoCounter;

    private Task? _memoTask;
    private bool _stopped;

    public AccountingBackfillService(IServiceScopeFactory scopeFactory, ILogger<AccountingBackfillService> logger,
                                     TimeProvider? timeProvider = null, int batchSize = DefaultBatchSize,
                                     AccountingFeedGate? feedGate = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        _feedGate = feedGate;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        BatchSize = batchSize;

        Meter = new Meter(AccountingEventSealerService.MeterName);
        _openingCounter = Meter.CreateCounter<long>("nlightning.accounting.backfill.opening", "{event}",
                                                    "Opening balances written by the accounting cutover");
        _memoCounter = Meter.CreateCounter<long>("nlightning.accounting.backfill.memo", "{event}",
                                                 "Memo events written by the accounting backfill");
    }

    /// <summary>The source rows per page of the memo pass.</summary>
    public int BatchSize { get; }

    /// <summary>The background memo pass started by <see cref="StartMemoBackfill"/>, for tests.</summary>
    public Task MemoTask
    {
        get
        {
            lock (_gate)
                return _memoTask ?? Task.CompletedTask;
        }
    }

    /// <summary>The meter, for tests.</summary>
    internal Meter Meter { get; }

    /// <inheritdoc />
    /// <remarks>A failure holds the feed's gate (NL-619, <see cref="AccountingFeedGate"/>): the live writers' events are
    /// dropped until a later start's cutover succeeds, which then opens the feed with the balances of that moment.
    /// Success (or a cutover done before) releases it.</remarks>
    public async Task<AccountingCutoverResult> EnsureCutoverAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await EnsureCutoverCoreAsync(cancellationToken);
            _feedGate?.Release();
            return result;
        }
        catch (Exception e)
        {
            _feedGate?.Hold($"the accounting cutover failed: {e.Message}");
            throw;
        }
    }

    private async Task<AccountingCutoverResult> EnsureCutoverCoreAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var events = unitOfWork.AccountingEventDbRepository;
        if (await events.ExistsAsync(AccountingEventKeys.Cutover(), cancellationToken))
            return AccountingCutoverResult.AlreadyDone;

        var cutoverAt = _timeProvider.GetUtcNow();
        var height = (await unitOfWork.BlockchainStateDbRepository.GetStateAsync())?.LastProcessedHeight ?? 0;

        if (await HasEventsAsync(events, cancellationToken))
        {
            events.Add(AccountingCutoverEvents.Marker(cutoverAt, height,
                                                      (AccountingCutoverEvents.SkippedOpeningKey, "true")));
            await unitOfWork.SaveChangesAsync();
            _logger.LogWarning("The accounting feed already has events but no cutover: no opening balance was written "
                             + "(they would count the recorded facts twice); the feed's balances start from its first "
                             + "event, not from the node's holdings");
            return new AccountingCutoverResult(AccountingCutoverOutcome.SkippedOpening, cutoverAt, height);
        }

        int channelCount = 0, pendingCount = 0, awaitingFunding = 0, closesWithoutRow = 0;
        long channelMsat = 0, pendingMsat = 0;
        var channels = (await unitOfWork.ChannelDbRepository.GetAllAsync())
                      .OrderBy(c => c.ChannelId.ToString(), StringComparer.Ordinal);
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (channel.State)
            {
                case ChannelState.Closed or ChannelState.Stale:
                    continue;
                case ChannelState.OnchainResolving:
                    var close = await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channel.ChannelId);
                    if (close is null)
                    {
                        closesWithoutRow++;
                        _logger.LogWarning("Accounting cutover: channel {ChannelId} resolves on chain without a "
                                         + "recorded close; it gets no opening balance", channel.ChannelId);
                        continue;
                    }

                    var closeKey = AccountingEventKeys.ChannelForceClosed(channel.ChannelId,
                                                                          close.CommitmentTransactionId);
                    if (await events.ExistsAsync(closeKey, cancellationToken))
                        continue;

                    var outputs = await unitOfWork.OnchainResolutionDbRepository
                                                  .GetOutputsByChannelIdAsync(channel.ChannelId);
                    var resolving = AccountingCutoverEvents.ResolvingChannel(channel, close, outputs, cutoverAt,
                                                                             height);
                    events.Add(resolving.Opening);
                    events.Add(resolving.Close);
                    pendingCount++;
                    pendingMsat = checked(pendingMsat + resolving.PendingMsat);
                    continue;
                default:
                    if (!AccountingCutoverEvents.HoldsChannelBalance(channel))
                    {
                        awaitingFunding++;
                        continue;
                    }

                    var opening = AccountingCutoverEvents.ChannelOpening(channel, cutoverAt, height);
                    events.Add(opening);
                    channelCount++;
                    channelMsat = checked(channelMsat + opening.AmountMsat);
                    continue;
            }
        }

        var utxos = (await unitOfWork.UtxoDbRepository.GetUnspentAsync()).ToList();
        long walletMsat = 0;
        if (utxos.Count > 0)
        {
            var wallet = AccountingCutoverEvents.WalletOpening(utxos, cutoverAt, height);
            events.Add(wallet);
            walletMsat = wallet.AmountMsat;
        }

        events.Add(AccountingCutoverEvents.Marker(
                       cutoverAt, height,
                       ("channels", Text(channelCount)), ("channelMsat", Text(channelMsat)),
                       ("pendingChannels", Text(pendingCount)), ("pendingMsat", Text(pendingMsat)),
                       ("walletUtxos", Text(utxos.Count)), ("walletMsat", Text(walletMsat)),
                       ("awaitingFunding", Text(awaitingFunding)),
                       ("closesWithoutRow", closesWithoutRow > 0 ? Text(closesWithoutRow) : null)));
        await unitOfWork.SaveChangesAsync();

        RecordOpening("channel", channelCount);
        RecordOpening("pending", pendingCount);
        RecordOpening("wallet", utxos.Count > 0 ? 1 : 0);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Accounting cutover at height {Height}: {Channels} channel(s) {ChannelMsat} msat, "
                                 + "{Pending} on-chain resolution(s) {PendingMsat} msat, wallet {WalletMsat} msat in "
                                 + "{Utxos} output(s); {Awaiting} channel(s) awaiting funding", height, channelCount,
                                   channelMsat, pendingCount, pendingMsat, walletMsat, utxos.Count, awaitingFunding);

        return new AccountingCutoverResult(AccountingCutoverOutcome.Written, cutoverAt, height, channelCount,
                                           channelMsat, pendingCount, pendingMsat, utxos.Count, walletMsat,
                                           awaitingFunding);
    }

    /// <inheritdoc />
    public void StartMemoBackfill()
    {
        lock (_gate)
        {
            if (_memoTask is not null || _stopped)
                return;

            // NL-619: without a cutover the memo pass would mark its completion with nothing written
            if (_feedGate?.IsHeld == true)
            {
                _logger.LogWarning("The accounting memo backfill does not run: {Reason}", _feedGate.Reason);
                return;
            }

            var token = _stopping.Token;
            _memoTask = Task.Run(() => RunMemoLoopAsync(token), CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        Task? memo;
        lock (_gate)
        {
            if (_stopped)
                return;

            _stopped = true;
            memo = _memoTask;
        }

        await _stopping.CancelAsync();
        if (memo is not null)
            await memo;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose();
        Meter.Dispose();
    }

    /// <summary>Cancels the memo pass without waiting for it (a container disposed synchronously); prefer
    /// <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        lock (_gate)
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
    /// Runs the memo pass now (what <see cref="StartMemoBackfill"/> runs in the background): nothing without the cutover
    /// marker, nothing more once the completion marker and the marker of every <see cref="LaterMemoSources"/> exist
    /// (one indexed key lookup each). With the completion marker written by an earlier version, only the later sources
    /// without their marker run (NL-682).
    /// </summary>
    public async Task<AccountingMemoResult> RunMemoBackfillAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset cutoverAt;
        uint? cutoverHeight;
        bool openingSkipped;
        bool firstPassDone;
        var pendingSources = new List<string>();
        using (var scope = _scopeFactory.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingEventDbRepository;
            firstPassDone = await events.ExistsAsync(AccountingEventKeys.MemoComplete(), cancellationToken);
            foreach (var source in LaterMemoSources)
                if (!await events.ExistsAsync(AccountingEventKeys.MemoSourceComplete(source), cancellationToken))
                    pendingSources.Add(source);
            if (firstPassDone && pendingSources.Count == 0)
                return new AccountingMemoResult(true, 0, 0, 0, 0, 0);

            var marker = await events.GetByKeyAsync(AccountingEventKeys.Cutover(), cancellationToken);
            if (marker is null)
                return new AccountingMemoResult(false, 0, 0, 0, 0, 0);

            cutoverAt = AccountingCutoverEvents.ReadCutoverAt(marker) ?? marker.OccurredAt;
            cutoverHeight = marker.BlockHeight;
            openingSkipped = marker.Details.ContainsKey(AccountingCutoverEvents.SkippedOpeningKey);
        }

        var tally = new MemoTally();
        var forceCloses = !firstPassDone || pendingSources.Contains(ForceCloseMemoSource);
        if (firstPassDone)
        {
            // A node whose first pass predates these sources (NL-682): only they run
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Accounting memo backfill: running the memo source(s) added since this node's "
                                     + "memo pass completed: {Sources}", string.Join(", ", pendingSources));
        }
        else
        {
            await MemoInvoicesAsync(cutoverAt, tally, cancellationToken);
            await MemoPaymentsAsync(cutoverAt, tally, cancellationToken);
            await MemoForwardsAsync(cutoverAt, tally, cancellationToken);
            await MemoTrampolineRelaysAsync(cutoverAt, tally, cancellationToken);
        }

        await MemoChannelsAsync(openingSkipped, cutoverAt, cutoverHeight, !firstPassDone, forceCloses, tally,
                                cancellationToken);

        var result = new AccountingMemoResult(true, tally.Invoices, tally.Payments, tally.Forwards, tally.Channels,
                                              tally.Skipped);
        using (var scope = _scopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var now = _timeProvider.GetUtcNow();
            if (!firstPassDone)
                unitOfWork.AccountingEventDbRepository.Add(AccountingCutoverEvents.MemoCompleteMarker(now, result));
            // The first pass runs every later source too: their markers are written with it
            foreach (var source in pendingSources)
                unitOfWork.AccountingEventDbRepository.Add(
                    AccountingCutoverEvents.MemoSourceCompleteMarker(
                        now, source, source == ForceCloseMemoSource ? tally.ForceCloses : 0));
            await unitOfWork.SaveChangesAsync();
        }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Accounting memo backfill complete: {Invoices} invoice(s), {Payments} payment(s), "
                                 + "{Forwards} forward(s), {Channels} channel event(s) written; {Skipped} already in "
                                 + "the feed", result.Invoices, result.Payments, result.Forwards, result.Channels,
                                   result.Skipped);
        return result;
    }

    private async Task RunMemoLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunMemoBackfillAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Accounting memo backfill stopped; it resumes at the next start");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "The accounting memo backfill failed; it resumes at the next start");
        }
    }

    private async Task MemoInvoicesAsync(DateTimeOffset cutoverAt, MemoTally tally, CancellationToken cancellationToken)
    {
        var filtered = true;
        for (var skip = 0; ; skip += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            IReadOnlyList<InvoiceModel> page;
            if (filtered)
            {
                try
                {
                    page = await unitOfWork.InvoiceDbRepository.ListSettledAsync(cutoverAt, skip, BatchSize);
                }
                catch (NotSupportedException)
                {
                    // A repository that cannot filter (test doubles): every invoice, newest first
                    filtered = false;
                    page = await unitOfWork.InvoiceDbRepository.ListAsync(skip, BatchSize);
                }
            }
            else
            {
                page = await unitOfWork.InvoiceDbRepository.ListAsync(skip, BatchSize);
            }

            var events = unitOfWork.AccountingEventDbRepository;
            var written = 0;
            foreach (var invoice in page)
            {
                if (invoice is not { Status: InvoiceStatus.Settled, SettledAt: { } settledAt } || settledAt > cutoverAt)
                    continue;

                // NL-609: an invoice we paid ourselves (a rebalance) is flagged as the live writer flags it
                var paidByUs = await IsPaidByUsAsync(unitOfWork, invoice, OurNodeId(scope));
                var built = Build("invoice", invoice.PaymentHash.ToString(), () =>
                                      PaymentAccountingEvents.InvoiceSettled(
                                          invoice, invoice.AmountReceived ?? invoice.Amount
                                                ?? throw new InvalidOperationException("No amount received"),
                                          null, null, 1, false, 0, paidByUs));
                if (built is null || !await TryAddMemoAsync(events, built, tally, cancellationToken,
                                                            s_invoiceDetailsUnknown))
                    continue;

                written++;
            }

            await SaveBatchAsync(unitOfWork, "invoice", written, tally.Invoices += written);
            if (page.Count < BatchSize)
                return;
        }
    }

    private async Task MemoPaymentsAsync(DateTimeOffset cutoverAt, MemoTally tally, CancellationToken cancellationToken)
    {
        // Newest first: a payment added (or a failed one replaced by a retry) moves to the top, so a page boundary only
        // ever re-reads rows, which the key check skips
        for (var skip = 0; ; skip += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var page = await unitOfWork.PaymentDbRepository.ListAsync(skip, BatchSize);
            var events = unitOfWork.AccountingEventDbRepository;
            var written = 0;
            foreach (var payment in page)
            {
                // NL-875: a trampoline relay's outgoing payment is booked with its relay (MemoTrampolineRelaysAsync)
                if (payment.CompletedAt is not { } completedAt || completedAt > cutoverAt || payment.IsTrampolineRelay)
                    continue;

                AccountingEventModel? built;
                switch (payment.Status)
                {
                    case PaymentStatus.Succeeded:
                        var selfPayment = await IsOurInvoiceAsync(unitOfWork, payment, OurNodeId(scope));
                        built = Build("payment", payment.PaymentHash.ToString(), () =>
                                          PaymentAccountingEvents.PaymentSucceeded(payment, 1, selfPayment, null));
                        break;
                    case PaymentStatus.Failed:
                        built = Build("payment", payment.PaymentHash.ToString(),
                                      () => PaymentAccountingEvents.PaymentFailed(payment));
                        break;
                    default:
                        continue;
                }

                if (built is null || !await TryAddMemoAsync(events, built, tally, cancellationToken,
                                                            s_paymentDetailsUnknown))
                    continue;

                written++;
            }

            await SaveBatchAsync(unitOfWork, "payment", written, tally.Payments += written);
            if (page.Count < BatchSize)
                return;
        }
    }

    private async Task MemoForwardsAsync(DateTimeOffset cutoverAt, MemoTally tally, CancellationToken cancellationToken)
    {
        // Circuits are never deleted; one fulfilled after a page was read only moves rows into the set above the
        // boundary (re-read, skipped by the key check)
        foreach (var status in new[] { ForwardCircuitStatus.Fulfilled, ForwardCircuitStatus.Failed })
            for (var skip = 0; ; skip += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var scope = _scopeFactory.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var page = await unitOfWork.ForwardCircuitDbRepository.ListAsync(
                               new ForwardCircuitListQuery(skip, BatchSize, Status: status),
                               cancellationToken);
                var events = unitOfWork.AccountingEventDbRepository;
                var written = 0;
                foreach (var circuit in page)
                {
                    if (circuit.Status != status || circuit.ResolvedAt is not { } resolvedAt || resolvedAt > cutoverAt)
                        continue;
                    if (status == ForwardCircuitStatus.Failed &&
                        (circuit.IncomingClaimedPreimage is not { } claimed ||
                         new Hash(System.Security.Cryptography.SHA256.HashData((byte[])claimed)) != circuit.PaymentHash))
                        continue;

                    var built = Build("forward", $"{circuit.IncomingChannelId}:{circuit.IncomingHtlcId}",
                                      () => status == ForwardCircuitStatus.Fulfilled
                                          ? PaymentAccountingEvents.ForwardSettled(circuit, null, null)
                                          : PaymentAccountingEvents.InterceptedHtlcSettled(circuit.IncomingChannelId,
                                              circuit.IncomingHtlcId, circuit.PaymentHash, circuit.ActualIncomingAmount, null,
                                              circuit.OutgoingShortChannelId, null, circuit.OutgoingAmount, resolvedAt, 0));
                    if (built is null || !await TryAddMemoAsync(events, built, tally, cancellationToken))
                        continue;

                    written++;
                }

                await SaveBatchAsync(unitOfWork, "forward", written, tally.Forwards += written);
                if (page.Count < BatchSize)
                    break;
            }
    }

    /// <summary>
    /// NL-875: the trampoline relays fulfilled before the cutover, as memo <c>TrampolineRelaySettled</c> events (counted
    /// with the forwards). Their outgoing payments are left out of <see cref="MemoPaymentsAsync"/>. A unit of work that
    /// stores no relays has none.
    /// </summary>
    private async Task MemoTrampolineRelaysAsync(DateTimeOffset cutoverAt, MemoTally tally,
                                                 CancellationToken cancellationToken)
    {
        for (var skip = 0; ; skip += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (TrampolineRelayReads.TryGetRepository(unitOfWork) is not { } relays)
                return;

            var page = await relays.ListAsync(
                           new TrampolineRelayListQuery(skip, BatchSize, Status: TrampolineRelayStatus.Fulfilled),
                           cancellationToken);
            var events = unitOfWork.AccountingEventDbRepository;
            var written = 0;
            foreach (var relay in page)
            {
                if (relay is not { Status: TrampolineRelayStatus.Fulfilled, CompletedAt: { } completedAt }
                 || completedAt > cutoverAt)
                    continue;

                var parts = await relays.GetPartsAsync(relay.PaymentHash);
                var payment = await unitOfWork.PaymentDbRepository.GetByPaymentHashAsync(relay.PaymentHash);
                var built = Build("trampoline relay", relay.PaymentHash.ToString(), () =>
                                      PaymentAccountingEvents.TrampolineRelaySettled(
                                          relay, parts,
                                          payment is { IsTrampolineRelay: true, Status: PaymentStatus.Succeeded }
                                              ? payment
                                              : null, null));
                if (built is null || !await TryAddMemoAsync(events, built, tally, cancellationToken))
                    continue;

                written++;
            }

            await SaveBatchAsync(unitOfWork, "trampoline", written, tally.Forwards += written);
            if (page.Count < BatchSize)
                return;
        }
    }

    /// <summary>
    /// The funding of every channel the cutover gave an opening balance (or that was closed) and the mutual close of
    /// every closed channel. A channel confirmed after the cutover is left out: its live <c>ChannelFunded</c> is the
    /// fact, and a memo row racing it would make the live one the duplicate. A force close recorded before the cutover
    /// (NL-624, <see cref="ForceCloseMemoEvents"/>): for a channel closed at the cutover its
    /// <c>ChannelForceClosed</c>, the resolution of every final output and its anchor CPFP fees; for a channel still
    /// resolving at the cutover (its close already has the cutover's synthetic event) only what was resolved or
    /// confirmed at or below the cutover's block; a close recorded after the cutover is a live fact and left out.
    /// <paramref name="fundingsAndMutualCloses"/> false (a node whose first pass predates the force-close source,
    /// NL-682) writes only the force closes; <paramref name="forceCloses"/> false leaves them out.
    /// </summary>
    private async Task MemoChannelsAsync(bool openingSkipped, DateTimeOffset cutoverAt, uint? cutoverHeight,
                                         bool fundingsAndMutualCloses, bool forceCloses, MemoTally tally,
                                         CancellationToken cancellationToken)
    {
        List<ChannelModel> channels;
        using (var scope = _scopeFactory.CreateScope())
            channels = (await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ChannelDbRepository.GetAllAsync())
                      .OrderBy(c => c.ChannelId.ToString(), StringComparer.Ordinal)
                      .ToList();

        foreach (var batch in channels.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var events = unitOfWork.AccountingEventDbRepository;
            var written = 0;
            foreach (var channel in batch)
            {
                var closed = channel.State is ChannelState.Closed;
                var resolvingAtCutover = !openingSkipped
                                      && await events.ExistsAsync(
                                             AccountingEventKeys.OpeningBalance(
                                                 AccountingCutoverEvents.PendingBucket(channel.ChannelId)),
                                             cancellationToken);
                var hadOpening = !openingSkipped
                              && (resolvingAtCutover
                               || await events.ExistsAsync(
                                      AccountingEventKeys.OpeningBalance(
                                          AccountingCutoverEvents.ChannelBucket(channel.ChannelId)),
                                      cancellationToken));
                if (!closed && !hadOpening)
                    continue;

                if (!fundingsAndMutualCloses)
                {
                    // Only the force closes (NL-682)
                }
                else if (await IsSplicedAsync(unitOfWork, channel))
                {
                    // The funding outpoint is a splice's: the original funding's key and fee are not known any more
                    _logger.LogDebug("Accounting memo backfill: channel {ChannelId} was spliced; its funding is not "
                                   + "recorded", channel.ChannelId);
                }
                else if (IsSet(channel))
                {
                    IReadOnlyList<AccountingEventModel> funded;
                    try
                    {
                        funded = await ChannelAccountingEvents.BuildChannelFundedAsync(unitOfWork, channel,
                                                                                       _timeProvider.GetUtcNow());
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        _logger.LogWarning(e, "Accounting memo backfill: the funding of channel {ChannelId} could not "
                                            + "be read", channel.ChannelId);
                        funded = [];
                    }

                    foreach (var accountingEvent in funded)
                        if (await TryAddMemoAsync(events, accountingEvent, tally, cancellationToken))
                            written++;
                }

                // Closed at the cutover: a channel that held an opening balance closed after it (a live fact)
                var close = closed && !hadOpening || resolvingAtCutover
                                ? await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channel.ChannelId)
                                : null;
                if (close is not null)
                {
                    // A close recorded after the cutover is a live fact (its events are the live writers')
                    if (forceCloses && (resolvingAtCutover || close.CreatedAt <= cutoverAt))
                    {
                        var forceCloseEvents = await MemoForceCloseAsync(unitOfWork, events, channel, close,
                                                                         resolvingAtCutover ? cutoverHeight ?? 0 : null,
                                                                         tally, cancellationToken);
                        written += forceCloseEvents;
                        tally.ForceCloses += forceCloseEvents;
                    }
                }
                else if (fundingsAndMutualCloses && closed && !hadOpening
                      && channel.ClosingTransaction is { } closingTransaction)
                {
                    var height = (await unitOfWork.WatchedTransactionDbRepository
                                                  .GetByTransactionIdAsync(closingTransaction.TxId))
                                ?.FirstSeenAtHeight;
                    var built = Build("channel", channel.ChannelId.ToString(), () =>
                                          ChannelAccountingEvents.BuildMutualClose(
                                              channel, height, _timeProvider.GetUtcNow(), _logger));
                    if (built is not null && await TryAddMemoAsync(events, built, tally, cancellationToken))
                        written++;
                }
            }

            await SaveBatchAsync(unitOfWork, "channel", written, tally.Channels += written);
        }
    }

    /// <summary>
    /// Stages the memo history of a force close recorded before the cutover (NL-624): the close itself unless
    /// <paramref name="resolvedAtOrBelow"/> is set (a close resolving at the cutover, whose close event is the cutover's
    /// synthetic one), then the resolutions of its final outputs and its anchor CPFP fees, only those at or below
    /// <paramref name="resolvedAtOrBelow"/> when it is set (0: the cutover's height is unknown, nothing). Returns how
    /// many were staged.
    /// </summary>
    private async Task<int> MemoForceCloseAsync(IUnitOfWork unitOfWork, IAccountingEventDbRepository events,
                                                ChannelModel channel, ChannelCloseModel close, uint? resolvedAtOrBelow,
                                                MemoTally tally, CancellationToken cancellationToken)
    {
        if (resolvedAtOrBelow == 0)
            return 0;

        IReadOnlyList<OutputResolutionModel> rows;
        IReadOnlyList<BroadcastTransactionModel> broadcasts;
        try
        {
            rows = await unitOfWork.OnchainResolutionDbRepository.GetOutputsByChannelIdAsync(channel.ChannelId);
            broadcasts = await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(channel.ChannelId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Accounting memo backfill: the force close of channel {ChannelId} could not be read",
                               channel.ChannelId);
            return 0;
        }

        var built = new List<AccountingEventModel>();
        if (resolvedAtOrBelow is null)
        {
            var openedAtHeight = await OriginalFundingHeightAsync(unitOfWork, channel);
            if (Build("channel", channel.ChannelId.ToString(),
                      () => ForceCloseMemoEvents.ForceClosed(channel, close, rows, broadcasts, openedAtHeight))
                is { } closeEvent)
                built.Add(closeEvent);
        }

        try
        {
            built.AddRange(ForceCloseMemoEvents.Resolutions(channel, close, rows, broadcasts, resolvedAtOrBelow));
            built.AddRange(ForceCloseMemoEvents.AnchorCpfpFees(channel.ChannelId, broadcasts, resolvedAtOrBelow));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Accounting memo backfill: the on-chain resolution of channel {ChannelId} could not "
                                + "be recorded", channel.ChannelId);
        }

        var written = 0;
        foreach (var accountingEvent in built)
            if (await TryAddMemoAsync(events, accountingEvent, tally, cancellationToken))
                written++;

        return written;
    }

    private async Task<bool> TryAddMemoAsync(IAccountingEventDbRepository events, AccountingEventModel built,
                                             MemoTally tally, CancellationToken cancellationToken,
                                             params string[] removeDetails)
    {
        if (await events.ExistsAsync(built.EventKey, cancellationToken))
        {
            tally.Skipped++;
            return false;
        }

        events.Add(AccountingCutoverEvents.AsMemo(built, removeDetails));
        return true;
    }

    private AccountingEventModel? Build(string source, string id, Func<AccountingEventModel?> build)
    {
        try
        {
            return build();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Accounting memo backfill: the {Source} {Id} could not be recorded", source, id);
            return null;
        }
    }

    private async Task SaveBatchAsync(IUnitOfWork unitOfWork, string source, int written, int total)
    {
        if (written == 0)
            return;

        await unitOfWork.SaveChangesAsync();
        _memoCounter.Add(written, new KeyValuePair<string, object?>("source", source));
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Accounting memo backfill: {Written} {Source} event(s) saved ({Total} so far)", written,
                             source, total);
    }

    /// <summary>Our node id, or null when the scope has no key manager (then nothing is a self-payment).</summary>
    private static CompactPubKey? OurNodeId(IServiceScope scope) =>
        scope.ServiceProvider.GetService<ISecureKeyManager>()?.GetNodePubKey();

    /// <summary>Whether one of our own payments of <paramref name="invoice"/> succeeded (a rebalance, NL-609), by the
    /// live writers' <see cref="SelfPaymentRule"/> (NL-670).</summary>
    private async Task<bool> IsPaidByUsAsync(IUnitOfWork unitOfWork, InvoiceModel invoice, CompactPubKey? ourNodeId)
    {
        if (ourNodeId is null || invoice is not { Bolt11: not null, Keysend: null, Bolt12: null })
            return false;

        try
        {
            var payment = await unitOfWork.PaymentDbRepository.GetByPaymentHashAsync(invoice.PaymentHash);
            return payment is { Status: PaymentStatus.Succeeded }
                && SelfPaymentRule.IsSelfPayment(payment, invoice, ourNodeId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "Accounting memo backfill: could not check whether invoice {PaymentHash} was paid by us",
                             invoice.PaymentHash);
            return false;
        }
    }

    private async Task<bool> IsOurInvoiceAsync(IUnitOfWork unitOfWork, PaymentModel payment, CompactPubKey? ourNodeId)
    {
        if (ourNodeId is null || payment.PayeeNodeId != ourNodeId.Value)
            return false;

        try
        {
            return SelfPaymentRule.IsSelfPayment(
                payment, await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(payment.PaymentHash), ourNodeId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "Accounting memo backfill: could not check whether payment {PaymentHash} paid one of "
                              + "our invoices", payment.PaymentHash);
            return false;
        }
    }

    /// <summary>
    /// The block of the channel's original funding (NL-682): the initial funding row's short channel id (kept after a
    /// splice replaced it), else the channel's own when its funding is not a splice's; null when unknown.
    /// </summary>
    private static async Task<uint?> OriginalFundingHeightAsync(IUnitOfWork unitOfWork, ChannelModel channel)
    {
        IReadOnlyList<ChannelFunding> fundings;
        try
        {
            fundings = await unitOfWork.ChannelFundingDbRepository.GetByChannelIdAsync(channel.ChannelId);
        }
        catch (NotSupportedException)
        {
            fundings = [];
        }

        if (fundings.FirstOrDefault(f => f.Kind == ChannelFundingKind.Initial
                                      && f.Status != ChannelFundingStatus.Discarded
                                      && (f.ConfirmedHeight is > 0 || f.ShortChannelId is { BlockHeight: > 0 }))
            is { } initial)
            return initial.ShortChannelId is { BlockHeight: > 0 } scid ? scid.BlockHeight : initial.ConfirmedHeight;

        if (await IsSplicedAsync(unitOfWork, channel) || !IsSet(channel))
            return null;

        return channel.ShortChannelId.BlockHeight > 0 ? channel.ShortChannelId.BlockHeight : null;
    }

    /// <summary>Whether the channel's funding output is a splice's (its <c>ChannelFunded</c> key would name the splice
    /// transaction, not the open).</summary>
    private static async Task<bool> IsSplicedAsync(IUnitOfWork unitOfWork, ChannelModel channel)
    {
        if (channel.FundingOutput?.TransactionId is not { } fundingTxId)
            return false;

        try
        {
            var fundings = await unitOfWork.ChannelFundingDbRepository.GetByChannelIdAsync(channel.ChannelId);
            return fundings.Any(f => f.FundingTxId == fundingTxId && f.Kind != ChannelFundingKind.Initial);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<bool> HasEventsAsync(IAccountingEventDbRepository events,
                                                   CancellationToken cancellationToken) =>
        (await events.GetChainTipAsync(cancellationToken)).LedgerSeq > 0
     || (await events.GetUnsealedAsync(1, cancellationToken)).Count > 0;

    private void RecordOpening(string bucket, int count)
    {
        if (count > 0)
            _openingCounter.Add(count, new KeyValuePair<string, object?>("bucket", bucket));
    }

    private static bool IsSet(ChannelModel channel) => ((byte[]?)channel.ShortChannelId)?.Length > 0;

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class MemoTally
    {
        public int Invoices;
        public int Payments;
        public int Forwards;
        public int Channels;
        public int ForceCloses;
        public int Skipped;
    }
}