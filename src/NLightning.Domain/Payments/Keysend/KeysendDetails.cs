namespace NLightning.Domain.Payments.Keysend;

/// <summary>
/// The keysend side of an invoice record (a spontaneous payment we received, <c>InvoiceKind.Keysend</c>) or of one of
/// our payments (a spontaneous payment we sent): the custom records the payer attached to the final hop.
/// </summary>
/// <param name="CustomRecords">The records of type 65536 or more, ascending, without the keysend preimage itself.
/// </param>
public sealed record KeysendDetails(IReadOnlyList<CustomRecord> CustomRecords)
{
    /// <summary>No custom record.</summary>
    public static KeysendDetails Empty { get; } = new([]);
}