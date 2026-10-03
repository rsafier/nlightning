namespace NLightning.Domain.Payments.Trampoline;

using Crypto.ValueObjects;
using Money;

/// <summary>
/// One hop of the trampoline onion of one of our own payments sent through trampoline nodes (payer side, NL-875): the
/// trampoline nodes and the payee, with the shared secret of the trampoline onion's layer, so a failure returned by a
/// trampoline node (the inner error onion) can be decrypted and attributed after a restart. Keyed by
/// (<see cref="PaymentHash"/>, <see cref="Attempt"/>, <see cref="HopIndex"/>): each attempt of the payment may build
/// its own trampoline route.
/// </summary>
/// <param name="PaymentHash">The payment's hash.</param>
/// <param name="Attempt">The attempt the trampoline onion was built for (0 first).</param>
/// <param name="HopIndex">The hop's position in the trampoline route (0 = the first trampoline node).</param>
/// <param name="NodeId">The trampoline node (or the payee, last).</param>
/// <param name="SharedSecret">The Sphinx shared secret of the hop's layer of the trampoline onion.</param>
/// <param name="Amount">The amount the hop forwards (its <c>amt_to_forward</c>).</param>
/// <param name="CltvExpiry">The hop's <c>outgoing_cltv_value</c>.</param>
public sealed record PaymentTrampolineHopModel(
    Hash PaymentHash,
    int Attempt,
    int HopIndex,
    CompactPubKey NodeId,
    Secret SharedSecret,
    LightningMoney Amount,
    uint CltvExpiry);