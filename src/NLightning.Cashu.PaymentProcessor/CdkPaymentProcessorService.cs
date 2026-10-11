using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Cashu.PaymentProcessor;

using Domain.Accounting.Labels;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Cashu.Enums;
using Domain.Cashu.Interfaces;
using Domain.Cashu.Models;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Offers.Interfaces;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Grpc;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// CDK's <c>CdkPaymentProcessor</c> gRPC service over this node (Cashu plan C1, NL-992; breadth NL-997): a Cashu mint
/// (<c>cdk-mintd</c>, <c>backend = "grpcprocessor"</c>) creates its mint quotes as our BOLT 11 invoices, BOLT 12
/// offers or on-chain wallet addresses, and pays its melts with our payment service, offer payer or wallet.
/// </summary>
/// <remarks>
/// <para>The identifiers follow CDK's own backends: BOLT 11 by payment hash (<c>cdk-ldk-node</c>), BOLT 12 mint quotes by
/// offer id with one payment per paid invoice (<c>payment_id</c> = its payment hash), BOLT 12 melts and everything
/// on-chain by the mint's quote id (<c>cdk-ldk-node</c>, <c>cdk-bdk</c>; NUT-25, NUT-30). Methods the node cannot serve
/// are left out of <see cref="GetSettings"/> and answer <see cref="StatusCode.Unimplemented"/>.</para>
/// <para>Every melt and on-chain mint quote is a <c>CashuQuotes</c> row (<see cref="ICashuQuoteDbRepository"/>): a
/// melt is saved <see cref="CashuQuoteState.Dispatching"/> before its payment starts, so after a restart of the node or
/// of the mint the quote still names its payment, and a melt is never sent twice for one quote.</para>
/// <para>Invoices, offers, payments and withdrawals carry the label <see cref="CashuPaymentProcessorOptions.Label"/>
/// (melts also the tag <c>cdk_quote</c>), so the books show the mint's flows. Amounts are in the configured unit:
/// received amounts are rounded down to it, spent amounts and fee reserves up.</para>
/// <para><see cref="WaitPaymentEvent"/> streams <see cref="ProcessorEventHub"/>, which the background loops
/// (<see cref="StartBackgroundAsync"/>) fill: settled invoices with our label, the outcomes of the mint's melts, and
/// on-chain deposits and melts once they have <see cref="CashuPaymentProcessorOptions.OnchainConfirmations"/>.</para>
/// </remarks>
public sealed partial class CdkPaymentProcessorService : CdkPaymentProcessor.CdkPaymentProcessorBase, IAsyncDisposable
{
    internal const string QuoteTagKey = "cdk_quote";

    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IBitcoinChainService? _chainService;
    private readonly IPaymentEventSource _eventSource;
    private readonly ProcessorEventHub _events = new();
    private readonly IFeeService? _feeService;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly IInvoiceService _invoiceService;
    private readonly SourceLabels _labels;
    private readonly ILogger<CdkPaymentProcessorService> _logger;
    private readonly NodeOptions _nodeOptions;
    private readonly IOfferPaymentService? _offerPaymentService;
    private readonly IOfferService? _offerService;
    private readonly CashuPaymentProcessorOptions _options;
    private readonly IPaymentService _paymentService;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IWalletSpendService? _walletSpendService;
    private readonly SemaphoreSlim _melts;

