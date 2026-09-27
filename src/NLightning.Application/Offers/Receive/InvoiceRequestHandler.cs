using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Offers.Receive;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Offers.Constants;
using Domain.Offers.Enums;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Enums;
using Domain.Protocol.OnionMessages.Interfaces;

/// <summary>
/// What <see cref="InvoiceRequestHandler.ProcessAsync"/> did with an invoice_request (for logs and tests).
/// </summary>
public enum InvoiceRequestOutcome
{
    /// <summary>Answered with a signed invoice.</summary>
    Invoice,

    /// <summary>Answered with an <c>invoice_error</c> (the request's signature verified).</summary>
    InvoiceError,

    /// <summary>Not answered: refused before its signature verified, ignored by a BOLT 12 "MUST ignore" rule, or
    /// dropped by the rate limit.</summary>
    Ignored
}

/// <summary>
/// Answers invoice_requests for our offers (<see cref="IOnionMessageHandler"/> for <c>onionmsg_tlv</c> type 64; BOLT 12
/// "Invoice Requests" reader and "Invoices" writer; plan B3-T2, B3-T3, §3.7 steps 2-4).
/// </summary>
/// <remarks>
/// <para>Order (first failure wins): offers unavailable, no <c>reply_path</c> or the node-wide rate limit (one token
/// per request, taken before parsing) → ignore;
/// <see cref="InvoiceRequestReader"/> (B12-IRQ-02 without the offer) and the <c>invreq_payer_id</c> signature → ignore
/// (plan D10: nothing is answered before the signature verified); a request that answers no offer (no issuer id and no
/// paths: the refund flow, out of scope) → ignore; offer fields that match no offer of ours → <c>invoice_error</c>;
/// the arrival path (an offer with <c>offer_paths</c> only through one of them, recognized by the <c>path_id</c>
/// <see cref="OfferPathIds"/> gave it; an offer without paths only when the message came through no blinded path of
/// ours) → ignore (BOLT 12 MUST ignore, B12-IRQ-03); the per-offer rate limit → ignore; chain, quantity, amount,
/// <c>invreq_bip_353_name</c> (<see cref="OfferInvoiceRequestRules"/>) → <c>invoice_error</c> naming the field; an
/// offer that is disabled or expired → <c>invoice_error</c>; the unpaid invoice caps (plan D11) or no payment path →
/// <c>invoice_error</c>.</para>
/// <para>Then the invoice (<see cref="OfferInvoiceFactory"/>): a fresh preimage, blinded payment paths from
/// <see cref="IBlindedPaymentPathSource"/>, signed by the node (plan D2: every offer of ours has our node id as
/// <c>offer_issuer_id</c>). Its <see cref="InvoiceModel"/> (<c>Kind</c> BOLT 12, with the offer id, the invoice bytes,
/// the payer id, the quantity and the note) is saved <b>before</b> the reply is sent, so no payment can arrive for an
/// invoice we forgot; the final hop then accepts it only through its blinded paths (<c>FinalHopProcessor</c>). The
/// row has no BOLT 11 string (see <see cref="CreateInvoiceModel"/>). Every invoice is new: we never answer twice with the same invoice (BOLT 12 allows it only
/// with an issuer id and the same metadata, MAY).</para>
/// <para>Replies go through the request's <c>reply_path</c> with <see cref="IOnionMessageService.SendAsync"/>, resolved
/// lazily (the onion-message service takes every handler, this one included). Singleton; handles one message at a
/// time on the onion-message service's handler queue, but is thread-safe. Without an <see cref="IBolt12Signer"/> (none
/// registered) every request is ignored.</para>
/// </remarks>
public sealed class InvoiceRequestHandler : IOnionMessageHandler
{
    /// <summary>The <c>onionmsg_tlv</c> type of an invoice_request.</summary>
    public const ulong InvoiceRequestType = 64;

    /// <summary>The <c>onionmsg_tlv</c> type of an invoice.</summary>
    public const ulong InvoiceType = 66;

    /// <summary>The <c>onionmsg_tlv</c> type of an invoice_error.</summary>
    public const ulong InvoiceErrorType = 68;

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly IBolt12Signer? _signer;
    private readonly IBlindedPaymentPathSource _pathSource;
    private readonly OfferPathIds _pathIds;
    private readonly IOptions<NodeOptions> _nodeOptions;
    private readonly OfferOptions _offerOptions;
    private readonly InvoiceRequestRateLimiter _rateLimiter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InvoiceRequestHandler> _logger;

