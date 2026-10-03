namespace NLightning.Domain.Payments.Keysend;

using Crypto.ValueObjects;
using Money;

/// <summary>
/// A spontaneous (keysend) payment to <paramref name="Destination"/>: no invoice, we pick the preimage and send it in
/// the final hop's <c>keysend_preimage</c> record (<c>IPaymentService.PayKeysendAsync</c>).
/// </summary>
/// <param name="Destination">The payee's node id.</param>
/// <param name="Amount">What the payee receives.</param>
public sealed record PayKeysendRequest(CompactPubKey Destination, LightningMoney Amount)
{
    /// <summary>
    /// Application records for the payee (types of 65536 or more, never the keysend preimage's 5482373484).
    /// </summary>
    public IReadOnlyList<CustomRecord> CustomRecords { get; init; } = [];
}