    public CdkPaymentProcessorService(IInvoiceService invoiceService, IPaymentService paymentService,
                                      IPaymentEventSource eventSource, IOptions<NodeOptions> nodeOptions,
                                      IOptions<CashuPaymentProcessorOptions> options,
                                      ILogger<CdkPaymentProcessorService> logger, TimeProvider? timeProvider = null,
                                      IServiceScopeFactory? scopeFactory = null, IOfferService? offerService = null,
                                      IOfferPaymentService? offerPaymentService = null,
                                      IWalletSpendService? walletSpendService = null, IFeeService? feeService = null,
                                      IBlockchainMonitor? blockchainMonitor = null,
                                      IBitcoinChainService? chainService = null)
    {
        _invoiceService = invoiceService;
        _paymentService = paymentService;
        _eventSource = eventSource;
        _nodeOptions = nodeOptions.Value;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _scopeFactory = scopeFactory;
        _offerService = offerService;
        _offerPaymentService = offerPaymentService;
        _walletSpendService = walletSpendService;
        _feeService = feeService;
        _blockchainMonitor = blockchainMonitor;
        _chainService = chainService;
        _labels = SourceLabels.Create(_options.Label, null);
        _melts = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentMelts));
    }

    /// <summary>How many <see cref="WaitPaymentEvent"/> streams are open.</summary>
    internal int StreamCount => _events.SubscriberCount;

    private string Unit => _options.IsMsat ? "msat" : "sat";

    /// <summary>Whether BOLT 12 is served: enabled, offers work both ways, and quotes can be stored.</summary>
    internal bool Bolt12Available => _options.Bolt12Enabled && _scopeFactory is not null
                                  && _offerService is { IsAvailable: true }
                                  && _offerPaymentService is { IsAvailable: true };

    /// <summary>Whether on-chain is served: enabled and the wallet, fee and chain services are there.</summary>
    internal bool OnchainAvailable => _options.OnchainEnabled && _scopeFactory is not null
                                   && _walletSpendService is not null && _feeService is not null
                                   && _blockchainMonitor is not null;

    /// <inheritdoc />
    public override Task<SettingsResponse> GetSettings(EmptyRequest request, ServerCallContext context)
    {
        var settings = new SettingsResponse
        {
            Unit = Unit,
            Bolt11 = new Bolt11Settings { Mpp = false, Amountless = true, InvoiceDescription = true }
        };
        if (Bolt12Available)
            settings.Bolt12 = new Bolt12Settings { Amountless = true, InvoiceDescription = true };
        if (OnchainAvailable)
            settings.Onchain = new OnchainSettings
            {
                Confirmations = _options.OnchainConfirmations,
                MinReceiveAmountSat = _options.OnchainMinReceiveSat,
                MinSendAmountSat = _options.OnchainMinSendSat
            };
        return Task.FromResult(settings);
    }

    /// <inheritdoc />
    public override Task<CreatePaymentResponse> CreatePayment(CreatePaymentRequest request, ServerCallContext context) =>
        request.Options?.OptionsCase switch
        {
            IncomingPaymentOptions.OptionsOneofCase.Bolt11 => CreateBolt11Async(request.Options.Bolt11,
                                                                                context.CancellationToken),
            IncomingPaymentOptions.OptionsOneofCase.Bolt12 when Bolt12Available =>
                CreateBolt12Async(request.Options.Bolt12, context.CancellationToken),
            IncomingPaymentOptions.OptionsOneofCase.Onchain when OnchainAvailable =>
                CreateOnchainAsync(request.Options.Onchain, context.CancellationToken),
            _ => throw Unimplemented(request.Options?.OptionsCase.ToString() ?? "no method")
        };

    /// <inheritdoc />
    public override Task<PaymentQuoteResponse> GetPaymentQuote(PaymentQuoteRequest request, ServerCallContext context)
    {
        CheckUnit(request.Unit);
        return request.RequestType switch
        {
            OutgoingPaymentRequestType.Bolt11Invoice => Task.FromResult(QuoteBolt11(request)),
            OutgoingPaymentRequestType.Bolt12Offer when Bolt12Available =>
                QuoteBolt12Async(request, context.CancellationToken),
            OutgoingPaymentRequestType.Onchain when OnchainAvailable =>
                QuoteOnchainAsync(request, context.CancellationToken),
            _ => throw Unimplemented(request.RequestType.ToString())
        };
    }

    /// <inheritdoc />
    public override async Task<MakePaymentResponse> MakePayment(MakePaymentRequest request, ServerCallContext context)
    {
        // A bounded number of melts at once (NL-1000); the mint retries a refused one
        if (!_melts.Wait(0))
            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                                              $"At most {_options.MaxConcurrentMelts} melts are paid at once."));
        try
        {
            return await MakePaymentCoreAsync(request, context);
        }
        finally
        {
            _melts.Release();
        }
    }

    private Task<MakePaymentResponse> MakePaymentCoreAsync(MakePaymentRequest request, ServerCallContext context)
    {
        if (request.PartialAmount is not null)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              "Partial (multi-path) melts are not supported."));
        if (!string.IsNullOrEmpty(request.Unit))
            CheckUnit(request.Unit);

        return request.PaymentOptions?.OptionsCase switch
        {
            OutgoingPaymentVariant.OptionsOneofCase.Bolt11 =>
                PayBolt11Async(request.PaymentOptions.Bolt11, request.MaxFeeAmount, context.CancellationToken),
            OutgoingPaymentVariant.OptionsOneofCase.Bolt12 when Bolt12Available =>
                PayBolt12Async(request.PaymentOptions.Bolt12, request.MaxFeeAmount, context.CancellationToken),
            OutgoingPaymentVariant.OptionsOneofCase.Onchain when OnchainAvailable =>
                PayOnchainAsync(request.PaymentOptions.Onchain, request.MaxFeeAmount, context.CancellationToken),
            _ => throw Unimplemented(request.PaymentOptions?.OptionsCase.ToString() ?? "no method")
        };
    }

    /// <inheritdoc />
    public override async Task<CheckIncomingPaymentResponse> CheckIncomingPayment(
        CheckIncomingPaymentRequest request, ServerCallContext context)
    {
        var identifier = request.RequestIdentifier;
        var response = new CheckIncomingPaymentResponse();
        switch (identifier?.Type)
        {
            case PaymentIdentifierType.PaymentHash:
                var invoice = await _invoiceService.GetInvoiceAsync(ParseHash(identifier, "payment hash"),
                                                                    context.CancellationToken);
                // Only the mint's own invoices (NL-999)
                if (invoice is { Status: Domain.Payments.Enums.InvoiceStatus.Settled } && invoice.Label == _options.Label)
                    response.Payments.Add(Received(invoice));
                return response;
            case PaymentIdentifierType.OfferId when Bolt12Available:
                response.Payments.AddRange(await CheckOfferAsync(ParseHash(identifier, "offer id")));
                return response;
            case PaymentIdentifierType.QuoteId when OnchainAvailable:
                response.Payments.AddRange(await CheckDepositsAsync(ParseQuoteId(identifier)));
                return response;
            default:
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                  $"Identifiers of type {identifier?.Type} are not known here."));
        }
    }

    /// <inheritdoc />
    public override async Task<MakePaymentResponse> CheckOutgoingPayment(CheckOutgoingPaymentRequest request,
                                                                         ServerCallContext context)
    {
        var identifier = request.RequestIdentifier;
        switch (identifier?.Type)
        {
            case PaymentIdentifierType.PaymentHash:
                var paymentHash = ParseHash(identifier, "payment hash");
                var payment = await _paymentService.GetPaymentAsync(paymentHash, context.CancellationToken);
                // Another payment of the node, a trampoline relay's leg included, is not the mint's to read (NL-999)
                if (payment is not null && IsMintPayment(payment))
                    return ToMakePaymentResponse(payment, Identifier(paymentHash));

                // A melt saved before its payment that never reached the payment service, in this process or before
                var quote = _scopeFactory is null ? null : await WithQuotesAsync(r => r.GetOutgoingByPaymentHashAsync(paymentHash));
                return quote is not null && _inFlight.ContainsKey(quote.QuoteId)
                           ? Pending(Identifier(paymentHash))
                           : Unknown(Identifier(paymentHash));
            case PaymentIdentifierType.QuoteId when _scopeFactory is not null:
                return await CheckQuoteAsync(ParseQuoteId(identifier), context.CancellationToken);
            default:
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                  $"Identifiers of type {identifier?.Type} are not known here."));
        }
    }

    /// <inheritdoc />
    public override async Task WaitPaymentEvent(EmptyRequest request, IServerStreamWriter<PaymentEventResponse> stream,
                                                ServerCallContext context)
    {
        if (_events.SubscriberCount >= _options.MaxEventStreams)
            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                                              $"At most {_options.MaxEventStreams} event streams are open at once."));
        using var subscription = _events.Subscribe();
        _logger.LogInformation("Cashu mint subscribed to payment events");
        try
        {
            await foreach (var response in subscription.ReadAllAsync(context.CancellationToken))
            {
                // Events were dropped: end the stream, so the mint subscribes again and checks its quotes (NL-999)
                if (subscription.Overflowed)
                    throw new EventsDroppedException();
                await stream.WriteAsync(response, context.CancellationToken);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The mint went away
        }
        catch (EventsDroppedException e)
        {
            _logger.LogWarning("Payment events for the Cashu mint were dropped; its stream is ended for it to "
                             + "subscribe again and check its quotes");
            throw new RpcException(new Status(StatusCode.Unavailable, e.Message));
        }
    }

    /// <summary>The state of a melt by its quote id (BOLT 12 and on-chain melts, and BOLT 11 melts as stored).</summary>
    private async Task<MakePaymentResponse> CheckQuoteAsync(string quoteId, CancellationToken cancellationToken)
    {
        var identifier = QuoteIdentifier(quoteId);
        var quote = await WithQuotesAsync(r => r.GetAsync(quoteId));
        if (quote is not { Direction: CashuQuoteDirection.Outgoing })
            return Unknown(identifier);

        return quote.Method == CashuQuoteMethod.Onchain
                   ? OnchainResponse(quote)
                   : await LightningResponseAsync(quote, identifier, cancellationToken);
    }

    /// <summary>A melt's state when the mint asks: in flight here, or as its quote and payment say.</summary>
    private async Task<MakePaymentResponse> LightningResponseAsync(CashuQuoteModel quote, PaymentIdentifier identifier,
                                                                   CancellationToken cancellationToken)
    {
        if (quote.PaymentHash is { } paymentHash
         && await _paymentService.GetPaymentAsync(paymentHash, cancellationToken) is { } payment)
            return ToMakePaymentResponse(payment, identifier);

        return quote.State switch
        {
            CashuQuoteState.Failed => Failed(identifier),
            CashuQuoteState.Created => Unpaid(identifier),
            // Dispatching without a payment: sending here, or interrupted by a restart before the payment was stored
            // (the BOLT 12 invoice is fetched first): never answered as unpaid, so the mint never pays it twice
            _ when _inFlight.ContainsKey(quote.QuoteId) || quote.Method == CashuQuoteMethod.Bolt12 => Pending(identifier),
            _ => Unknown(identifier)
        };
    }

    /// <summary>Runs <paramref name="read"/> on a fresh unit of work's quote repository.</summary>
    private async Task<T> WithQuotesAsync<T>(Func<ICashuQuoteDbRepository, Task<T>> read)
    {
        using var scope = _scopeFactory!.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await read(unitOfWork.CashuQuoteDbRepository);
    }

    /// <summary>Stages <paramref name="write"/> on a fresh unit of work and saves it.</summary>
    private async Task SaveQuotesAsync(Action<ICashuQuoteDbRepository> write)
    {
        using var scope = _scopeFactory!.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        write(unitOfWork.CashuQuoteDbRepository);
        await unitOfWork.SaveChangesAsync();
    }

    /// <summary>Saves <paramref name="quote"/> as new or changed.</summary>
    private Task SaveQuoteAsync(CashuQuoteModel quote, bool isNew) =>
        SaveQuotesAsync(r =>
        {
            if (isNew)
                r.Add(quote);
            else
                r.Update(quote);
        });

    /// <summary>The labels of a melt: the processor's label and the mint's quote id as tag <c>cdk_quote</c>.</summary>
    private SourceLabels MeltLabels(string quoteId) =>
        SourceLabels.TryCreate(_options.Label, [$"{QuoteTagKey}={quoteId}"], out var labels, out _) ? labels : _labels;

    /// <summary>
    /// max(<see cref="CashuPaymentProcessorOptions.MinFeeReserveMsat"/>, amount × FeeReservePpm / 1,000,000).
    /// </summary>
    internal LightningMoney FeeReserve(LightningMoney amount)
    {
        var proportional = (ulong)(amount.MilliSatoshi * (UInt128)_options.FeeReservePpm / 1_000_000);
        return LightningMoney.MilliSatoshis(Math.Max(proportional, _options.MinFeeReserveMsat));
    }

    /// <summary>Refuses a melt above <see cref="CashuPaymentProcessorOptions.MaxPaymentSat"/> (NL-1004).</summary>
    private void CheckPaymentAmount(LightningMoney amount)
    {
        if ((ulong)amount.Satoshi > _options.MaxPaymentSat
         || amount.MilliSatoshi > _options.MaxPaymentSat * 1_000)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              $"The amount is above this processor's limit of {_options.MaxPaymentSat} "
                                            + "sat."));
    }

    /// <summary>
    /// The Lightning fee limit of a melt: the mint's <paramref name="requested"/> limit (the reserve without one), never
    /// above max(<see cref="CashuPaymentProcessorOptions.MinFeeReserveMsat"/>, amount × MaxFeePpm / 1,000,000) (NL-1004).
    /// </summary>
    internal LightningMoney CapFee(LightningMoney? requested, LightningMoney amount)
    {
        var proportional = (ulong)(amount.MilliSatoshi * (UInt128)_options.MaxFeePpm / 1_000_000);
        var cap = LightningMoney.MilliSatoshis(Math.Max(proportional, _options.MinFeeReserveMsat));
        var fee = requested ?? FeeReserve(amount);
        return fee > cap ? cap : fee;
    }

    internal LightningMoney ToMoney(AmountMessage amount)
    {
        if (!string.IsNullOrEmpty(amount.Unit))
            CheckUnit(amount.Unit);
        if (!_options.IsMsat && amount.Value > ulong.MaxValue / 1_000)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The amount is too large."));
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

    private MakePaymentResponse Pending(PaymentIdentifier identifier) => StatusOnly(identifier, QuoteState.Pending);

    private MakePaymentResponse Unknown(PaymentIdentifier identifier) => StatusOnly(identifier, QuoteState.Unknown);

    private MakePaymentResponse Unpaid(PaymentIdentifier identifier) => StatusOnly(identifier, QuoteState.Unpaid);

    private MakePaymentResponse Failed(PaymentIdentifier identifier) => StatusOnly(identifier, QuoteState.Failed);

    private MakePaymentResponse StatusOnly(PaymentIdentifier identifier, QuoteState state) => new()
    {
        PaymentIdentifier = identifier,
        Status = state,
        TotalSpent = new AmountMessage { Value = 0, Unit = Unit }
    };

    private static PaymentIdentifier Identifier(Hash paymentHash) => new()
    {
        Type = PaymentIdentifierType.PaymentHash,
        Hash = paymentHash.ToString()
    };

    private static PaymentIdentifier OfferIdentifier(Hash offerId) => new()
    {
        Type = PaymentIdentifierType.OfferId,
        Id = offerId.ToString()
    };

    private static PaymentIdentifier QuoteIdentifier(string quoteId) => new()
    {
        Type = PaymentIdentifierType.QuoteId,
        Id = quoteId
    };

    /// <summary>The hash a payment-hash (<c>hash</c>) or offer-id (<c>id</c>) identifier names.</summary>
    private static Hash ParseHash(PaymentIdentifier identifier, string what)
    {
        var text = identifier.ValueCase switch
        {
            PaymentIdentifier.ValueOneofCase.Hash => identifier.Hash,
            PaymentIdentifier.ValueOneofCase.Id => identifier.Id,
            _ => ""
        };
        if (text.Length != 64)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"A {what} is 64 hex characters."));
        try
        {
            return new Hash(Convert.FromHexString(text));
        }
        catch (FormatException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"The {what} is not hex."));
        }
    }

    private static string ParseQuoteId(PaymentIdentifier identifier) =>
        CheckQuoteId(identifier.ValueCase == PaymentIdentifier.ValueOneofCase.Id ? identifier.Id : null);

    /// <summary>The mint's quote id, refused when missing or too long to store.</summary>
    private static string CheckQuoteId(string? quoteId) =>
        string.IsNullOrWhiteSpace(quoteId)
            ? throw new RpcException(new Status(StatusCode.InvalidArgument, "The mint's quote id is required."))
            : quoteId.Length > CashuQuoteModel.QuoteIdMaxLength
                ? throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                    $"A quote id is at most {CashuQuoteModel.QuoteIdMaxLength} "
                                                  + "characters."))
                : quoteId;

    private static RpcException Unimplemented(string method) =>
        new(new Status(StatusCode.Unimplemented, $"{method} is not served by this processor."));
}