namespace NLightning.Domain.Node.Options;

using Money;
using Payments.Policies;

/// <summary>
/// Our forwarding policy (what we would announce in <c>channel_update</c> and what invoice route hints to us must use)
/// and the invoice defaults. Bound from the <c>Node:Routing</c> configuration section (it is
/// <see cref="NodeOptions.Routing"/>).
/// </summary>
/// <remarks>
/// One policy applies to every channel for now. The defaults follow the ABCD roadmap §4.6: 1000 msat + 1 ppm,
/// <c>cltv_expiry_delta</c> 40 (BOLT 2 calls at least 34 reasonable; LND uses 80), invoice
/// <c>min_final_cltv_expiry_delta</c> 40, and HTLCs expiring at most 2016 blocks ahead.
/// Call <see cref="GetValidationErrors"/> (through <see cref="NodeOptions.GetValidationErrors"/>) at startup.
/// </remarks>
public class RoutingOptions
{
    /// <summary>
    /// BOLT 2 "Risks With HTLC Timeouts": a <c>cltv_expiry_delta</c> of at least 34 (<c>3R+2G+2S</c> with R=2, G=2,
    /// S=12) is reasonable. We refuse to run with less.
    /// </summary>
    public const ushort MinimumCltvExpiryDelta = 34;

    /// <summary>
    /// BOLT 2: a fulfilled incoming HTLC must be claimed on chain <c>2R+G+S</c> blocks (18) before its
    /// <c>cltv_expiry</c>. It is also BOLT 11's default <c>min_final_cltv_expiry_delta</c>.
    /// </summary>
    public const ushort MinimumFinalCltvExpiryDelta = 18;

    /// <summary>
    /// BOLT 4 <c>max_htlc_cltv</c> is not fixed by the spec; LND and CLN use 2016 blocks.
    /// </summary>
    public const uint DefaultMaxCltvExpiryDistance = 2016;

    /// <summary>
    /// <c>fee_base_msat</c>: the fixed part of our forwarding fee, in msat. A <c>u32</c>, as in BOLT 7
    /// <c>channel_update</c> and BOLT 11 <c>r</c> route hints, so every configured value can be advertised.
    /// </summary>
    public uint FeeBaseMsat { get; set; } = 1_000;

    /// <summary>
    /// <c>fee_proportional_millionths</c>: the proportional part of our forwarding fee.
    /// </summary>
    public uint FeeProportionalMillionths { get; set; } = 1;

    /// <summary>
    /// <c>cltv_expiry_delta</c>: the blocks we require between an incoming HTLC's <c>cltv_expiry</c> and the outgoing
    /// <c>outgoing_cltv_value</c> (BOLT 4: <c>cltv_expiry - cltv_expiry_delta &gt;= outgoing_cltv_value</c>, else
    /// <c>incorrect_cltv_expiry</c>). At least <see cref="MinimumCltvExpiryDelta"/>.
    /// </summary>
    public ushort CltvExpiryDelta { get; set; } = 40;

    /// <summary>
    /// BOLT 4 <c>max_htlc_cltv</c>: an HTLC whose <c>cltv_expiry</c> is more than this many blocks ahead of the
    /// current height fails with <c>expiry_too_far</c>.
    /// </summary>
    public uint MaxCltvExpiryDistance { get; set; } = DefaultMaxCltvExpiryDistance;

    /// <summary>
    /// BOLT 4 <c>expiry_too_soon</c>: we refuse to forward when the outgoing HTLC would expire within this many blocks
    /// of the current height, because we could not safely claim the incoming one on chain in time.
    /// </summary>
    public ushort ExpiryTooSoonBlocks { get; set; } = MinimumFinalCltvExpiryDelta;

    /// <summary>
    /// BOLT 11 <c>c</c> (<c>min_final_cltv_expiry_delta</c>) of the invoices we create. At least
    /// <see cref="MinimumFinalCltvExpiryDelta"/>.
    /// </summary>
    public ushort InvoiceMinFinalCltvExpiry { get; set; } = 40;

    /// <summary>
    /// The expiry, in seconds, of the invoices we create when the caller does not choose one (BOLT 11 default 3600).
    /// </summary>
    public uint InvoiceExpirySeconds { get; set; } = 3_600;

    /// <summary>
    /// <c>htlc_minimum_msat</c> of our forwarding policy: an outgoing HTLC below it fails with
    /// <c>amount_below_minimum</c>. The channel's own negotiated minimum still applies on top.
    /// </summary>
    public ulong HtlcMinimumMsat { get; set; } = 1_000;

    /// <summary>
    /// <c>htlc_maximum_msat</c> of our forwarding policy, or null for "the channel's limits only". An outgoing HTLC
    /// above it fails with <c>temporary_channel_failure</c>.
    /// </summary>
    public ulong? HtlcMaximumMsat { get; set; }

    /// <summary>
    /// Our forwarding fee for <paramref name="amountToForward"/> (BOLT 7 "HTLC Fees").
    /// </summary>
    public LightningMoney CalculateFee(LightningMoney amountToForward) =>
        ForwardingFee.Calculate(FeeBaseMsat, FeeProportionalMillionths, amountToForward);

    /// <summary>
    /// Returns every configuration error; empty when the options are valid.
    /// </summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();

        if (CltvExpiryDelta < MinimumCltvExpiryDelta)
            errors.Add($"Routing:{nameof(CltvExpiryDelta)} is {CltvExpiryDelta}; BOLT 2 requires at least "
                     + $"{MinimumCltvExpiryDelta} blocks.");

        if (MaxCltvExpiryDistance < CltvExpiryDelta)
            errors.Add($"Routing:{nameof(MaxCltvExpiryDistance)} ({MaxCltvExpiryDistance}) must be at least "
                     + $"{nameof(CltvExpiryDelta)} ({CltvExpiryDelta}).");

        if (InvoiceMinFinalCltvExpiry < MinimumFinalCltvExpiryDelta)
            errors.Add($"Routing:{nameof(InvoiceMinFinalCltvExpiry)} is {InvoiceMinFinalCltvExpiry}; it must be at "
                     + $"least {MinimumFinalCltvExpiryDelta} blocks.");

        if (MaxCltvExpiryDistance < InvoiceMinFinalCltvExpiry)
            errors.Add($"Routing:{nameof(MaxCltvExpiryDistance)} ({MaxCltvExpiryDistance}) must be at least "
                     + $"{nameof(InvoiceMinFinalCltvExpiry)} ({InvoiceMinFinalCltvExpiry}).");

        if (ExpiryTooSoonBlocks == 0)
            errors.Add($"Routing:{nameof(ExpiryTooSoonBlocks)} must be positive.");

        if (ExpiryTooSoonBlocks >= CltvExpiryDelta)
            errors.Add($"Routing:{nameof(ExpiryTooSoonBlocks)} ({ExpiryTooSoonBlocks}) must be below "
                     + $"{nameof(CltvExpiryDelta)} ({CltvExpiryDelta}).");

        if (InvoiceExpirySeconds == 0)
            errors.Add($"Routing:{nameof(InvoiceExpirySeconds)} must be positive.");

        if (HtlcMaximumMsat is { } maximum && maximum < HtlcMinimumMsat)
            errors.Add($"Routing:{nameof(HtlcMaximumMsat)} ({maximum}) must be at least {nameof(HtlcMinimumMsat)} "
                     + $"({HtlcMinimumMsat}).");

        return errors;
    }
}