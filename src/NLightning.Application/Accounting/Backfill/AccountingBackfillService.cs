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
using Domain.Channels.Splicing.Enums;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Payments;

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
/// (<see cref="AccountingEventKeys.MemoComplete"/>) ends it for good.</para>
/// <para>Metrics on <c>Meter("NLightning.Accounting")</c>: <c>nlightning.accounting.backfill.opening</c> (opening
/// balances written, tag <c>bucket</c>) and <c>nlightning.accounting.backfill.memo</c> (memo events written, tag
/// <c>source</c>).</para>
/// </remarks>
public sealed class AccountingBackfillService : IAccountingBackfill, IAsyncDisposable, IDisposable
{
    /// <summary>The source rows read per page (and saved per save) of the memo pass.</summary>
    public const int DefaultBatchSize = 500;

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
    /// marker, nothing more once the completion marker exists.
    /// </summary>
    public async Task<AccountingMemoResult> RunMemoBackfillAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset cutoverAt;
        bool openingSkipped;
        using (var scope = _scopeFactory.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingEventDbRepository;
            if (await events.ExistsAsync(AccountingEventKeys.MemoComplete(), cancellationToken))
                return new AccountingMemoResult(true, 0, 0, 0, 0, 0);

            var marker = await events.GetByKeyAsync(AccountingEventKeys.Cutover(), cancellationToken);
            if (marker is null)
                return new AccountingMemoResult(false, 0, 0, 0, 0, 0);

            cutoverAt = AccountingCutoverEvents.ReadCutoverAt(marker) ?? marker.OccurredAt;
            openingSkipped = marker.Details.ContainsKey(AccountingCutoverEvents.SkippedOpeningKey);
        }

        var tally = new MemoTally();
        await MemoInvoicesAsync(cutoverAt, tally, cancellationToken);
        await MemoPaymentsAsync(cutoverAt, tally, cancellationToken);
        await MemoForwardsAsync(cutoverAt, tally, cancellationToken);
        await MemoChannelsAsync(openingSkipped, tally, cancellationToken);

        var result = new AccountingMemoResult(true, tally.Invoices, tally.Payments, tally.Forwards, tally.Channels,
                                              tally.Skipped);
        using (var scope = _scopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            unitOfWork.AccountingEventDbRepository.Add(
                AccountingCutoverEvents.MemoCompleteMarker(_timeProvider.GetUtcNow(), result));
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

                var built = Build("invoice", invoice.PaymentHash.ToString(), () =>
                                      PaymentAccountingEvents.InvoiceSettled(
                                          invoice, invoice.AmountReceived ?? invoice.Amount
                                                ?? throw new InvalidOperationException("No amount received"),
                                          null, null, 1, false, 0));
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
                if (payment.CompletedAt is not { } completedAt || completedAt > cutoverAt)
                    continue;

                AccountingEventModel? built;
                switch (payment.Status)
                {
                    case PaymentStatus.Succeeded:
                        var selfPayment = await IsOurInvoiceAsync(unitOfWork, payment);
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
        for (var skip = 0; ; skip += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var page = await unitOfWork.ForwardCircuitDbRepository.ListAsync(
                           new ForwardCircuitListQuery(skip, BatchSize, Status: ForwardCircuitStatus.Fulfilled),
                           cancellationToken);
            var events = unitOfWork.AccountingEventDbRepository;
            var written = 0;
            foreach (var circuit in page)
            {
                if (circuit is not { Status: ForwardCircuitStatus.Fulfilled, ResolvedAt: { } resolvedAt }
                 || resolvedAt > cutoverAt)
                    continue;

                var built = Build("forward", $"{circuit.IncomingChannelId}:{circuit.IncomingHtlcId}",
                                  () => PaymentAccountingEvents.ForwardSettled(circuit, null, null));
                if (built is null || !await TryAddMemoAsync(events, built, tally, cancellationToken))
                    continue;

                written++;
            }

            await SaveBatchAsync(unitOfWork, "forward", written, tally.Forwards += written);
            if (page.Count < BatchSize)
                return;
        }
    }

    /// <summary>
    /// The funding of every channel the cutover gave an opening balance (or that was closed) and the mutual close of
    /// every closed channel. A channel confirmed after the cutover is left out: its live <c>ChannelFunded</c> is the
    /// fact, and a memo row racing it would make the live one the duplicate.
    /// </summary>
    private async Task MemoChannelsAsync(bool openingSkipped, MemoTally tally, CancellationToken cancellationToken)
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
                var hadOpening = !openingSkipped
                              && (await events.ExistsAsync(
                                      AccountingEventKeys.OpeningBalance(
                                          AccountingCutoverEvents.ChannelBucket(channel.ChannelId)),
                                      cancellationToken)
                               || await events.ExistsAsync(
                                      AccountingEventKeys.OpeningBalance(
                                          AccountingCutoverEvents.PendingBucket(channel.ChannelId)),
                                      cancellationToken));
                if (!closed && !hadOpening)
                    continue;

                if (await IsSplicedAsync(unitOfWork, channel))
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
                if (closed && !hadOpening && channel.ClosingTransaction is { } closingTransaction
                 && await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channel.ChannelId) is null)
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

    private async Task<bool> IsOurInvoiceAsync(IUnitOfWork unitOfWork, PaymentModel payment)
    {
        try
        {
            return await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(payment.PaymentHash) is not null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "Accounting memo backfill: could not check whether payment {PaymentHash} paid one of "
                              + "our invoices", payment.PaymentHash);
            return false;
        }
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
        public int Skipped;
    }
}