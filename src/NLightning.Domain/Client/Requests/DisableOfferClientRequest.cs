namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;

/// <summary>
/// Disables one of our BOLT 12 offers (<c>disableoffer</c>): later invoice_requests for it get an
/// <c>invoice_error</c>; invoices already issued stay payable until they expire.
/// </summary>
public sealed class DisableOfferClientRequest
{
    public required Hash OfferId { get; init; }
}