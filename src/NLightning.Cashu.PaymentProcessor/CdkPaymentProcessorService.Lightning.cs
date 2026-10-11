using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Cashu.PaymentProcessor;

using Application.Offers.Send;
using Bolt11.Exceptions;
using Bolt11.Models;
using Domain.Cashu.Enums;
using Domain.Cashu.Models;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Grpc;

/// <summary>BOLT 11 and BOLT 12 (NUT-23, NUT-25) of <see cref="CdkPaymentProcessorService"/>.</summary>
public sealed partial class CdkPaymentProcessorService
{
    private async Task<CreatePaymentResponse> CreateBolt11Async(Bolt11IncomingPaymentOptions bolt11,
                                                                CancellationToken cancellationToken)
    {
        var amount = bolt11.Amount is { Value: > 0 } given ? ToMoney(given) : null;
        uint? expirySeconds = null;
        if (bolt11.HasUnixExpiry)
        {
            var seconds = (long)bolt11.UnixExpiry - _timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (seconds < 1)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "The expiry is in the past."));
            expirySeconds = (uint)Math.Min(seconds, uint.MaxValue);
        }

        InvoiceModel invoice;
        try
        {
            invoice = await _invoiceService.CreateInvoiceAsync(amount, bolt11.HasDescription ? bolt11.Description : "",
                                                               expirySeconds, _labels, cancellationToken);
        }
        catch (ArgumentException e)
        {
            // A description too long for BOLT 11, an amount or expiry out of range (NL-999)
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }
        _logger.LogInformation("Cashu mint quote: invoice {PaymentHash} for {Amount}", invoice.PaymentHash,
                               amount is null ? "any amount" : $"{amount.MilliSatoshi} msat");
        return new CreatePaymentResponse
        {
            RequestIdentifier = Identifier(invoice.PaymentHash),
            Request = invoice.Bolt11 ?? "",
            Expiry = (ulong)invoice.CreatedAt.AddSeconds(invoice.ExpirySeconds).ToUnixTimeSeconds()
        };
    }

    /// <summary>A BOLT 12 mint quote: one of our offers, labelled for the mint, named by its offer id.</summary>
    private async Task<CreatePaymentResponse> CreateBolt12Async(Bolt12IncomingPaymentOptions bolt12,
                                                                CancellationToken cancellationToken)
    {
        var amount = bolt12.Amount is { Value: > 0 } given ? ToMoney(given) : null;
        DateTimeOffset? expiry = bolt12.HasUnixExpiry ? DateTimeOffset.FromUnixTimeSeconds((long)bolt12.UnixExpiry) : null;
        var description = bolt12.HasDescription && !string.IsNullOrEmpty(bolt12.Description)
                              ? bolt12.Description
                              : amount is null ? null : "Cashu mint quote";

        CreatedOffer created;
        try
        {
            created = await _offerService!.CreateOfferAsync(new CreateOfferRequest(amount, description,
                                                                                   AbsoluteExpiry: expiry)
            {
                Labels = _labels
            }, cancellationToken);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }
        catch (InvalidOperationException e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }

        if (created.Warning is { } warning)
            _logger.LogWarning("Cashu mint quote offer {OfferId}: {Warning}", created.Offer.OfferId, warning);
        _logger.LogInformation("Cashu mint quote: offer {OfferId} for {Amount}", created.Offer.OfferId,
                               amount is null ? "any amount" : $"{amount.MilliSatoshi} msat");
        var response = new CreatePaymentResponse
        {
            RequestIdentifier = OfferIdentifier(created.Offer.OfferId),
            Request = created.Offer.Bolt12
        };
        if (expiry is { } at)
            response.Expiry = (ulong)at.ToUnixTimeSeconds();
        return response;
    }

    private PaymentQuoteResponse QuoteBolt11(PaymentQuoteRequest request)
    {
        var invoice = Decode(request.Request);
        var amount = AmountToPay(invoice, request.Options);
        CheckPaymentAmount(amount);
        return new PaymentQuoteResponse
        {
            RequestIdentifier = Identifier(HashOf(invoice)),
            Amount = ToAmount(amount, roundUp: true),
            Fee = ToAmount(FeeReserve(amount), roundUp: true),
            State = QuoteState.Unpaid
        };
    }

    /// <summary>A BOLT 12 melt quote: the offer's amount (or the mint's, for an offer without one) and the reserve.</summary>
    private async Task<PaymentQuoteResponse> QuoteBolt12Async(PaymentQuoteRequest request,
                                                              CancellationToken cancellationToken)
    {
        var quoteId = CheckQuoteId(request.QuoteId);
        var amount = OfferAmountToPay(request.Request, request.Options);
        CheckPaymentAmount(amount);
        var reserve = FeeReserve(amount);
        var quote = await WithQuotesAsync(r => r.GetAsync(quoteId));
        if (quote is null)
        {
            quote = new CashuQuoteModel(quoteId, CashuQuoteMethod.Bolt12, CashuQuoteDirection.Outgoing, amount,
                                        _timeProvider.GetUtcNow())
            {
                Request = request.Request.Trim(),
                MaxFee = reserve
            };
            await SaveQuoteAsync(quote, isNew: true);
        }
        else if (quote.Method != CashuQuoteMethod.Bolt12 || quote.Direction != CashuQuoteDirection.Outgoing)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists,
                                              $"Quote {quoteId} is already a {quote.Method} quote."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new PaymentQuoteResponse
        {
            RequestIdentifier = QuoteIdentifier(quoteId),
            Amount = ToAmount(amount, roundUp: true),
            Fee = ToAmount(reserve, roundUp: true),
            State = QuoteState.Unpaid
        };
    }

    private async Task<MakePaymentResponse> PayBolt11Async(Bolt11OutgoingPaymentOptions options, AmountMessage? maxFee,
                                                           CancellationToken cancellationToken)
    {
        var invoice = Decode(options.Bolt11);
        var paymentHash = HashOf(invoice);
        var amount = AmountToPay(invoice, options.MeltOptions);
        CheckPaymentAmount(amount);
        maxFee ??= options.MaxFeeAmount;
        var fee = CapFee(maxFee is null ? null : ToMoney(maxFee), amount);
        var quoteId = string.IsNullOrWhiteSpace(options.QuoteId) ? null : CheckQuoteId(options.QuoteId);

        // The quote is saved before the payment starts (by hash: the mint names BOLT 11 melts by payment hash)
        if (quoteId is not null && _scopeFactory is not null)
        {
            var quote = await WithQuotesAsync(r => r.GetAsync(quoteId));
            var isNew = quote is null;
            quote ??= new CashuQuoteModel(quoteId, CashuQuoteMethod.Bolt11, CashuQuoteDirection.Outgoing, amount,
                                          _timeProvider.GetUtcNow());
            quote.Request = options.Bolt11.Trim();
            quote.PaymentHash = paymentHash;
            quote.MaxFee = fee;
            quote.SetState(CashuQuoteState.Dispatching, _timeProvider.GetUtcNow());
            await SaveQuoteAsync(quote, isNew);
        }

        if (quoteId is not null && !_inFlight.TryAdd(quoteId, 0))
            return Pending(Identifier(paymentHash));

        PaymentModel payment;
        try
        {
            var result = await _paymentService.PayInvoiceAsync(options.Bolt11, invoice.Amount.IsZero ? amount : null,
                                                               new PayInvoiceOptions
                                                               {
                                                                   Timeout = PaymentTimeout,
                                                                   MaxFee = fee,
                                                                   Labels = quoteId is null
                                                                                ? _labels
                                                                                : MeltLabels(quoteId)
                                                               }, cancellationToken);
            payment = result.Payment;
        }
        catch (ArgumentException e)
        {
            await FailQuoteAsync(quoteId, e.Message);
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }
        catch (InvalidOperationException e) when (e.GetType() == typeof(InvalidOperationException))
        {
            // Already in flight or paid: answer with the stored payment when it is the mint's (NL-999)
            var stored = await _paymentService.GetPaymentAsync(paymentHash, cancellationToken);
            if (stored is null || !IsMintPayment(stored))
            {
                var reason = stored is null ? e.Message : "This node already pays or paid this invoice outside the mint.";
                await FailQuoteAsync(quoteId, reason);
                throw new RpcException(new Status(StatusCode.FailedPrecondition, reason));
            }

            payment = stored;
        }
        finally
        {
            if (quoteId is not null)
                _inFlight.TryRemove(quoteId, out _);
        }

        await RecordPaymentAsync(quoteId, payment);
        _logger.LogInformation("Cashu melt {QuoteId}: payment {PaymentHash} is {Status}", quoteId, paymentHash,
                               payment.Status);
        return ToMakePaymentResponse(payment, Identifier(paymentHash));
    }

    /// <summary>
    /// A BOLT 12 melt: the quote is saved <see cref="CashuQuoteState.Dispatching"/>, then the invoice is fetched and
    /// paid; the quote then names the payment's hash.
    /// </summary>
    private async Task<MakePaymentResponse> PayBolt12Async(Bolt12OutgoingPaymentOptions options, AmountMessage? maxFee,
                                                           CancellationToken cancellationToken)
    {
        var quoteId = CheckQuoteId(options.QuoteId);
        var identifier = QuoteIdentifier(quoteId);
        var amount = OfferAmountToPay(options.Offer, options.MeltOptions);
        CheckPaymentAmount(amount);
        maxFee ??= options.MaxFeeAmount;
        var fee = CapFee(maxFee is null ? null : ToMoney(maxFee), amount);

        // Replays keep what is stored: a melt in flight, paid or pending is never sent again
        var quote = await WithQuotesAsync(r => r.GetAsync(quoteId));
        if (quote is { State: CashuQuoteState.Dispatching or CashuQuoteState.Pending or CashuQuoteState.Paid })
            return await LightningResponseAsync(quote, identifier, cancellationToken);
        if (!_inFlight.TryAdd(quoteId, 0))
            return Pending(identifier);

        try
        {
            var isNew = quote is null;
            quote ??= new CashuQuoteModel(quoteId, CashuQuoteMethod.Bolt12, CashuQuoteDirection.Outgoing, amount,
                                          _timeProvider.GetUtcNow());
            quote.Request = options.Offer.Trim();
            quote.Amount = amount;
            quote.MaxFee = fee;
            quote.PaymentHash = null;
            quote.SetState(CashuQuoteState.Dispatching, _timeProvider.GetUtcNow());
            await SaveQuoteAsync(quote, isNew);

            PayOfferResult result;
            try
            {
                var offerHasAmount = options.MeltOptions?.OptionsCase != MeltOptions.OptionsOneofCase.Amountless;
                result = await _offerPaymentService!.PayOfferAsync(
                             new PayOfferRequest(options.Offer.Trim(), offerHasAmount ? null : amount),
                             new PayOfferOptions
                             {
                                 Payment = new PayInvoiceOptions
                                 {
                                     Timeout = PaymentTimeout,
                                     MaxFee = fee,
                                     Labels = MeltLabels(quoteId)
                                 }
                             }, cancellationToken);
            }
            catch (ArgumentException e)
            {
                await FailQuoteAsync(quoteId, e.Message);
                throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
            }
            catch (InvalidOperationException e) when (e.GetType() == typeof(InvalidOperationException))
            {
                // The offer payer refused (already paying or paid the invoice it fetched, no block yet): the quote
                // stays Dispatching, which reads PENDING, so the mint checks it again rather than giving the ecash back
                throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
            }

            if (result.Payment?.Payment is not { } payment)
            {
                var reason = $"No invoice for the offer ({result.Fetch.Status}): {result.Fetch.Error}";
                await FailQuoteAsync(quoteId, reason);
                _logger.LogWarning("Cashu melt {QuoteId}: {Reason}", quoteId, reason);
                return Failed(identifier);
            }

            await RecordPaymentAsync(quoteId, payment);
            _logger.LogInformation("Cashu melt {QuoteId}: offer payment {PaymentHash} is {Status}", quoteId,
                                   payment.PaymentHash, payment.Status);
            return ToMakePaymentResponse(payment, identifier);
        }
        finally
        {
            _inFlight.TryRemove(quoteId, out _);
        }
    }

    /// <summary>Every invoice of our offer that was paid (each a <c>payment_id</c> of its own).</summary>
    private async Task<IEnumerable<WaitIncomingPaymentResponse>> CheckOfferAsync(Hash offerId)
    {
        using var scope = _scopeFactory!.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var invoices = await unitOfWork.InvoiceDbRepository.ListSettledByOfferIdAsync(offerId);
        // Only the mint's own offers' invoices (NL-999)
        return invoices.Where(i => i.Label == _options.Label).Select(Received).ToList();
    }

    /// <summary>Stores the payment's hash and outcome on its melt.</summary>
    private async Task RecordPaymentAsync(string? quoteId, PaymentModel payment)
    {
        if (quoteId is null || _scopeFactory is null)
            return;

        try
        {
            var quote = await WithQuotesAsync(r => r.GetAsync(quoteId));
            if (quote is null)
                return;

            quote.PaymentHash = payment.PaymentHash;
            quote.Fee = payment.Status == PaymentStatus.Succeeded ? payment.Fee : null;
            quote.SetState(LightningState(payment) switch
            {
                QuoteState.Paid => CashuQuoteState.Paid,
                QuoteState.Failed => CashuQuoteState.Failed,
                _ => CashuQuoteState.Pending
            }, _timeProvider.GetUtcNow(), payment.FailureReason);
            await SaveQuoteAsync(quote, isNew: false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The payment itself is stored; the quote keeps naming it by hash (BOLT 11) or answers pending (BOLT 12)
            _logger.LogError(e, "Could not record the payment of Cashu melt {QuoteId}", quoteId);
        }
    }

    /// <summary>Marks a melt failed before anything was sent.</summary>
    private async Task FailQuoteAsync(string? quoteId, string reason)
    {
        if (quoteId is null || _scopeFactory is null)
            return;

        var quote = await WithQuotesAsync(r => r.GetAsync(quoteId));
        if (quote is null)
            return;

        quote.SetState(CashuQuoteState.Failed, _timeProvider.GetUtcNow(), reason);
        await SaveQuoteAsync(quote, isNew: false);
    }

    private TimeSpan PaymentTimeout => TimeSpan.FromSeconds(_options.PaymentTimeoutSeconds);

    private MakePaymentResponse ToMakePaymentResponse(PaymentModel payment, PaymentIdentifier identifier)
    {
        var response = new MakePaymentResponse
        {
            PaymentIdentifier = identifier,
            Status = LightningState(payment),
            TotalSpent = ToAmount(payment.Status == PaymentStatus.Succeeded
                                      ? payment.Amount + payment.Fee
                                      : LightningMoney.Zero, roundUp: true)
        };
        if (payment.Preimage is { } preimage)
            response.PaymentProof = Convert.ToHexString((byte[])preimage).ToLowerInvariant();
        return response;
    }

    /// <summary>
    /// The mint's view of a payment. CDK takes <c>FAILED</c> as final and gives the melt's ecash back, so a payment
    /// row that reads <c>Failed</c> is <c>FAILED</c> only when it is: not between two attempts of a payment the
    /// payment service still retries (<c>PENDING</c>), and not failed for an unknown outcome after a restart, which a
    /// replayed fulfill may still turn into paid (<c>UNKNOWN</c>) (NL-999, NL-1001).
    /// </summary>
    private QuoteState LightningState(PaymentModel payment) => payment.Status switch
    {
        PaymentStatus.Succeeded => QuoteState.Paid,
        PaymentStatus.Failed when payment.IsOutcomeUnknown => QuoteState.Unknown,
        PaymentStatus.Failed when _paymentService.IsPaying(payment.PaymentHash) => QuoteState.Pending,
        PaymentStatus.Failed => QuoteState.Failed,
        _ => QuoteState.Pending
    };

    /// <summary>Whether <paramref name="payment"/> is one the processor made (its label; never a trampoline relay's).</summary>
    private bool IsMintPayment(PaymentModel payment) =>
        payment is { IsTrampolineRelay: false } && payment.Label == _options.Label;

    /// <summary>A settled invoice as the mint's payment: by payment hash (BOLT 11) or by its offer (BOLT 12).</summary>
    private WaitIncomingPaymentResponse Received(InvoiceModel invoice) => new()
    {
        PaymentIdentifier = invoice.Bolt12?.OfferId is { } offerId
                                ? OfferIdentifier(offerId)
                                : Identifier(invoice.PaymentHash),
        PaymentAmount = ToAmount(invoice.AmountReceived ?? invoice.Amount ?? LightningMoney.Zero, roundUp: false),
        PaymentId = invoice.PaymentHash.ToString()
    };

    private Invoice Decode(string bolt11)
    {
        try
        {
            var invoice = Invoice.Decode(bolt11.Trim(), _nodeOptions.BitcoinNetwork);
            if (invoice.PaymentHash is null)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "The invoice has no payment hash."));
            if (invoice.ExpiryDate <= _timeProvider.GetUtcNow())
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                  $"The invoice expired at {invoice.ExpiryDate:O}."));
            return invoice;
        }
        catch (InvoiceSerializationException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              $"The invoice cannot be decoded for {_nodeOptions.BitcoinNetwork}: "
                                            + (e.InnerException?.Message ?? e.Message)));
        }
    }

    private static LightningMoney AmountToPay(Invoice invoice, MeltOptions? meltOptions)
    {
        var requested = RequestedAmount(meltOptions);
        if (!invoice.Amount.IsZero)
        {
            if (requested is not null && requested != invoice.Amount)
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                  "The amount differs from the invoice's."));
            return invoice.Amount;
        }

        return requested is { IsZero: false }
                   ? requested
                   : throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                       "The invoice has no amount; give one (amountless option)."));
    }

    /// <summary>What a BOLT 12 melt pays: the offer's amount in msat, or the mint's for an offer without one.</summary>
    private LightningMoney OfferAmountToPay(string offerText, MeltOptions? meltOptions)
    {
        OfferToPay offer;
        try
        {
            offer = OfferToPay.Parse(offerText.Trim(), _nodeOptions.BitcoinNetwork.ChainHash, _timeProvider.GetUtcNow());
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"The offer is not valid: {e.Message}"));
        }

        var requested = RequestedAmount(meltOptions);
        if (offer.Currency is not null)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              $"Offers in {offer.Currency} are not supported."));
        if (offer.QuantityMax is not null)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              "Offers with a quantity are not supported."));
        if (offer.Amount is { } offerAmount)
        {
            if (requested is not null && requested != LightningMoney.MilliSatoshis(offerAmount))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "The amount differs from the offer's."));
            return LightningMoney.MilliSatoshis(offerAmount);
        }

        return requested is { IsZero: false }
                   ? requested
                   : throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                       "The offer has no amount; give one (amountless option)."));
    }

    private static LightningMoney? RequestedAmount(MeltOptions? meltOptions)
    {
        if (meltOptions?.OptionsCase == MeltOptions.OptionsOneofCase.Mpp)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              "Partial (multi-path) melts are not supported."));

        return meltOptions?.OptionsCase == MeltOptions.OptionsOneofCase.Amountless
                   ? LightningMoney.MilliSatoshis(meltOptions.Amountless.AmountMsat)
                   : null;
    }

    private static Hash HashOf(Invoice invoice) => new(Convert.FromHexString(invoice.PaymentHash!.ToString()));
}