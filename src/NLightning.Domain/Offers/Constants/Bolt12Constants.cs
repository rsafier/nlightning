using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Offers.Constants;

/// <summary>
/// Constants of BOLT 12 (<c>12-offer-encoding.md</c>): string prefixes, signature tags, TLV ranges and defaults.
/// </summary>
[ExcludeFromCodeCoverage]
public static class Bolt12Constants
{
    /// <summary>
    /// BOLT 12 "Encoding": the human-readable part of an offer string.
    /// </summary>
    public const string OfferHrp = "lno";

    /// <summary>
    /// BOLT 12 "Encoding": the human-readable part of an invoice_request string (a refund; parsed, never sent).
    /// </summary>
    public const string InvoiceRequestHrp = "lnr";

    /// <summary>
    /// CLN's prefix for an invoice string. The spec defines none (invoices travel only in onion messages); accepted on
    /// input only.
    /// </summary>
    public const string InvoiceHrp = "lni";

    /// <summary>
    /// BOLT 12 "Signature Calculation": the tag of an invoice_request's signature,
    /// <c>"lightning" || messagename || fieldname</c>.
    /// </summary>
    public const string InvoiceRequestSignatureTag = "lightninginvoice_requestsignature";

    /// <summary>
    /// BOLT 12 "Signature Calculation": the tag of an invoice's signature.
    /// </summary>
    public const string InvoiceSignatureTag = "lightninginvoicesignature";

    /// <summary>
    /// BOLT 12 "Signature Calculation": the tag of a Merkle leaf, <c>H("LnLeaf", tlv)</c>.
    /// </summary>
    public const string MerkleLeafTag = "LnLeaf";

    /// <summary>
    /// BOLT 12 "Signature Calculation": the tag prefix of a Merkle nonce leaf, <c>H("LnNonce" || first_tlv, type)</c>.
    /// </summary>
    public const string MerkleNonceTag = "LnNonce";

    /// <summary>
    /// BOLT 12 "Signature Calculation": the tag of a Merkle inner node, <c>H("LnBranch", lesser || greater)</c>.
    /// </summary>
    public const string MerkleBranchTag = "LnBranch";

    /// <summary>
    /// The first TLV type of the signature range (BOLT 12: types 240 to 1000 inclusive are signature elements and are
    /// left out of the Merkle tree).
    /// </summary>
    public const ulong SignatureRangeStart = 240;

    /// <summary>
    /// The last TLV type of the signature range (inclusive).
    /// </summary>
    public const ulong SignatureRangeEnd = 1000;

    /// <summary>
    /// A BIP-340 signature's length (the <c>signature</c> field, type 240).
    /// </summary>
    public const int SignatureLength = 64;

    /// <summary>
    /// BOLT 12 "Invoices": the <c>invoice_relative_expiry</c> a reader assumes when the field is absent, in seconds.
    /// </summary>
    public const uint DefaultInvoiceRelativeExpirySeconds = 7200;

    /// <summary>
    /// The length of the random <c>offer_metadata</c> of our own offers (§3.7 of the BOLT 12 plan).
    /// </summary>
    public const int OurOfferMetadataLength = 16;

    /// <summary>
    /// The length of the random <c>invreq_metadata</c> of our own invoice_requests (BOLT 12 plan D3; the payer key is
    /// derived from it).
    /// </summary>
    public const int OurInvoiceRequestMetadataLength = 32;
}