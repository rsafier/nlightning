namespace NLightning.Domain.Node.Options;

/// <summary>
/// Spontaneous payments (keysend: the payer picks the preimage and sends it in the final hop's
/// <c>keysend_preimage</c> record, type 5482373484). Bound from the <c>Node:Keysend</c> configuration section (it is
/// <see cref="NodeOptions.Keysend"/>).
/// </summary>
/// <remarks>
/// <para>Receiving is on by default (LND needs <c>--accept-keysend</c>, CLN accepts by default). Accepting one only adds
/// money to our side of a channel: it needs no invoice, reveals only the payer's own preimage and cannot be used to
/// take funds, so the default is CLN's. Turn it off to fail every keysend HTLC with
/// <c>incorrect_or_unknown_payment_details</c>.</para>
/// <para>No feature bit is involved: keysend has no standard one (LND and CLN advertise nothing for it; LND's
/// <c>--accept-keysend</c> changes no bit), so a payer just tries.</para>
/// </remarks>
public class KeysendOptions
{
    /// <summary>
    /// The lowest <see cref="MinFinalCltvExpiryDelta"/> accepted (a block or two of slack under BOLT 11's default 18,
    /// for payers that send exactly 18 while a block arrives).
    /// </summary>
    public const ushort MinimumFinalCltvExpiryDelta = 12;

    /// <summary>
    /// Accept keysend payments.
    /// </summary>
    public bool Accept { get; set; } = true;

    /// <summary>
    /// Receiving: the fewest blocks between the current height and a keysend HTLC's <c>cltv_expiry</c> (the
    /// <c>min_final_cltv_expiry_delta</c> of the invoice record we create for it). The payer chose its final CLTV
    /// without knowing ours, so this is lenient: BOLT 11's default of 18.
    /// </summary>
    public ushort MinFinalCltvExpiryDelta { get; set; } = 18;

    /// <summary>
    /// Sending: the final CLTV delta of our keysend payments (there is no invoice to give one); LND's default 40.
    /// </summary>
    public ushort FinalCltvExpiryDelta { get; set; } = 40;

    /// <summary>
    /// How long the invoice record of a keysend HTLC we could not settle stays usable for a retry with the same
    /// preimage, in seconds.
    /// </summary>
    public uint RecordExpirySeconds { get; set; } = 86_400;

    /// <summary>
    /// The configuration errors, empty when valid (<see cref="NodeOptions.GetValidationErrors"/>).
    /// </summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (MinFinalCltvExpiryDelta < MinimumFinalCltvExpiryDelta)
            errors.Add($"Keysend:{nameof(MinFinalCltvExpiryDelta)} is {MinFinalCltvExpiryDelta}; it must be at least "
                     + $"{MinimumFinalCltvExpiryDelta} blocks.");
        if (FinalCltvExpiryDelta < RoutingOptions.MinimumFinalCltvExpiryDelta)
            errors.Add($"Keysend:{nameof(FinalCltvExpiryDelta)} is {FinalCltvExpiryDelta}; it must be at least "
                     + $"{RoutingOptions.MinimumFinalCltvExpiryDelta} blocks.");
        if (RecordExpirySeconds == 0)
            errors.Add($"Keysend:{nameof(RecordExpirySeconds)} must be positive.");

        return errors;
    }
}