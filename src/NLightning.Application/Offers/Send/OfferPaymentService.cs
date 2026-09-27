using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Offers.Send;

using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Offers.Constants;
using Domain.Offers.Enums;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.OnionMessages.Enums;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.Tlv;
using OnionMessages;

/// <summary>
/// Pays BOLT 12 offers (<c>payoffer</c>, <c>fetchinvoice</c>; BOLT 12 plan §3.8, B4-T3).
/// </summary>
/// <remarks>
/// <para>Fetch: <see cref="OfferToPay.Parse"/> (B12-OFR-03), <see cref="InvoiceRequestFactory.Create"/>
/// (B12-IRQ-01), then up to <see cref="PayOfferOptions.MaxFetchAttempts"/> sends of the same invoice_request (field 64)
/// through <see cref="IOnionMessageService.SendAndWaitForReplyAsync"/> with a fresh reply path each time, expecting 66
/// or 68 within <see cref="PayOfferOptions.FetchTimeout"/>: over the offer's paths in turn (B12-OFR-04: a request goes
/// through <c>offer_paths</c> when the offer has them), else to <c>offer_issuer_id</c>. Each attempt that times out,
/// cannot be sent or brings an invoice that fails <see cref="InvoiceVerifier"/> moves on to the next path (BOLT 4
/// OM-S-07); an <c>invoice_error</c> ends the fetch; onion messages off end it as
/// <see cref="FetchInvoiceStatus.Unreachable"/>.</para>
/// <para>Pay: <see cref="IPaymentService.PayBlindedAsync"/> over the invoice's usable paths with
/// <c>invoice_node_id</c> as the payee, a split allowed when <c>invoice_features</c> sets <c>basic_mpp</c>
/// (B12-INV-05) and the BOLT 12 details stored with the payment.</para>
/// <para>Singleton; thread-safe (no state between calls).</para>
/// </remarks>
public sealed class OfferPaymentService : IOfferPaymentService
{
    /// <summary>The most invoice_requests one call may send.</summary>
    public const int MaxFetchAttemptsLimit = 10;

    private static readonly ulong[] s_replyTypes = [OnionMessageConstants.InvoiceType, OnionMessageConstants.InvoiceErrorType];

    private readonly ILogger<OfferPaymentService> _logger;
    private readonly IOptions<NodeOptions> _nodeOptions;
    private readonly IOnionMessageService _onionMessageService;
    private readonly OnionMessagePathFinder? _pathFinder;
    private readonly IPaymentService _paymentService;
    private readonly IBolt12Signer? _signer;
    private readonly TimeProvider _timeProvider;

    public OfferPaymentService(IOnionMessageService onionMessageService, IPaymentService paymentService,
                               IOptions<NodeOptions> nodeOptions, ILogger<OfferPaymentService> logger,
                               TimeProvider timeProvider, IBolt12Signer? signer = null,
                               OnionMessagePathFinder? pathFinder = null)
    {
        _onionMessageService = onionMessageService;
        _paymentService = paymentService;
        _nodeOptions = nodeOptions;
        _logger = logger;
        _timeProvider = timeProvider;
        _signer = signer;
        _pathFinder = pathFinder;
    }

    /// <inheritdoc />
    /// <remarks>Also false without an <see cref="IBolt12Signer"/> registered.</remarks>
    public bool IsAvailable => _signer is not null && _onionMessageService.IsAvailable;

    /// <inheritdoc />
    public async Task<FetchInvoiceResult> FetchInvoiceAsync(PayOfferRequest request, PayOfferOptions options,
                                                            CancellationToken cancellationToken = default)
    {
        var fetched = await FetchAsync(request, options, cancellationToken);
        return fetched.Result;
    }

