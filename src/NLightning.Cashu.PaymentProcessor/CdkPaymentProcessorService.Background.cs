using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Cashu.PaymentProcessor;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Cashu.Enums;
using Domain.Cashu.Models;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Grpc;

/// <summary>
/// The background loops of <see cref="CdkPaymentProcessorService"/> (NL-997), started by
/// <see cref="CashuPaymentProcessorHost"/>: the node's payment events mapped to the mint's quotes, and the chain's
/// deposits and confirmations for on-chain quotes. Both publish into <see cref="ProcessorEventHub"/>.
/// </summary>
public sealed partial class CdkPaymentProcessorService
{
    private readonly Channel<ChainWork> _chainWork = Channel.CreateUnbounded<ChainWork>(
        new UnboundedChannelOptions { SingleReader = true });

    private CancellationTokenSource? _background;
    private Task? _chainLoop;
    private Task? _paymentLoop;

    /// <summary>
    /// Starts the background loops: payment events always, the chain loop when on-chain is served (it first records
    /// deposits found in the wallet while the processor was down, then follows the chain monitor).
    /// </summary>
    public Task StartBackgroundAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_background is not null)
            return Task.CompletedTask;

        _background = new CancellationTokenSource();
        var subscription = _eventSource.Subscribe();
        _paymentLoop = Task.Run(() => PumpPaymentEventsAsync(subscription, _background.Token), CancellationToken.None);

        if (OnchainAvailable)
        {
            _blockchainMonitor!.OnWalletMovementDetected += OnWalletMovement;
            _blockchainMonitor.OnNewBlockDetected += OnNewBlock;
            _chainWork.Writer.TryWrite(ChainWork.CatchUp);
            _chainLoop = Task.Run(() => RunChainLoopAsync(_background.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <summary>Stops the background loops.</summary>
    public async Task StopBackgroundAsync()
    {
        if (_background is null)
            return;

        if (_blockchainMonitor is not null)
        {
            _blockchainMonitor.OnWalletMovementDetected -= OnWalletMovement;
            _blockchainMonitor.OnNewBlockDetected -= OnNewBlock;
        }

        await _background.CancelAsync();
        foreach (var loop in new[] { _paymentLoop, _chainLoop })
        {
            if (loop is null)
                continue;
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // Stopped
            }
        }

        _background.Dispose();
        _background = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopBackgroundAsync();

    private void OnWalletMovement(object? sender, WalletMovementEventArgs movement) =>
        _chainWork.Writer.TryWrite(new ChainWork(movement, null));

    private void OnNewBlock(object? sender, NewBlockEventArgs block) =>
        _chainWork.Writer.TryWrite(ChainWork.Block);

    private async Task PumpPaymentEventsAsync(IPaymentEventSubscription subscription,
                                              CancellationToken cancellationToken)
    {
        using (subscription)
        {
            await foreach (var paymentEvent in subscription.ReadAllAsync(cancellationToken))
            {
                try
                {
                    if (await ToEventResponseAsync(paymentEvent, cancellationToken) is { } response)
                        _events.Publish(response);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogError(e, "Could not map payment event {Event} for the Cashu mint", paymentEvent);
                }
            }

            if (subscription.Overflowed)
                _logger.LogWarning("The Cashu processor read payment events too slowly; some were dropped (the mint's "
                                 + "checks recover them)");
        }
    }

    /// <summary>
    /// The stream message for <paramref name="paymentEvent"/>, or null when it is not the mint's: a settled invoice
    /// with our label (by payment hash, or by offer for a BOLT 12 invoice), or the outcome of a melt (its quote is
    /// found by payment hash and updated).
    /// </summary>
    internal async Task<PaymentEventResponse?> ToEventResponseAsync(PaymentEvent paymentEvent,
                                                                   CancellationToken cancellationToken)
    {
        switch (paymentEvent)
        {
            case InvoiceSettledEvent settled:
                var invoice = await _invoiceService.GetInvoiceAsync(settled.PaymentHash, cancellationToken);
                if (invoice is not { Status: InvoiceStatus.Settled } || invoice.Label != _options.Label)
                    return null;
                return new PaymentEventResponse { PaymentReceived = Received(invoice) };
            case PaymentSucceededEvent or PaymentFailedEvent when _scopeFactory is not null:
                var quote = await WithQuotesAsync(r => r.GetOutgoingByPaymentHashAsync(paymentEvent.PaymentHash));
                if (quote is null)
                    return null;

                var payment = await _paymentService.GetPaymentAsync(paymentEvent.PaymentHash, cancellationToken);
                if (payment is null)
                    return null;

                await RecordPaymentAsync(quote.QuoteId, payment);
                var identifier = quote.Method == CashuQuoteMethod.Bolt11
                                     ? Identifier(paymentEvent.PaymentHash)
                                     : QuoteIdentifier(quote.QuoteId);
                return paymentEvent is PaymentFailedEvent failed
                           ? new PaymentEventResponse
                           {
                               PaymentFailed = new PaymentFailedResponse
                               {
                                   QuoteId = quote.QuoteId,
                                   Reason = failed.Reason ?? "The payment failed."
                               }
                           }
                           : new PaymentEventResponse
                           {
                               PaymentSuccessful = new PaymentSuccessfulResponse
                               {
                                   QuoteId = quote.QuoteId,
                                   Details = ToMakePaymentResponse(payment, identifier)
                               }
                           };
            default:
                return null;
        }
    }

    private async Task RunChainLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var work in _chainWork.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                if (work.Movement is { } movement)
                    await RecordDepositsAsync([(movement.WalletAddress, movement.TxId, movement.OutputIndex,
                                                   movement.Amount, movement.BlockHeight)]);
                else if (work.IsCatchUp)
                    await CatchUpDepositsAsync();

                // The monitor raises a block's deposits after the block itself, so the tip is checked after each
                await ProcessTipAsync(cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "The Cashu processor's chain work failed; it runs again at the next block");
            }
        }
    }

    /// <summary>Records the wallet's unspent outputs paying mint quote addresses (deposits seen while it was down).</summary>
    private async Task CatchUpDepositsAsync()
    {
        using var scope = _scopeFactory!.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var utxos = await unitOfWork.UtxoDbRepository.GetUnspentAsync(includeWalletAddress: true);
        await RecordDepositsAsync(utxos.Where(u => u.WalletAddress is not null)
                                       .Select(u => (u.WalletAddress!.Address, u.TxId, u.Index, u.Amount,
                                                     u.BlockHeight))
                                       .ToList());
    }

    /// <summary>
    /// Stores the outputs that pay a mint quote's address and are at least the minimum; a known one moves to the
    /// block it is in now (a reorg).
    /// </summary>
    private async Task RecordDepositsAsync(
        IReadOnlyList<(string Address, TxId TxId, uint Index, LightningMoney Amount, uint Height)> outputs)
    {
        if (outputs.Count == 0)
            return;

        using var scope = _scopeFactory!.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var repository = unitOfWork.CashuQuoteDbRepository;
        var quotes = (await repository.GetIncomingByAddressesAsync(outputs.Select(o => o.Address).Distinct().ToList()))
                    .Where(q => q.Method == CashuQuoteMethod.Onchain)
                    .ToDictionary(q => q.Address!, StringComparer.Ordinal);
        var changed = false;
        foreach (var output in outputs)
        {
            if (!quotes.TryGetValue(output.Address, out var quote) || output.Height == 0)
                continue;

            if ((ulong)output.Amount.Satoshi < _options.OnchainMinReceiveSat)
            {
                _logger.LogWarning("Deposit {TxId}:{Index} of {Amount} sat to Cashu quote {QuoteId} is below the "
                                 + "minimum of {Minimum} sat; it stays in the wallet", output.TxId, output.Index,
                                   output.Amount.Satoshi, quote.QuoteId, _options.OnchainMinReceiveSat);
                continue;
            }

            var known = await repository.GetDepositAsync(output.TxId, output.Index);
            if (known is null)
            {
                repository.AddDeposit(new CashuDepositModel(quote.QuoteId, output.TxId, output.Index, output.Amount,
                                                            output.Height));
                _logger.LogInformation("Cashu mint quote {QuoteId}: deposit {TxId}:{Index} of {Amount} sat at block "
                                     + "{Height}", quote.QuoteId, output.TxId, output.Index, output.Amount.Satoshi,
                                       output.Height);
                changed = true;
            }
            else if (known.BlockHeight != output.Height && known.ReportedAt is null)
            {
                repository.UpdateDeposit(known with { BlockHeight = output.Height });
                changed = true;
            }
        }

        if (changed)
            await unitOfWork.SaveChangesAsync();
    }

    /// <summary>
    /// At the chain tip: reports the deposits that reached their confirmations (still in their block, checked against
    /// bitcoind when it can tell), and settles or fails the on-chain melts whose transaction is final.
    /// </summary>
    private async Task ProcessTipAsync(CancellationToken cancellationToken)
    {
        var tip = _blockchainMonitor!.LastProcessedBlockHeight;
        using var scope = _scopeFactory!.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var repository = unitOfWork.CashuQuoteDbRepository;
        var reported = new List<CashuDepositModel>();
        foreach (var deposit in await repository.ListUnreportedDepositsAsync())
        {
            if (deposit.ConfirmationsAt(tip) < _options.OnchainConfirmations
             || !await IsStillInItsBlockAsync(deposit, cancellationToken))
                continue;

            deposit.ReportedAt = _timeProvider.GetUtcNow();
            repository.UpdateDeposit(deposit);
            reported.Add(deposit);
        }

        var settled = new List<CashuQuoteModel>();
        foreach (var melt in await repository.ListOutgoingAsync(CashuQuoteMethod.Onchain, CashuQuoteState.Pending))
        {
            if (melt.TxId is not { } txId)
                continue;

            var broadcast = await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txId);
            if (OnchainOutcome(broadcast?.State, broadcast?.ConfirmedHeight, tip) is not { } outcome)
                continue;

            melt.SetState(outcome, _timeProvider.GetUtcNow(),
                          outcome == CashuQuoteState.Failed ? "The transaction was given up." : null);
            repository.Update(melt);
            settled.Add(melt);
        }

        if (reported.Count == 0 && settled.Count == 0)
            return;

        await unitOfWork.SaveChangesAsync();
        foreach (var deposit in reported)
            _events.Publish(new PaymentEventResponse { PaymentReceived = DepositReceived(deposit) });
        foreach (var melt in settled)
        {
            _logger.LogInformation("Cashu melt {QuoteId}: {Outpoint} is {State}", melt.QuoteId, melt.Outpoint,
                                   melt.State);
            _events.Publish(melt.State == CashuQuoteState.Paid
                                ? new PaymentEventResponse
                                {
                                    PaymentSuccessful = new PaymentSuccessfulResponse
                                    {
                                        QuoteId = melt.QuoteId,
                                        Details = OnchainResponse(melt)
                                    }
                                }
                                : new PaymentEventResponse
                                {
                                    PaymentFailed = new PaymentFailedResponse
                                    {
                                        QuoteId = melt.QuoteId,
                                        Reason = melt.FailureReason ?? "The transaction was given up."
                                    }
                                });
        }
    }

    /// <summary>
    /// Whether the deposit's block on the active chain still holds its transaction (a reorg moves or drops it; the
    /// chain monitor then reports it again in its new block). True when bitcoind cannot tell.
    /// </summary>
    private async Task<bool> IsStillInItsBlockAsync(CashuDepositModel deposit, CancellationToken cancellationToken)
    {
        if (_chainService is null)
            return true;

        try
        {
            var block = await _chainService.GetBlockTxIdsAsync(deposit.BlockHeight);
            if (block is null)
                return true;

            var txId = new uint256((byte[])deposit.TxId);
            if (block.Value.TxIds.Contains(txId))
                return true;

            _logger.LogWarning("Deposit {Outpoint} to Cashu quote {QuoteId} is no longer in block {Height}; waiting "
                             + "for it to confirm again", deposit.Outpoint, deposit.QuoteId, deposit.BlockHeight);
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug(e, "Could not check deposit {Outpoint} against bitcoind", deposit.Outpoint);
            return true;
        }
    }

    /// <summary>One unit of chain work: a wallet deposit, a new block, or the catch-up at start.</summary>
    private sealed record ChainWork(WalletMovementEventArgs? Movement, bool? CatchUpFlag)
    {
        public static readonly ChainWork Block = new(null, null);
        public static readonly ChainWork CatchUp = new(null, true);

        public bool IsCatchUp => CatchUpFlag == true;
    }
}