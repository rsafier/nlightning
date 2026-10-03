using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Cashu.PaymentProcessor;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;
using Domain.Cashu.Enums;
using Domain.Cashu.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Persistence.Interfaces;
using Grpc;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// On-chain mint and melt quotes (NUT-30, NL-997) of <see cref="CdkPaymentProcessorService"/>, from the node's own
/// wallet, as CDK's <c>cdk-bdk</c> backend serves them.
/// </summary>
/// <remarks>
/// <para>A mint quote gets a fresh wallet address (reserved, never handed out again); every output paying it is a
/// deposit (<c>CashuDeposits</c>) reported to the mint once it has
/// <see cref="CashuPaymentProcessorOptions.OnchainConfirmations"/>, with <c>payment_id</c> = <c>txid:vout</c>.</para>
/// <para>A melt quote offers one fee option per <see cref="CashuPaymentProcessorOptions.OnchainFeeTargets"/>; the melt
/// is a wallet withdrawal at that target's fee rate that never pays more than the mint's fee limit, answered
/// <c>PENDING</c> (spent 0) and reported <c>PAID</c> with <c>txid:vout</c> once it has its confirmations.</para>
/// </remarks>
public sealed partial class CdkPaymentProcessorService
{
    /// <summary>An on-chain mint quote: a fresh wallet address bound to the mint's quote id (the same one on a replay).</summary>
    private async Task<CreatePaymentResponse> CreateOnchainAsync(OnchainIncomingPaymentOptions onchain,
                                                                 CancellationToken cancellationToken)
    {
        var quoteId = CheckQuoteId(onchain.QuoteId);
        var quote = await WithQuotesAsync(r => r.GetAsync(quoteId));
        if (quote is null)
        {
            using var scope = _scopeFactory!.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IBitcoinWalletService>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // It saves the scope's unit of work itself, so the quote is staged after it
            var address = await wallet.GetUnusedAddressAsync(
                              _options.OnchainUsesTaproot ? AddressType.P2Tr : AddressType.P2Wpkh, false);
            quote = new CashuQuoteModel(quoteId, CashuQuoteMethod.Onchain, CashuQuoteDirection.Incoming,
                                        LightningMoney.Zero, _timeProvider.GetUtcNow())
            {
                Address = address.Address
            };
            unitOfWork.CashuQuoteDbRepository.Add(quote);
            await unitOfWork.SaveChangesAsync();
            _logger.LogInformation("Cashu mint quote {QuoteId}: on-chain address {Address}", quoteId, quote.Address);
        }
        else if (quote is not { Method: CashuQuoteMethod.Onchain, Direction: CashuQuoteDirection.Incoming })
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists,
                                              $"Quote {quoteId} is already a {quote.Method} quote."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new CreatePaymentResponse
        {
            RequestIdentifier = QuoteIdentifier(quoteId),
            Request = quote.Address!
        };
    }

    /// <summary>The deposits to a mint quote's address that have their confirmations.</summary>
    private async Task<IEnumerable<WaitIncomingPaymentResponse>> CheckDepositsAsync(string quoteId)
    {
        var tip = _blockchainMonitor!.LastProcessedBlockHeight;
        var deposits = await WithQuotesAsync(r => r.GetDepositsAsync(quoteId));
        return deposits.Where(d => d.ConfirmationsAt(tip) >= _options.OnchainConfirmations)
                       .Select(d => DepositReceived(d))
                       .ToList();
    }

    private WaitIncomingPaymentResponse DepositReceived(CashuDepositModel deposit) => new()
    {
        PaymentIdentifier = QuoteIdentifier(deposit.QuoteId),
        PaymentAmount = ToAmount(deposit.Amount, roundUp: false),
        PaymentId = deposit.Outpoint
    };