    /// <inheritdoc />
    public async Task<PayOfferResult> PayOfferAsync(PayOfferRequest request, PayOfferOptions options,
                                                    CancellationToken cancellationToken = default)
    {
        var fetched = await FetchAsync(request, options, cancellationToken);
        if (fetched is not { Verified: { } verified, InvoiceRequest: { } invoiceRequest })
            return new PayOfferResult(fetched.Result, null);

        var invoice = verified.Invoice;
        var payRequest = new PayBlindedRequest(invoice.PaymentHash, invoice.Amount, verified.Paths)
        {
            PayeeNodeId = invoice.NodeId,
            AllowMpp = verified.AllowsMpp,
            Bolt12 = new Bolt12PaymentDetails(request.Offer.Trim(), invoice.InvoiceBytes, invoiceRequest.Metadata,
                                              request.PayerNote)
        };

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("payoffer: paying {Amount} msat to {NodeId} ({PaymentHash}) over {Paths} blinded "
                                 + "path(s){Mpp}", invoice.Amount.MilliSatoshi, invoice.NodeId, invoice.PaymentHash,
                                   verified.Paths.Count, verified.AllowsMpp ? ", split allowed" : string.Empty);

        var payment = await _paymentService.PayBlindedAsync(payRequest, options.Payment, cancellationToken);
        return new PayOfferResult(fetched.Result, payment);
    }

    private async Task<FetchOutcome> FetchAsync(PayOfferRequest request, PayOfferOptions options,
                                                CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Offer);
        ArgumentNullException.ThrowIfNull(options.Payment);
        if (options.FetchTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "The fetch timeout must be positive.");
        if (options.MaxFetchAttempts is < 1 or > MaxFetchAttemptsLimit)
            throw new ArgumentOutOfRangeException(nameof(options),
                                                  $"The fetch attempts must be 1 to {MaxFetchAttemptsLimit}.");

        var chain = _nodeOptions.Value.BitcoinNetwork.ChainHash;
        var offer = OfferToPay.Parse(request.Offer.Trim(), chain, _timeProvider.GetUtcNow());
        if (_signer is null)
            return new FetchOutcome(new FetchInvoiceResult(FetchInvoiceStatus.Unreachable, null, 0,
                                                           "BOLT 12 signing is not available on this node."));
        if (!_onionMessageService.IsAvailable)
            return new FetchOutcome(new FetchInvoiceResult(FetchInvoiceStatus.Unreachable, null, 0,
                                                           "Onion messages are off (option_onion_messages)."));

