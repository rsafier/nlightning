namespace NLightning.Domain.Offers.Validators;

using Constants;
using Crypto.ValueObjects;
using Encoding;
using Protocol.Constants;
using Protocol.ValueObjects;

/// <summary>
/// The BOLT 12 invoice reader's rules on the invoice alone (B12-INV-03, B12-SIG-03) and against the invoice_request it
/// answers (B12-INV-04, except the signature and the arrival path), on an invoice that already parsed.
/// </summary>
/// <remarks>
/// The signature is checked with <see cref="Interfaces.IBolt12Signer.Verify"/> by <c>invoice_node_id</c> over
/// <see cref="Signing.Bolt12MerkleTree.ComputeRoot"/>. B12-INV-05 is the payer's: pay over several paths only when
/// <see cref="AllowsMultiPart"/>, and use only <see cref="Models.FallbackAddress.IsUsable"/> fallbacks.
/// </remarks>
public static class InvoiceValidator
{
    /// <summary>
    /// The invoice feature bit MPP/compulsory.
    /// </summary>
    public const int MppCompulsoryBit = 16;

    /// <summary>
    /// The invoice feature bit MPP/optional.
    /// </summary>
    public const int MppOptionalBit = 17;

    private static readonly HashSet<int> s_knownInvoiceEvenBits = [MppCompulsoryBit];

    /// <summary>
    /// The first reader rule <paramref name="invoice"/> breaks on its own, or null.
    /// </summary>
    /// <param name="invoice">The invoice.</param>
    /// <param name="now">The current time for the expiry check, or null to skip it.</param>
    /// <param name="supportedChains">The chains we accept, or null to skip the chain check (no <c>invreq_chain</c>
    /// means bitcoin mainnet).</param>
    public static Bolt12Violation? Validate(Bolt12Invoice invoice, DateTimeOffset? now = null,
                                            IReadOnlyCollection<ChainHash>? supportedChains = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var fields = invoice.Fields;

        if (fields.Amount is null)
            return Reader("invoice_amount is missing.", Bolt12TlvTypes.InvoiceAmount);
        if (fields.CreatedAt is null)
            return Reader("invoice_created_at is missing.", Bolt12TlvTypes.InvoiceCreatedAt);
        if (fields.PaymentHash is null)
            return Reader("invoice_payment_hash is missing.", Bolt12TlvTypes.InvoicePaymentHash);
        if (fields.NodeId is null)
            return Reader("invoice_node_id is missing.", Bolt12TlvTypes.InvoiceNodeId);

        if (supportedChains is not null
         && !supportedChains.Contains(invoice.InvoiceRequestFields.Chain ?? ChainConstants.Main))
            return Reader("The invoice's chain is not supported.", Bolt12TlvTypes.InvreqChain);

        if (fields.Features is { } features
         && Bolt12FieldCodec.FindUnknownEvenBit(features.Span, s_knownInvoiceEvenBits) is { } bit)
            return Reader($"invoice_features sets the unknown even bit {bit}.", Bolt12TlvTypes.InvoiceFeatures);

        if (now is { } time && IsExpired(invoice, time))
            return Reader("The invoice has expired.", Bolt12TlvTypes.InvoiceCreatedAt);

        if (fields.Paths is not { Count: > 0 } paths)
            return Reader("invoice_paths is missing or empty.", Bolt12TlvTypes.InvoicePaths);

        if (fields.BlindedPay is not { } payInfos)
            return Reader("invoice_blindedpay is missing.", Bolt12TlvTypes.InvoiceBlindedPay);

        if (payInfos.Count != paths.Count)
            return Reader($"invoice_blindedpay has {payInfos.Count} entries for {paths.Count} paths.",
                          Bolt12TlvTypes.InvoiceBlindedPay);

        if (GetUsablePathIndexes(invoice).Count == 0)
            return Reader("Every path's payinfo sets an unknown even feature bit.", Bolt12TlvTypes.InvoiceBlindedPay);

        return InvoiceRequestValidator.CheckSignatureElements(invoice.Stream, required: true);
    }

