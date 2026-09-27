namespace NLightning.Application.Offers.Receive;

using Domain.Offers.Constants;

/// <summary>
/// Options of our BOLT 12 offers and of the invoices we answer their invoice_requests with (section
/// <see cref="SectionName"/>; BOLT 12 plan §3.7, D11).
/// </summary>
public sealed class OfferOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Offers";

    /// <summary>
    /// <c>invoice_relative_expiry</c> of our invoices, in seconds (BOLT 12: omitted when it is the default 7200).
    /// </summary>
    public uint InvoiceRelativeExpirySeconds { get; set; } = Bolt12Constants.DefaultInvoiceRelativeExpirySeconds;

    /// <summary>The most blinded payment paths in one invoice.</summary>
    public int MaxPaymentPaths { get; set; } = 3;

    /// <summary>The most <c>offer_paths</c> in one offer.</summary>
    public int MaxOfferPaths { get; set; } = 2;

    /// <summary>The most open, unexpired BOLT 12 invoices per offer (plan D11); over it we answer an
    /// <c>invoice_error</c>.</summary>
    public int MaxUnpaidInvoicesPerOffer { get; set; } = 1_000;

    /// <summary>The most open, unexpired BOLT 12 invoices over every offer (plan D11).</summary>
    public int MaxUnpaidInvoices { get; set; } = 10_000;

    /// <summary>How many invoice_requests per second one offer answers (plan §3.4: 5/s); more are dropped.</summary>
    public double InvoiceRequestsPerSecondPerOffer { get; set; } = 5;

    /// <summary>How many invoice_requests per second the node answers over every offer (plan §3.4: 20/s).</summary>
    public double InvoiceRequestsPerSecond { get; set; } = 20;

    /// <summary>
    /// Blocks added to the invoice's lifetime (its relative expiry at 10 minutes a block) for the paths'
    /// <c>max_cltv_expiry</c>, so a payment made just before the invoice expires still fits.
    /// </summary>
    public uint PathLifetimeMarginBlocks { get; set; } = 144;

    /// <summary>
    /// The configuration problems, or none.
    /// </summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (InvoiceRelativeExpirySeconds == 0)
            errors.Add($"{SectionName}:{nameof(InvoiceRelativeExpirySeconds)} must be positive.");
        if (MaxPaymentPaths is < 1 or > byte.MaxValue)
            errors.Add($"{SectionName}:{nameof(MaxPaymentPaths)} must be between 1 and 255.");
        if (MaxOfferPaths is < 1 or > byte.MaxValue)
            errors.Add($"{SectionName}:{nameof(MaxOfferPaths)} must be between 1 and 255.");
        if (MaxUnpaidInvoicesPerOffer < 1)
            errors.Add($"{SectionName}:{nameof(MaxUnpaidInvoicesPerOffer)} must be positive.");
        if (MaxUnpaidInvoices < 1)
            errors.Add($"{SectionName}:{nameof(MaxUnpaidInvoices)} must be positive.");
        if (!(InvoiceRequestsPerSecondPerOffer > 0))
            errors.Add($"{SectionName}:{nameof(InvoiceRequestsPerSecondPerOffer)} must be positive.");
        if (!(InvoiceRequestsPerSecond > 0))
            errors.Add($"{SectionName}:{nameof(InvoiceRequestsPerSecond)} must be positive.");
        return errors;
    }
}