        var invoiceRequest = InvoiceRequestFactory.Create(offer, request, chain, _signer);
        var contents = OnionMessageContents.Single(OnionMessageConstants.InvoiceRequestType, invoiceRequest.Bytes);
        FetchInvoiceStatus? failure = null;
        string? lastReason = null;
        var attempts = 0;
        for (var attempt = 0; attempt < options.MaxFetchAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = offer.Paths.Count > 0 ? offer.Paths[attempt % offer.Paths.Count] : null;
            var destination = path is not null
                                  ? OnionMessageDestination.ToBlindedPath(path)
                                  : OnionMessageDestination.ToNode(offer.IssuerId!.Value);
            var target = path is not null ? $"offer path {attempt % offer.Paths.Count}" : $"{offer.IssuerId}";

            var sent = await _onionMessageService.SendAndWaitForReplyAsync(destination, contents, s_replyTypes,
                                                                          options.FetchTimeout, cancellationToken);
            switch (sent.Status)
            {
                case OnionMessageSendStatus.NotAvailable:
                    return new FetchOutcome(new FetchInvoiceResult(FetchInvoiceStatus.Unreachable, null, attempts,
                                                                   "Onion messages are off (option_onion_messages)."));
                case OnionMessageSendStatus.NoPath or OnionMessageSendStatus.Dropped
                                                  or OnionMessageSendStatus.TooLarge:
                    lastReason = $"The invoice_request could not be sent to {target} ({sent.Status}).";
                    _logger.LogInformation("fetchinvoice: {Reason}", lastReason);
                    failure ??= FetchInvoiceStatus.Unreachable;
                    continue;
                case OnionMessageSendStatus.ReplyTimedOut:
                    attempts++;
                    lastReason = $"No reply from {target} within {options.FetchTimeout.TotalSeconds:0.#} s.";
                    _logger.LogInformation("fetchinvoice: {Reason}", lastReason);
                    if (failure is not FetchInvoiceStatus.InvalidInvoice)
                        failure = FetchInvoiceStatus.TimedOut;
                    continue;
                case OnionMessageSendStatus.Replied when sent.Reply is { } reply:
                    attempts++;
                    if (TryGetRecord(reply, OnionMessageConstants.InvoiceErrorType, out var errorBytes))
                    {
                        var (error, field) = ReadInvoiceError(errorBytes);
                        _logger.LogInformation("fetchinvoice: {Target} answered with invoice_error: {Error}", target,
                                               error);
                        return new FetchOutcome(new FetchInvoiceResult(FetchInvoiceStatus.InvoiceError, null,
                                                                       attempts, error, field));
                    }

                    var invalid = "no invoice in the reply";
                    if (TryGetRecord(reply, OnionMessageConstants.InvoiceType, out var invoiceBytes)
                     && InvoiceVerifier.TryVerify(invoiceBytes, invoiceRequest, offer, path, chain,
                                                  _timeProvider.GetUtcNow(), _signer, ResolveNode, out var verified,
                                                  out invalid))
                    {
                        _logger.LogInformation("fetchinvoice: invoice for {Amount} msat from {NodeId} with {Paths} "
                                             + "usable path(s)", verified.Invoice.Amount.MilliSatoshi,
                                               verified.Invoice.NodeId, verified.Paths.Count);
                        return new FetchOutcome(new FetchInvoiceResult(FetchInvoiceStatus.Received, verified.Invoice,
                                                                       attempts), verified, invoiceRequest);
                    }

                    lastReason = $"The invoice from {target} was rejected: {invalid}";
                    _logger.LogWarning("fetchinvoice: {Reason}", lastReason);
                    failure = FetchInvoiceStatus.InvalidInvoice;
                    continue;
                default:
                    lastReason = $"Unexpected send result {sent.Status} for {target}.";
                    failure ??= FetchInvoiceStatus.Unreachable;
                    continue;
            }
        }

        return new FetchOutcome(new FetchInvoiceResult(failure ?? FetchInvoiceStatus.Unreachable, null, attempts,
                                                       lastReason));
    }

    private CompactPubKey? ResolveNode(SciddirOrPubkey node) => node.NodeId ?? _pathFinder?.Resolve(node);

    private static bool TryGetRecord(ReceivedOnionMessage reply, ulong type, out ReadOnlyMemory<byte> value)
    {
        foreach (var record in reply.Contents.Records)
        {
            if (record.Type != type)
                continue;
            value = record.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// BOLT 12 <c>invoice_error</c>: <c>error</c> (5, utf8) and <c>erroneous_field</c> (1, tu64); a malformed one is
    /// reported as such.
    /// </summary>
    internal static (string Error, ulong? ErroneousField) ReadInvoiceError(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var stream = Bolt12Wire.ParseStream(bytes);
            ulong? field = null;
            if (stream.TryGetValue(Bolt12TlvTypes.ErroneousField, out var fieldValue)
             && TruncatedInt.TryDecodeTu64(fieldValue.Span, out var decoded))
                field = decoded;
            var error = OfferToPay.ReadUtf8(stream, Bolt12TlvTypes.Error) ?? "(invoice_error without a message)";
            return (error, field);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return ($"(malformed invoice_error: {e.Message})", null);
        }
    }

    private sealed record FetchOutcome(FetchInvoiceResult Result, VerifiedInvoice? Verified = null,
                                       BuiltInvoiceRequest? InvoiceRequest = null);
}