    /// <summary>
    /// The reader rules against the invoice_request we sent (B12-INV-04), except the signature and the arrival path:
    /// the fields 0-159 and 1,000,000,000-2,999,999,999 exactly match, <c>invoice_node_id</c> is the expected key, and
    /// <c>invoice_amount</c> equals <c>invreq_amount</c> when we set it.
    /// </summary>
    /// <param name="invoice">The invoice.</param>
    /// <param name="request">The invoice_request we sent.</param>
    /// <param name="expectedNodeId">For a request to an offer without <c>offer_issuer_id</c>: the final
    /// <c>blinded_node_id</c> of the path we sent it to. Ignored when the offer has <c>offer_issuer_id</c>.</param>
    public static Bolt12Violation? ValidateAgainstRequest(Bolt12Invoice invoice, InvoiceRequest request,
                                                          CompactPubKey? expectedNodeId = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(request);

        var sent = request.Stream.Filter(Bolt12TlvRanges.IsInvoiceRequestField);
        if (!invoice.GetInvoiceRequestStream().ContentEquals(sent))
            return Match("The invoice's invoice_request fields do not exactly match our request.");

        var nodeId = invoice.Fields.NodeId;
        if (request.OfferFields.IssuerId is { } issuerId)
        {
            if (nodeId != issuerId)
                return Match("invoice_node_id is not offer_issuer_id.", Bolt12TlvTypes.InvoiceNodeId);
        }
        else if (request.OfferFields.Paths is not null && expectedNodeId is { } expected && nodeId != expected)
        {
            return Match("invoice_node_id is not the final blinded_node_id we sent the request to.",
                         Bolt12TlvTypes.InvoiceNodeId);
        }

        if (request.Fields.Amount is { } requested && invoice.Fields.Amount != requested)
            return Match($"invoice_amount {invoice.Fields.Amount} is not our invreq_amount {requested}.",
                         Bolt12TlvTypes.InvoiceAmount);

        return null;
    }

    /// <summary>
    /// Whether <paramref name="now"/> is past <c>invoice_created_at</c> plus the relative expiry (7200 s by default).
    /// </summary>
    public static bool IsExpired(Bolt12Invoice invoice, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (invoice.Fields.CreatedAt is not { } createdAt)
            return false;

        var expiresAt = (UInt128)createdAt + invoice.Fields.EffectiveRelativeExpiry;
        return (UInt128)(ulong)Math.Max(0, now.ToUnixTimeSeconds()) > expiresAt;
    }

    /// <summary>
    /// The indexes of the paths a payer may use: those whose payinfo sets no unknown even feature bit.
    /// </summary>
    public static IReadOnlyList<int> GetUsablePathIndexes(Bolt12Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (invoice.Fields.Paths is not { } paths || invoice.Fields.BlindedPay is not { } payInfos)
            return [];

        var usable = new List<int>();
        for (var i = 0; i < Math.Min(paths.Count, payInfos.Count); i++)
            if (Bolt12FieldCodec.FindUnknownEvenBit(payInfos[i].Features.Span) is null)
                usable.Add(i);

        return usable;
    }

    /// <summary>
    /// B12-INV-05: whether the payer may split the payment (MPP/compulsory or MPP/optional set).
    /// </summary>
    public static bool AllowsMultiPart(Bolt12Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return invoice.Fields.Features is { } features
            && (Bolt12FieldCodec.IsBitSet(features.Span, MppCompulsoryBit)
             || Bolt12FieldCodec.IsBitSet(features.Span, MppOptionalBit));
    }

    /// <summary>
    /// B12-INV-05: whether the payer must split the payment over several paths (MPP/compulsory set).
    /// </summary>
    public static bool RequiresMultiPart(Bolt12Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return invoice.Fields.Features is { } features
            && Bolt12FieldCodec.IsBitSet(features.Span, MppCompulsoryBit);
    }

    private static Bolt12Violation Reader(string reason, ulong field) =>
        new(Bolt12RequirementIds.InvoiceReader, reason, field);

    private static Bolt12Violation Match(string reason, ulong? field = null) =>
        new(Bolt12RequirementIds.InvoiceMatchesRequest, reason, field);
}