using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Cashu.PaymentProcessor;

using Bolt11.Exceptions;
using Bolt11.Models;
using Domain.Accounting.Labels;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Grpc;

/// <summary>
/// CDK's <c>CdkPaymentProcessor</c> gRPC service over this node (Cashu plan C1, NL-902): a Cashu mint
/// (<c>cdk-mintd</c>, <c>ln_backend = "grpcprocessor"</c>) creates its mint quotes as our BOLT 11 invoices and pays
/// its melts with our payment service.
/// </summary>
/// <remarks>
/// <para>Only BOLT 11 is offered (<see cref="GetSettings"/> leaves bolt12 and onchain unset); other methods answer
/// <see cref="StatusCode.Unimplemented"/>. Every request is identified by its payment hash (hex). Invoices and
/// payments carry the label <see cref="CashuPaymentProcessorOptions.Label"/>, so the books show the mint's flows.</para>
/// <para>Amounts are in the configured unit: received amounts are rounded down to it, spent amounts and fee reserves
/// up. A melt's <c>max_fee_amount</c> is the fee limit of the payment; the payment waits at most
/// <see cref="CashuPaymentProcessorOptions.PaymentTimeoutSeconds"/> and answers <c>PENDING</c> after that (the mint
/// then checks it, or hears of it on <see cref="WaitPaymentEvent"/>).</para>
/// <para><see cref="WaitPaymentEvent"/> streams the settles of invoices with our label and the outcomes of the
/// payments this process made (their quote ids are held in memory: after a restart the mint learns them through
/// <see cref="CheckOutgoingPayment"/>, as the CDK contract expects of a reconnecting mint).</para>
/// </remarks>
public sealed class CdkPaymentProcessorService : CdkPaymentProcessor.CdkPaymentProcessorBase
{
    private const string Bolt11Method = "bolt11";

    private readonly IPaymentEventSource _eventSource;
    private readonly IInvoiceService _invoiceService;
    private readonly ILogger<CdkPaymentProcessorService> _logger;
    private readonly NodeOptions _nodeOptions;
    private readonly CashuPaymentProcessorOptions _options;
    private readonly IPaymentService _paymentService;
    private readonly TimeProvider _timeProvider;
    private readonly SourceLabels _labels;
    private readonly ConcurrentDictionary<Hash, string> _quoteIds = new();

    public CdkPaymentProcessorService(IInvoiceService invoiceService, IPaymentService paymentService,
                                      IPaymentEventSource eventSource, IOptions<NodeOptions> nodeOptions,
                                      IOptions<CashuPaymentProcessorOptions> options,
                                      ILogger<CdkPaymentProcessorService> logger, TimeProvider? timeProvider = null)
    {
        _invoiceService = invoiceService;
        _paymentService = paymentService;
        _eventSource = eventSource;
        _nodeOptions = nodeOptions.Value;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _labels = SourceLabels.Create(_options.Label, null);
    }

    private string Unit => _options.IsMsat ? "msat" : "sat";

    /// <inheritdoc />
    public override Task<SettingsResponse> GetSettings(EmptyRequest request, ServerCallContext context) =>
        Task.FromResult(new SettingsResponse
        {
            Unit = Unit,
            Bolt11 = new Bolt11Settings { Mpp = false, Amountless = true, InvoiceDescription = true }
        });

