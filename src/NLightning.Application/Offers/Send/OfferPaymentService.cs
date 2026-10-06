using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Offers.Send;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
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
/// <para>Pay a fetched invoice later (<see cref="PayFetchedInvoiceAsync"/>, NL-1151): every verified fetch is kept in
/// memory until its invoice expires (at most <see cref="MaxRememberedInvoices"/>, the soonest to expire dropped first),
/// with the request it answers and the paths it verified, so the invoice string can be handed out (CLN's
/// <c>fetchinvoice</c>) and paid by it afterwards exactly as <see cref="PayOfferAsync"/> pays; an invoice this process
/// did not fetch is refused, never paid unverified.</para>
/// <para>Singleton; thread-safe.</para>
/// </remarks>
public sealed class OfferPaymentService : IOfferPaymentService
{
    /// <summary>The most invoice_requests one call may send.</summary>
    public const int MaxFetchAttemptsLimit = 10;

    /// <summary>The most fetched invoices kept for <see cref="PayFetchedInvoiceAsync"/>.</summary>
    public const int MaxRememberedInvoices = 1_024;

    private static readonly ulong[] s_replyTypes = [OnionMessageConstants.InvoiceType, OnionMessageConstants.InvoiceErrorType];

    /// <summary>Verified fetches by the invoice's bytes (hex), for <see cref="PayFetchedInvoiceAsync"/>.</summary>
    private readonly ConcurrentDictionary<string, RememberedInvoice> _fetched = new(StringComparer.Ordinal);

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
        if (fetched is { Verified: { } verified, InvoiceRequest: { } invoiceRequest })
            Remember(new RememberedInvoice(verified, invoiceRequest, request.Offer.Trim(), request.PayerNote));
        return fetched.Result;
    }

    /// <inheritdoc />
    public async Task<PayInvoiceResult> PayFetchedInvoiceAsync(string invoice, PayInvoiceOptions options,
                                                               CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoice);
        ArgumentNullException.ThrowIfNull(options);
        if (!Bolt12Bech32.TryDecode(invoice.Trim(), out var hrp, out var bytes, out var reason, out _))
            throw new ArgumentException($"Not a BOLT 12 invoice: {reason}", nameof(invoice));
        if (!string.Equals(hrp, Bolt12Constants.InvoiceHrp, StringComparison.Ordinal))
            throw new ArgumentException($"Not a BOLT 12 invoice (prefix {hrp}).", nameof(invoice));
        if (_fetched.TryGetValue(Convert.ToHexString(bytes), out var remembered))
        {
            if (remembered.Verified.Invoice.ExpiresAt <= _timeProvider.GetUtcNow())
                throw new ArgumentException("The invoice has expired.", nameof(invoice));
        }
        else if (!TryVerifyStateless(bytes, out remembered, out var notOurs))
        {
            // Not in memory (a restart since the fetch, or one dropped over the cap): checked from the invoice alone
            throw new ArgumentException($"Not an invoice this node fetched ({notOurs}); fetch it again.",
                                        nameof(invoice));
        }

        return await PayVerifiedAsync(remembered.Verified, remembered.InvoiceRequest, remembered.Offer,
                                      remembered.PayerNote, options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PayOfferResult> PayOfferAsync(PayOfferRequest request, PayOfferOptions options,
                                                    CancellationToken cancellationToken = default)
    {
        var fetched = await FetchAsync(request, options, cancellationToken);
        if (fetched is not { Verified: { } verified, InvoiceRequest: { } invoiceRequest })
            return new PayOfferResult(fetched.Result, null);

        var payment = await PayVerifiedAsync(verified, invoiceRequest, request.Offer.Trim(), request.PayerNote,
                                             options.Payment, cancellationToken);
        return new PayOfferResult(fetched.Result, payment);
    }

    /// <summary>Pays a verified invoice over its verified paths (see the class remarks).</summary>
    private async Task<PayInvoiceResult> PayVerifiedAsync(VerifiedInvoice verified,
                                                          BuiltInvoiceRequest invoiceRequest, string offer,
                                                          string? payerNote, PayInvoiceOptions options,
                                                          CancellationToken cancellationToken)
    {
        var invoice = verified.Invoice;
        var payRequest = new PayBlindedRequest(invoice.PaymentHash, invoice.Amount, verified.Paths)
        {
            PayeeNodeId = invoice.NodeId,
            AllowMpp = verified.AllowsMpp,
            RecipientFeatures = verified.Features.IsEmpty
                                    ? null
                                    : FeatureSet.DeserializeFromBytes(verified.Features.ToArray()),
            Bolt12 = new Bolt12PaymentDetails(offer, invoice.InvoiceBytes, invoiceRequest.Metadata, payerNote)
        };

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("payoffer: paying {Amount} msat to {NodeId} ({PaymentHash}) over {Paths} blinded "
                                 + "path(s){Mpp}", invoice.Amount.MilliSatoshi, invoice.NodeId, invoice.PaymentHash,
                                   verified.Paths.Count, verified.AllowsMpp ? ", split allowed" : string.Empty);

        return await _paymentService.PayBlindedAsync(payRequest, options, cancellationToken);
    }

    /// <summary>
    /// Keeps a verified fetch for <see cref="PayFetchedInvoiceAsync"/>: expired entries go first, then, over the cap,
    /// the soonest to expire.
    /// </summary>
    private void Remember(RememberedInvoice remembered)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (key, entry) in _fetched)
            if (entry.Verified.Invoice.ExpiresAt <= now)
                _fetched.TryRemove(key, out _);
        _fetched[Convert.ToHexString(remembered.Verified.Invoice.InvoiceBytes.Span)] = remembered;
        while (_fetched.Count > MaxRememberedInvoices)
        {
            var soonest = _fetched.MinBy(e => e.Value.Verified.Invoice.ExpiresAt);
            _fetched.TryRemove(soonest.Key, out _);
        }
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

    /// <summary>
    /// Verifies a BOLT 12 invoice this process has no memory of (NL-1157): its <c>invreq_metadata</c> must commit to
    /// its request fields and derive its <c>invreq_payer_id</c> with our key — so it answers a request this node made,
    /// unchanged (<see cref="InvoiceRequestFactory.IsCommittedMetadata"/>) — and then it passes the same
    /// <see cref="InvoiceVerifier"/> checks as a fresh fetch, against the request rebuilt from those fields, through
    /// whichever of the offer's paths names its node.
    /// </summary>
    private bool TryVerifyStateless(byte[] invoiceBytes, [NotNullWhen(true)] out RememberedInvoice? remembered,
                                    [NotNullWhen(false)] out string? reason)
    {
        remembered = null;
        if (_signer is null)
        {
            reason = "BOLT 12 signing is not available on this node";
            return false;
        }

        try
        {
            var stream = Bolt12TlvStream.Parse(invoiceBytes);
            if (!stream.TryGetValue(Bolt12TlvTypes.InvreqMetadata, out var metadata)
             || !stream.TryGetValue(Bolt12TlvTypes.InvreqPayerId, out var payerId))
            {
                reason = "no invreq_metadata or invreq_payer_id";
                return false;
            }

            var fields = stream.Records.Where(r => InvoiceVerifier.IsMirroredType(r.Type)
                                                && r.Type is not (Bolt12TlvTypes.InvreqMetadata
                                                                  or Bolt12TlvTypes.InvreqPayerId)).ToList();
            if (!InvoiceRequestFactory.IsCommittedMetadata(metadata.Span, fields)
             || !payerId.Span.SequenceEqual((byte[])_signer.DerivePayerId(metadata)))
            {
                reason = "it does not answer an invoice_request of this node";
                return false;
            }

            // The offer as we requested it (its records are mirrored verbatim), and what we asked for
            var offerRecords = fields.Where(r => r.Type is >= 1 and <= 79 or >= 1_000_000_000 and <= 1_999_999_999)
                                     .ToList();
            var offerText = Bolt12Bech32.Encode(Bolt12Constants.OfferHrp, new Bolt12TlvStream(offerRecords).Encode());
            var chain = _nodeOptions.Value.BitcoinNetwork.ChainHash;
            var now = _timeProvider.GetUtcNow();
            var offer = OfferToPay.Parse(offerText, chain, now);
            LightningMoney? amount = null;
            if (stream.TryGetValue(Bolt12TlvTypes.InvreqAmount, out var amountValue))
                amount = TruncatedInt.TryDecodeTu64(amountValue.Span, out var msat)
                             ? LightningMoney.MilliSatoshis(msat)
                             : throw new FormatException("invreq_amount is not a minimal tu64");
            ulong? quantity = null;
            if (stream.TryGetValue(Bolt12TlvTypes.InvreqQuantity, out var quantityValue))
                quantity = TruncatedInt.TryDecodeTu64(quantityValue.Span, out var q)
                               ? q
                               : throw new FormatException("invreq_quantity is not a minimal tu64");
            var payerNote = OfferToPay.ReadUtf8(stream, Bolt12TlvTypes.InvreqPayerNote);
            var request = InvoiceRequestFactory.Create(offer, new PayOfferRequest(offerText, amount, quantity, payerNote),
                                                       chain, _signer, metadata.ToArray());

            reason = "no offer path names the invoice's node";
            IReadOnlyList<WireBlindedPath?> sentTo = offer.Paths.Count > 0 ? [.. offer.Paths] : [null];
            foreach (var path in sentTo)
            {
                if (!InvoiceVerifier.TryVerify(invoiceBytes, request, offer, path, chain, now, _signer, ResolveNode,
                                               out var verified, out reason))
                    continue;

                remembered = new RememberedInvoice(verified, request, offerText, payerNote);
                return true;
            }

            return false;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            reason = e.Message;
            return false;
        }
    }

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
            var stream = Bolt12TlvStream.Parse(bytes);
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

    private sealed record RememberedInvoice(VerifiedInvoice Verified, BuiltInvoiceRequest InvoiceRequest, string Offer,
                                            string? PayerNote);
}