    /// <summary>An on-chain melt quote: one fee option per configured target, each with its fee reserve.</summary>
    private async Task<PaymentQuoteResponse> QuoteOnchainAsync(PaymentQuoteRequest request,
                                                               CancellationToken cancellationToken)
    {
        var onchain = request.OnchainOptions
                   ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "The on-chain options are missing."));
        var quoteId = CheckQuoteId(string.IsNullOrEmpty(onchain.QuoteId) ? request.QuoteId : onchain.QuoteId);
        var address = string.IsNullOrWhiteSpace(onchain.Address) ? request.Request.Trim() : onchain.Address.Trim();
        var amount = OnchainAmount(onchain.Amount);
        CheckPaymentAmount(amount);

        var options = new List<OnchainFeeOption>();
        var targets = _options.GetOnchainFeeTargets()!;
        for (var index = 0; index < targets.Count; index++)
        {
            var estimate = await EstimateAsync(address, amount, targets[index], cancellationToken);
            var reserveSat = (estimate.Fee.Satoshi * _options.OnchainFeeReservePercent + 99) / 100;
            options.Add(new OnchainFeeOption
            {
                FeeIndex = (uint)index,
                FeeReserve = ToAmount(LightningMoney.Satoshis(reserveSat), roundUp: true).Value,
                EstimatedBlocks = targets[index]
            });
        }

        var quote = await WithQuotesAsync(r => r.GetAsync(quoteId));
        if (quote is null)
            await SaveQuoteAsync(new CashuQuoteModel(quoteId, CashuQuoteMethod.Onchain, CashuQuoteDirection.Outgoing,
                                                     amount, _timeProvider.GetUtcNow())
            { Address = address },
                                 isNew: true);
        else if (quote is not { Method: CashuQuoteMethod.Onchain, Direction: CashuQuoteDirection.Outgoing })
            throw new RpcException(new Status(StatusCode.AlreadyExists,
                                              $"Quote {quoteId} is already a {quote.Method} quote."));

        var cheapest = options.MinBy(o => o.FeeReserve)!;
        var response = new PaymentQuoteResponse
        {
            RequestIdentifier = QuoteIdentifier(quoteId),
            Amount = ToAmount(amount, roundUp: true),
            Fee = new AmountMessage { Value = cheapest.FeeReserve, Unit = Unit },
            State = QuoteState.Unpaid,
            EstimatedBlocks = cheapest.EstimatedBlocks
        };
        response.FeeOptions.AddRange(options);
        return response;
    }

    /// <summary>
    /// An on-chain melt: a withdrawal at the chosen option's fee rate, never above the mint's fee limit, saved
    /// <see cref="CashuQuoteState.Dispatching"/> first; answered <c>PENDING</c> until it has its confirmations. A
    /// refused melt answers <c>FAILED</c> (nothing was sent), as <c>cdk-bdk</c> does.
    /// </summary>
    private async Task<MakePaymentResponse> PayOnchainAsync(OnchainOutgoingPaymentOptions onchain,
                                                            AmountMessage? maxFee, CancellationToken cancellationToken)
    {
        var quoteId = CheckQuoteId(onchain.QuoteId);
        var identifier = QuoteIdentifier(quoteId);

        // Replays keep what is stored: a melt sent, or interrupted while sending, is never sent again
        var quote = await WithQuotesAsync(r => r.GetAsync(quoteId));
        if (quote is { State: CashuQuoteState.Dispatching or CashuQuoteState.Pending or CashuQuoteState.Paid })
            return OnchainResponse(quote);
        if (!_inFlight.TryAdd(quoteId, 0))
            return Pending(identifier);

        try
        {
            var address = onchain.Address.Trim();
            LightningMoney amount;
            uint target;
            try
            {
                amount = OnchainAmount(onchain.Amount);
                CheckPaymentAmount(amount);
                var targets = _options.GetOnchainFeeTargets()!;
                var index = onchain.HasFeeIndex ? onchain.FeeIndex : 0;
                if (index >= targets.Count)
                    return await FailOnchainAsync(quote, quoteId, address, null, $"Unknown fee index {index}.");
                target = targets[(int)index];
            }
            catch (RpcException e)
            {
                return await FailOnchainAsync(quote, quoteId, address, null, e.Status.Detail);
            }

            // Never above MaxOnchainFeeSat, whatever limit the mint passes (NL-1004)
            var cap = LightningMoney.Satoshis(_options.MaxOnchainFeeSat);
            var limit = (maxFee ?? onchain.MaxFeeAmount) is { } given && ToMoney(given) is var requested
                        && requested < cap
                            ? requested
                            : cap;
            var isNew = quote is null;
            quote ??= new CashuQuoteModel(quoteId, CashuQuoteMethod.Onchain, CashuQuoteDirection.Outgoing, amount,
                                          _timeProvider.GetUtcNow());
            quote.Address = address;
            quote.Amount = amount;
            quote.MaxFee = limit;
            quote.FeeIndex = onchain.HasFeeIndex ? onchain.FeeIndex : 0;
            quote.TxId = null;
            quote.OutputIndex = null;
            quote.SetState(CashuQuoteState.Dispatching, _timeProvider.GetUtcNow());
            await SaveQuoteAsync(quote, isNew);

            WalletWithdrawResult result;
            try
            {
                var feeRate = await _feeService!.GetFeeRatePerKwAsync(target, cancellationToken);
                result = await _walletSpendService!.WithdrawAsync(new WalletWithdrawRequest(address, amount, feeRate)
                {
                    Labels = MeltLabels(quoteId),
                    MaxFee = limit
                }, cancellationToken);
            }
            catch (Exception e) when (e is WalletSpendException or InsufficientFundsException
                                          or AnchorReserveException)
            {
                return await FailOnchainAsync(quote, quoteId, address, amount, e.Message);
            }

            quote.TxId = result.TxId;
            quote.OutputIndex = result.DestinationOutputIndex;
            quote.Fee = result.Fee;
            quote.SetState(CashuQuoteState.Pending, _timeProvider.GetUtcNow());
            await SaveQuoteAsync(quote, isNew: false);
            _logger.LogInformation("Cashu melt {QuoteId}: {Amount} sat to {Address} in {TxId} (fee {Fee} sat)",
                                   quoteId, amount.Satoshi, address, result.TxId, result.Fee.Satoshi);
            return OnchainResponse(quote);
        }
        finally
        {
            _inFlight.TryRemove(quoteId, out _);
        }
    }

    /// <summary>Stores the melt as failed (nothing was sent) and answers <c>FAILED</c>.</summary>
    private async Task<MakePaymentResponse> FailOnchainAsync(CashuQuoteModel? quote, string quoteId, string address,
                                                             LightningMoney? amount, string reason)
    {
        var isNew = quote is null;
        quote ??= new CashuQuoteModel(quoteId, CashuQuoteMethod.Onchain, CashuQuoteDirection.Outgoing,
                                      amount ?? LightningMoney.Zero, _timeProvider.GetUtcNow())
        { Address = address };
        quote.SetState(CashuQuoteState.Failed, _timeProvider.GetUtcNow(), reason);
        await SaveQuoteAsync(quote, isNew);
        _logger.LogWarning("Cashu melt {QuoteId} refused before sending: {Reason}", quoteId, reason);
        return Failed(QuoteIdentifier(quoteId));
    }

    /// <summary>An on-chain melt's answer from its quote (proof <c>txid:vout</c> once paid).</summary>
    private MakePaymentResponse OnchainResponse(CashuQuoteModel quote)
    {
        var identifier = QuoteIdentifier(quote.QuoteId);
        return quote.State switch
        {
            CashuQuoteState.Paid => new MakePaymentResponse
            {
                PaymentIdentifier = identifier,
                Status = QuoteState.Paid,
                PaymentProof = quote.Outpoint,
                TotalSpent = ToAmount(quote.Amount + (quote.Fee ?? LightningMoney.Zero), roundUp: true)
            },
            CashuQuoteState.Failed => Failed(identifier),
            CashuQuoteState.Created => Unpaid(identifier),
            // Sent and waiting for its confirmations, or interrupted while sending (never answered unpaid): the spent
            // amount stays 0 until it is final, as cdk-bdk answers
            _ => Pending(identifier)
        };
    }

    private async Task<WalletWithdrawEstimate> EstimateAsync(string address, LightningMoney amount, uint target,
                                                             CancellationToken cancellationToken)
    {
        try
        {
            var feeRate = await _feeService!.GetFeeRatePerKwAsync(target, cancellationToken);
            return await _walletSpendService!.EstimateWithdrawFeeAsync(
                       new WalletWithdrawRequest(address, amount, feeRate), cancellationToken);
        }
        catch (WalletSpendException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }
        catch (Exception e) when (e is InsufficientFundsException or AnchorReserveException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }
    }

    /// <summary>An on-chain amount: whole satoshis, at least the configured minimum.</summary>
    private LightningMoney OnchainAmount(AmountMessage? given)
    {
        var amount = given is null ? LightningMoney.Zero : ToMoney(given);
        if (amount.MilliSatoshi % 1_000 != 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              "An on-chain amount is a whole number of satoshis."));
        if ((ulong)amount.Satoshi < _options.OnchainMinSendSat)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              $"{amount.Satoshi} sat is below the on-chain minimum of "
                                            + $"{_options.OnchainMinSendSat} sat."));
        return amount;
    }

    /// <summary>
    /// The state of an on-chain melt's transaction at <paramref name="tip"/>: paid once confirmed deep enough, failed
    /// when given up (its inputs are back in the wallet), pending otherwise.
    /// </summary>
    private CashuQuoteState? OnchainOutcome(BroadcastState? state, uint? confirmedHeight, uint tip) => state switch
    {
        BroadcastState.Confirmed when confirmedHeight is { } height && tip >= height
                                   && tip - height + 1 >= _options.OnchainConfirmations => CashuQuoteState.Paid,
        BroadcastState.Abandoned => CashuQuoteState.Failed,
        _ => null
    };
}