    public InvoiceRequestHandler(IServiceScopeFactory serviceScopeFactory, IServiceProvider serviceProvider,
                                 ISecureKeyManager secureKeyManager, IBolt12Signer? signer,
                                 IBlindedPaymentPathSource pathSource, OfferPathIds pathIds,
                                 IOptions<NodeOptions> nodeOptions, InvoiceRequestRateLimiter rateLimiter,
                                 ILogger<InvoiceRequestHandler> logger, IOptions<OfferOptions>? offerOptions = null,
                                 TimeProvider? timeProvider = null)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _serviceProvider = serviceProvider;
        _secureKeyManager = secureKeyManager;
        _signer = signer;
        _pathSource = pathSource;
        _pathIds = pathIds;
        _nodeOptions = nodeOptions;
        _rateLimiter = rateLimiter;
        _logger = logger;
        _offerOptions = offerOptions?.Value ?? new OfferOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ulong> PayloadTypes { get; } = [InvoiceRequestType];

    /// <inheritdoc />
    public async Task HandleAsync(ReceivedOnionMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // A handler failure must never reach the onion-message worker
            _logger.LogError(e, "Answering an invoice_request from {Peer} failed", message.FromPeer);
        }
    }

    /// <summary>
    /// Handles one invoice_request (see the class remarks) and says what was done.
    /// </summary>
    public async Task<InvoiceRequestOutcome> ProcessAsync(ReceivedOnionMessage message,
                                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeOptions = _nodeOptions.Value;
        if (nodeOptions.Features.OptionRouteBlinding == FeatureSupport.No)
            return Ignore("route blinding is off, so no invoice of ours could be paid");

        if (_signer is null)
            return Ignore("no BOLT 12 signer is registered");

        if (message.ReplyPath is not { } replyPath)
            return Ignore("no reply_path");

        // Every request costs a node-wide token before any parsing or signature check, so floods of bad signatures,
        // unknown offers or wrong paths are capped too
        if (!_rateLimiter.TryTakeGlobal())
            return Ignore("node-wide invoice_request rate limit");

        var payload = message.Contents.Records.FirstOrDefault(r => r.Type == InvoiceRequestType);
        if (payload is null)
            return Ignore("no invoice_request field");

        if (!InvoiceRequestReader.TryRead(payload.Value, out var request, out var reason))
            return Ignore($"malformed invoice_request: {reason}");

        var merkleRoot = Bolt12Wire.ComputeMerkleRoot(request!.Stream.Records);
        if (!_signer.Verify(Bolt12Constants.InvoiceRequestSignatureTag, merkleRoot, request.PayerId,
                            request.Signature))
            return Ignore("invalid invreq_payer_id signature");

        // From here on the payer is authenticated: failures may be answered (plan D10)
        if (!request.IsForOffer)
            return Ignore("an invoice_request without an offer (refund flow) is not supported");

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var offer = await unitOfWork.OfferDbRepository.GetByOfferBytesAsync(request.OfferBytes);
        // Never answered: an invoice_error would tell a prober which offers are not ours, and so, by silence, which
        // ones are (linking an offer with offer_paths to our node id, or two offers to each other). BOLT 12 rationale
        // of Invoice Requests: a node must not reveal it is the source of an offer
        if (offer is null)
            return Ignore("the offer fields match no offer of ours");

        // BOLT 12: MUST ignore a request that did not come through one of the offer's paths, and, for an offer without
        // paths, one that came through a blinded path (a path_id of ours: the sender's own path to us has none)
        if (offer.HasPaths
                ? message.PathId is not { } pathId || !_pathIds.Matches(pathId.Span, offer.Metadata.Span)
                : message.PathId is not null)
            return Ignore($"offer {offer.OfferId}: the request did not arrive through the offer's paths");

        if (!_rateLimiter.TryAdmit(offer.OfferId))
            return Ignore($"offer {offer.OfferId}: invoice_request rate limit");

        var refusal = OfferInvoiceRequestRules.Check(request, offer, nodeOptions.BitcoinNetwork.ChainHash,
                                                     out var amountMsat);
        if (refusal is not null)
            return await RefuseAsync(replyPath, refusal, cancellationToken);

        var now = _timeProvider.GetUtcNow();
        if (!offer.IsActive(now))
            return await RefuseAsync(replyPath,
                                     offer.IsExpired(now) || offer.Status == OfferStatus.Expired
                                         ? new InvoiceRequestRefusal("Offer expired",
                                                                     Bolt12TlvTypes.OfferAbsoluteExpiry)
                                         : new InvoiceRequestRefusal("Offer no longer available"),
                                     cancellationToken);

        if (offer.IssuerKind != OfferIssuerKind.NodeId)
            return Ignore($"offer {offer.OfferId}: only offers signed by our node id are answered");

        var counts = await unitOfWork.OfferDbRepository.GetInvoiceCountsAsync(offer.OfferId, now);
        if (counts.Unpaid >= _offerOptions.MaxUnpaidInvoicesPerOffer
         || await unitOfWork.OfferDbRepository.CountUnpaidInvoicesAsync(now) >= _offerOptions.MaxUnpaidInvoices)
            return await RefuseAsync(replyPath, new InvoiceRequestRefusal("Temporarily unavailable"),
                                     cancellationToken);

        var preimage = RandomNumberGenerator.GetBytes(CryptoConstants.SecretLen);
        var paymentHash = new Hash(SHA256.HashData(preimage));
        var amount = LightningMoney.MilliSatoshis(amountMsat);
        var relativeExpiry = _offerOptions.InvoiceRelativeExpirySeconds;
        var paths = await _pathSource.CreateAsync(new Secret(preimage), amount, relativeExpiry, cancellationToken);
        if (paths.Count == 0)
            return await RefuseAsync(replyPath, new InvoiceRequestRefusal("No payment path available"),
                                     cancellationToken);

        var createdAt = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        var invoiceBytes = OfferInvoiceFactory.CreateInvoice(request, paths, createdAt, relativeExpiry, paymentHash,
                                                             amountMsat,
                                                             nodeOptions.Features.BasicMpp != FeatureSupport.No,
                                                             _secureKeyManager.GetNodePubKey(), _signer);

        var invoice = CreateInvoiceModel(paymentHash, preimage, amount, offer.Description, createdAt, relativeExpiry,
                                         nodeOptions.Routing.InvoiceMinFinalCltvExpiry,
                                         new Bolt12InvoiceDetails(offer.OfferId, invoiceBytes, request.PayerId,
                                                                  request.Quantity, request.PayerNote));
        await unitOfWork.InvoiceDbRepository.AddAsync(invoice);
        await unitOfWork.SaveChangesAsync();

        var result = await SendAsync(replyPath, OnionMessageContents.Single(InvoiceType, invoiceBytes),
                                     cancellationToken);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Answered an invoice_request for offer {OfferId} with invoice {PaymentHash} ({AmountMsat} msat, "
              + "{PathCount} path(s)): {Status}", offer.OfferId, paymentHash, amountMsat, paths.Count, result);

        return InvoiceRequestOutcome.Invoice;
    }

    /// <summary>
    /// The invoice row: no BOLT 11 string (BOLT 12 invoices have none; lane B12-C's <see cref="InvoiceModel"/>), or,
    /// with the B12-0 contract that still requires one, the invoice as an <c>lni1...</c> string (CLN's convention).
    /// </summary>
    /// <remarks>Integration seam: once B12-C is merged the fallback is dead and goes.</remarks>
    private static InvoiceModel CreateInvoiceModel(Hash paymentHash, byte[] preimage, LightningMoney amount,
                                                   string? description, DateTimeOffset createdAt, uint expirySeconds,
                                                   ushort minFinalCltvExpiry, Bolt12InvoiceDetails details)
    {
        var paymentSecret = new Secret(RandomNumberGenerator.GetBytes(CryptoConstants.SecretLen));
        try
        {
            return new InvoiceModel(paymentHash, new Secret(preimage), paymentSecret, amount, description, null!,
                                    createdAt, expirySeconds, minFinalCltvExpiry, bolt12: details);
        }
        catch (ArgumentException)
        {
            return new InvoiceModel(paymentHash, new Secret(preimage), paymentSecret, amount, description,
                                    Bolt12Wire.ToBolt12String(Bolt12Constants.InvoiceHrp,
                                                              details.InvoiceBytes.Span),
                                    createdAt, expirySeconds, minFinalCltvExpiry, bolt12: details);
        }
    }

    private async Task<InvoiceRequestOutcome> RefuseAsync(WireBlindedPath replyPath, InvoiceRequestRefusal refusal,
                                                          CancellationToken cancellationToken)
    {
        var result = await SendAsync(replyPath,
                                     OnionMessageContents.Single(InvoiceErrorType,
                                                                 OfferInvoiceFactory.CreateInvoiceError(refusal)),
                                     cancellationToken);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Refused an invoice_request: {Error} (field {Field}); invoice_error {Status}",
                                   refusal.Error, refusal.ErroneousField, result);

        return InvoiceRequestOutcome.InvoiceError;
    }

    private async Task<OnionMessageSendStatus> SendAsync(WireBlindedPath replyPath, OnionMessageContents contents,
                                                         CancellationToken cancellationToken)
    {
        var service = _serviceProvider.GetService<IOnionMessageService>();
        if (service is null)
            return OnionMessageSendStatus.NotAvailable;

        var result = await service.SendAsync(OnionMessageDestination.ToBlindedPath(replyPath), contents, null,
                                             cancellationToken);
        return result.Status;
    }

    private InvoiceRequestOutcome Ignore(string reason)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Ignoring an invoice_request: {Reason}", reason);

        return InvoiceRequestOutcome.Ignored;
    }
}