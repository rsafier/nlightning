namespace NLightning.Application.Payments.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;

/// <summary>
/// We are the final node of the onion: hand <see cref="Payload"/> to <c>FinalHopProcessor</c>.
/// </summary>
/// <param name="SharedSecret">The shared secret with the origin: the final-hop failure is created with it.</param>
/// <param name="Payload">The validated final hop payload: <c>amt_to_forward</c>, <c>outgoing_cltv_value</c> and
/// <c>payment_data</c> present, or, at the end of a blinded route, <c>amt_to_forward</c>,
/// <c>outgoing_cltv_value</c> and <c>total_amount_msat</c>.</param>
/// <param name="Blinded">Set when we are the final node of a blinded route (ONION M5): its <c>path_id</c> must be
/// ours.</param>
public sealed record IncomingOnionFinal(Secret SharedSecret, HopPayload Payload, IncomingBlindedHop? Blinded = null)
    : IncomingOnionResult
{
    /// <inheritdoc />
    public override Secret? SharedSecretOrNull => SharedSecret;
}