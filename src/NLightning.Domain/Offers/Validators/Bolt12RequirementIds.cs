using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Offers.Validators;

/// <summary>
/// The BOLT 12 requirement rows (<c>docs/agents/BOLT12_PLAN.md</c> §1.5-1.8) a <see cref="Bolt12Violation"/> names.
/// </summary>
[ExcludeFromCodeCoverage]
public static class Bolt12RequirementIds
{
    /// <summary>
    /// The string format: hrp, separator, bech32 characters, padding (BOLT 12 "Encoding").
    /// </summary>
    public const string Encoding = "B12-ENC-01";

    /// <summary>
    /// Case and <c>+</c> continuation rules of a BOLT 12 string.
    /// </summary>
    public const string Continuation = "B12-ENC-02";

    /// <summary>
    /// The TLV stream: BOLT 1 order, minimal encoding, lengths, unknown even types, and each known field's value
    /// format (UTF-8, points, <c>blinded_path</c>, lengths).
    /// </summary>
    public const string TlvStream = "B12-ENC-03";

    /// <summary>
    /// A TLV type outside the message's ranges.
    /// </summary>
    public const string TlvRange = "B12-ENC-04";

    /// <summary>
    /// Signatures: exactly one <c>signature</c> in an invoice_request or invoice, and no other signature element.
    /// </summary>
    public const string Signature = "B12-SIG-03";

    /// <summary>
    /// The offer reader's "MUST NOT respond" rules.
    /// </summary>
    public const string OfferReader = "B12-OFR-03";

    /// <summary>
    /// The invoice_request reader's reject rules that need no offer or signature check.
    /// </summary>
    public const string InvoiceRequestReader = "B12-IRQ-02";

    /// <summary>
    /// The invoice_request reader's amount, quantity, chain and <c>bip_353</c> rules.
    /// </summary>
    public const string InvoiceRequestAmounts = "B12-IRQ-04";

    /// <summary>
    /// The invoice reader's reject rules on the invoice alone.
    /// </summary>
    public const string InvoiceReader = "B12-INV-03";

    /// <summary>
    /// The invoice reader's rules against the invoice_request it answers.
    /// </summary>
    public const string InvoiceMatchesRequest = "B12-INV-04";
}