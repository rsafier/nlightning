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
    /// <c>max_cltv_expiry</c>, so a payment made just before the invoice expires still fits, and so does the random
    /// delta a payer adds to the final expiry to hide the recipient's position (NL-719: Eclair adds 150 to 350 blocks,
    /// <c>eclair.send.recipient-final-expiry</c>; with the former 144 every Eclair payment over our paths was refused as
    /// above <c>max_cltv_expiry</c>). Default 1,008 (a week); BOLT 4 lets the recipient choose it.
    /// </summary>
    public uint PathLifetimeMarginBlocks { get; set; } = DefaultPathLifetimeMarginBlocks;

    /// <summary>The default of <see cref="PathLifetimeMarginBlocks"/>.</summary>
    public const uint DefaultPathLifetimeMarginBlocks = 1_008;

    /// <summary>
    /// How often <see cref="ExpiredBolt12InvoicePruner"/> deletes expired unpaid BOLT 12 invoices (NL-448); zero turns
    /// the pruning off.
    /// </summary>
    public TimeSpan ExpiredInvoicePruneInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The most expired invoices deleted in one save; a round saves batches until one comes back short, at most
    /// <see cref="ExpiredBolt12InvoicePruner.MaxBatchesPerRound"/> of them.
    /// </summary>
    public int ExpiredInvoicePruneBatchSize { get; set; } = 500;

    /// <summary>
    /// How long past its expiry an unpaid BOLT 12 invoice is kept before <see cref="ExpiredBolt12InvoicePruner"/>
    /// deletes it (NL-448 review). The final hop checks the expiry only when each HTLC arrives, so an HTLC set held
    /// across the expiry (at most <c>Node:Switch:MppTimeout</c>), or an HTLC between its check and the fulfill's save,
    /// still settles the invoice; the pruner never uses less than the MPP timeout. Default one hour.
    /// </summary>
    public TimeSpan ExpiredInvoicePruneGrace { get; set; } = TimeSpan.FromHours(1);

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
        if (ExpiredInvoicePruneInterval < TimeSpan.Zero)
            errors.Add($"{SectionName}:{nameof(ExpiredInvoicePruneInterval)} must not be negative.");
        if (ExpiredInvoicePruneBatchSize < 1)
            errors.Add($"{SectionName}:{nameof(ExpiredInvoicePruneBatchSize)} must be positive.");
        if (ExpiredInvoicePruneGrace < TimeSpan.Zero)
            errors.Add($"{SectionName}:{nameof(ExpiredInvoicePruneGrace)} must not be negative.");
        return errors;
    }
}