    /// <inheritdoc />
    public override async Task<CreatePaymentResponse> CreatePayment(CreatePaymentRequest request,
                                                                    ServerCallContext context)
    {
        if (request.Options?.OptionsCase != IncomingPaymentOptions.OptionsOneofCase.Bolt11)
            throw Unimplemented(request.Options?.OptionsCase.ToString() ?? "no method");

        var bolt11 = request.Options.Bolt11;
        var amount = bolt11.Amount is { Value: > 0 } given ? ToMoney(given) : null;
        uint? expirySeconds = null;
        if (bolt11.HasUnixExpiry)
        {
            var seconds = (long)bolt11.UnixExpiry - _timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (seconds < 1)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "The expiry is in the past."));
            expirySeconds = (uint)Math.Min(seconds, uint.MaxValue);
        }

        var invoice = await _invoiceService.CreateInvoiceAsync(amount, bolt11.HasDescription ? bolt11.Description : "",
                                                               expirySeconds, _labels, context.CancellationToken);
        _logger.LogInformation("Cashu mint quote: invoice {PaymentHash} for {Amount}", invoice.PaymentHash,
                               amount is null ? "any amount" : $"{amount.MilliSatoshi} msat");
        return new CreatePaymentResponse
        {
            RequestIdentifier = Identifier(invoice.PaymentHash),
            Request = invoice.Bolt11 ?? "",
            Expiry = (ulong)invoice.CreatedAt.AddSeconds(invoice.ExpirySeconds).ToUnixTimeSeconds()
        };
    }

    /// <inheritdoc />
    public override Task<PaymentQuoteResponse> GetPaymentQuote(PaymentQuoteRequest request, ServerCallContext context)
    {
        if (request.RequestType != OutgoingPaymentRequestType.Bolt11Invoice)
            throw Unimplemented(request.RequestType.ToString());
        CheckUnit(request.Unit);

        var invoice = Decode(request.Request);
        var amount = AmountToPay(invoice, request.Options);
        return Task.FromResult(new PaymentQuoteResponse
        {
            RequestIdentifier = Identifier(invoice),
            Amount = ToAmount(amount, roundUp: true),
            Fee = ToAmount(FeeReserve(amount), roundUp: true),
            State = QuoteState.Unpaid
        });
    }

    /// <inheritdoc />
    public override async Task<MakePaymentResponse> MakePayment(MakePaymentRequest request, ServerCallContext context)
    {
        if (request.PaymentOptions?.OptionsCase != OutgoingPaymentVariant.OptionsOneofCase.Bolt11)
            throw Unimplemented(request.PaymentOptions?.OptionsCase.ToString() ?? "no method");
        if (request.PartialAmount is not null)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              "Partial (multi-path) melts are not supported."));
        if (!string.IsNullOrEmpty(request.Unit))
            CheckUnit(request.Unit);

        var options = request.PaymentOptions.Bolt11;
        var invoice = Decode(options.Bolt11);
        var paymentHash = HashOf(invoice);
        var amount = AmountToPay(invoice, options.MeltOptions);
        var maxFee = request.MaxFeeAmount ?? options.MaxFeeAmount;
        if (!string.IsNullOrEmpty(options.QuoteId))
            _quoteIds[paymentHash] = options.QuoteId;

        var payOptions = new PayInvoiceOptions
        {
            Timeout = TimeSpan.FromSeconds(_options.PaymentTimeoutSeconds),
            MaxFee = maxFee is null ? FeeReserve(amount) : ToMoney(maxFee),
            Labels = _labels
        };

        PaymentModel payment;
        try
        {
            var result = await _paymentService.PayInvoiceAsync(options.Bolt11, invoice.Amount.IsZero ? amount : null,
                                                               payOptions, context.CancellationToken);
            payment = result.Payment;
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }
        catch (InvalidOperationException e) when (e.GetType() == typeof(InvalidOperationException))
        {
            // Already in flight or paid: answer with the stored payment
            payment = await _paymentService.GetPaymentAsync(paymentHash, context.CancellationToken)
                   ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }

        _logger.LogInformation("Cashu melt {QuoteId}: payment {PaymentHash} is {Status}", options.QuoteId,
                               paymentHash, payment.Status);
        return ToMakePaymentResponse(payment);
    }

    /// <inheritdoc />
    public override async Task<CheckIncomingPaymentResponse> CheckIncomingPayment(
        CheckIncomingPaymentRequest request, ServerCallContext context)
    {
        var paymentHash = ParseIdentifier(request.RequestIdentifier);
        var invoice = await _invoiceService.GetInvoiceAsync(paymentHash, context.CancellationToken);
        var response = new CheckIncomingPaymentResponse();
        if (invoice is { Status: InvoiceStatus.Settled })
            response.Payments.Add(Received(invoice));
        return response;
    }

    /// <inheritdoc />
    public override async Task<MakePaymentResponse> CheckOutgoingPayment(CheckOutgoingPaymentRequest request,
                                                                         ServerCallContext context)
    {
        var paymentHash = ParseIdentifier(request.RequestIdentifier);
        var payment = await _paymentService.GetPaymentAsync(paymentHash, context.CancellationToken);
        return payment is null
                   ? new MakePaymentResponse
                   {
                       PaymentIdentifier = Identifier(paymentHash),
                       Status = QuoteState.Unknown,
                       TotalSpent = new AmountMessage { Value = 0, Unit = Unit }
                   }
                   : ToMakePaymentResponse(payment);
    }

    /// <inheritdoc />
    public override async Task WaitPaymentEvent(EmptyRequest request, IServerStreamWriter<PaymentEventResponse> stream,
                                                ServerCallContext context)
    {
        using var subscription = _eventSource.Subscribe();
        _logger.LogInformation("Cashu mint subscribed to payment events");
        try
        {
            await foreach (var paymentEvent in subscription.ReadAllAsync(context.CancellationToken))
            {
                var response = await ToEventResponseAsync(paymentEvent, context.CancellationToken);
                if (response is not null)
                    await stream.WriteAsync(response, context.CancellationToken);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The mint went away
        }

        if (subscription.Overflowed)
            _logger.LogWarning("The Cashu mint read payment events too slowly; some were dropped (the mint's checks "
                             + "recover them)");
    }

    /// <summary>
    /// The stream message for <paramref name="paymentEvent"/>, or null when it is not the mint's.
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
            case PaymentSucceededEvent succeeded when _quoteIds.TryRemove(succeeded.PaymentHash, out var quoteId):
                var payment = await _paymentService.GetPaymentAsync(succeeded.PaymentHash, cancellationToken);
                return payment is null
                           ? null
                           : new PaymentEventResponse
                           {
                               PaymentSuccessful = new PaymentSuccessfulResponse
                               {
                                   QuoteId = quoteId,
                                   Details = ToMakePaymentResponse(payment)
                               }
                           };
            case PaymentFailedEvent failed when _quoteIds.TryRemove(failed.PaymentHash, out var quoteId):
                return new PaymentEventResponse
                {
                    PaymentFailed = new PaymentFailedResponse
                    {
                        QuoteId = quoteId,
                        Reason = failed.Reason ?? "The payment failed."
                    }
                };
            default:
                return null;
        }
    }

    private MakePaymentResponse ToMakePaymentResponse(PaymentModel payment)
    {
        var response = new MakePaymentResponse
        {
            PaymentIdentifier = Identifier(payment.PaymentHash),
            Status = payment.Status switch
            {
                PaymentStatus.Succeeded => QuoteState.Paid,
                PaymentStatus.Failed => QuoteState.Failed,
                _ => QuoteState.Pending
            },
            TotalSpent = ToAmount(payment.Status == PaymentStatus.Succeeded
                                      ? payment.Amount + payment.Fee
                                      : LightningMoney.Zero, roundUp: true)
        };
        if (payment.Preimage is { } preimage)
            response.PaymentProof = Convert.ToHexString((byte[])preimage).ToLowerInvariant();
        return response;
    }

    private WaitIncomingPaymentResponse Received(InvoiceModel invoice) => new()
    {
        PaymentIdentifier = Identifier(invoice.PaymentHash),
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
        if (meltOptions?.OptionsCase == MeltOptions.OptionsOneofCase.Mpp)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              "Partial (multi-path) melts are not supported."));

        var requested = meltOptions?.OptionsCase == MeltOptions.OptionsOneofCase.Amountless
                            ? LightningMoney.MilliSatoshis(meltOptions.Amountless.AmountMsat)
                            : null;
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

    /// <summary>
    /// max(<see cref="CashuPaymentProcessorOptions.MinFeeReserveMsat"/>, amount × FeeReservePpm / 1,000,000).
    /// </summary>
    internal LightningMoney FeeReserve(LightningMoney amount)
    {
        var proportional = (ulong)(amount.MilliSatoshi * (UInt128)_options.FeeReservePpm / 1_000_000);
        return LightningMoney.MilliSatoshis(Math.Max(proportional, _options.MinFeeReserveMsat));
    }

    internal LightningMoney ToMoney(AmountMessage amount)
    {
        if (!string.IsNullOrEmpty(amount.Unit))
            CheckUnit(amount.Unit);
        return _options.IsMsat ? LightningMoney.MilliSatoshis(amount.Value) : LightningMoney.Satoshis(amount.Value);
    }

    internal AmountMessage ToAmount(LightningMoney amount, bool roundUp)
    {
        var msat = amount.MilliSatoshi;
        var value = _options.IsMsat ? msat : roundUp ? (msat + 999) / 1_000 : msat / 1_000;
        return new AmountMessage { Value = value, Unit = Unit };
    }

    private void CheckUnit(string unit)
    {
        if (!string.Equals(unit, Unit, StringComparison.OrdinalIgnoreCase))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              $"This processor's unit is {Unit}, not {unit}."));
    }

    private static Hash HashOf(Invoice invoice) => new(Convert.FromHexString(invoice.PaymentHash!.ToString()));

    private static PaymentIdentifier Identifier(Invoice invoice) => Identifier(HashOf(invoice));

    private static PaymentIdentifier Identifier(Hash paymentHash) => new()
    {
        Type = PaymentIdentifierType.PaymentHash,
        Hash = paymentHash.ToString()
    };

    private static Hash ParseIdentifier(PaymentIdentifier? identifier)
    {
        if (identifier is not { Type: PaymentIdentifierType.PaymentHash, ValueCase: PaymentIdentifier.ValueOneofCase.Hash }
         || identifier.Hash.Length != 64)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              "Only payment-hash identifiers (64 hex characters) are known."));
        try
        {
            return new Hash(Convert.FromHexString(identifier.Hash));
        }
        catch (FormatException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The payment hash is not hex."));
        }
    }

    private static RpcException Unimplemented(string method) =>
        new(new Status(StatusCode.Unimplemented, $"Only {Bolt11Method} is supported, not {method}."));
}