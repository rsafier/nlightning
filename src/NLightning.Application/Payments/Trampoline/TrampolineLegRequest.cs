namespace NLightning.Application.Payments.Trampoline;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node;
using Domain.Protocol.Onion.Models;

/// <summary>
/// The outgoing leg of a trampoline relay (NL-875, plan <c>TRAMPOLINE_PLAN.md</c> TR3): one payment of
/// <see cref="Amount"/> to the next trampoline node (or along the recipient's blinded paths), whose HTLCs carry
/// <c>HtlcOrigin.Trampoline(PaymentHash)</c> and whose <c>Payments</c> row is marked <c>IsTrampolineRelay</c>.
/// </summary>
/// <param name="PaymentHash">The relayed payment's hash (shared with the incoming parts).</param>
/// <param name="Amount">The trampoline payload's <c>amt_to_forward</c>: what the next node must receive in total.</param>
/// <param name="FinalCltvExpiry">The trampoline payload's <c>outgoing_cltv_value</c>: the absolute expiry the next node
/// must receive (its outer final hop's <c>outgoing_cltv_value</c>).</param>
/// <param name="MaxFirstHopCltvExpiry">The highest expiry our first outgoing HTLC may carry: the lowest incoming
/// part's expiry minus our plain forwarding <c>cltv_expiry_delta</c> (<c>Node:Routing</c>; our trampoline delta pays
/// for the route).</param>
/// <param name="MaxFee">The routing fee budget of the leg: incoming sum − <see cref="Amount"/> (our trampoline fee pays
/// for the route; we keep what it leaves).</param>
/// <param name="NextNodeId">The next trampoline node (<c>outgoing_node_id</c>, or the next node of a blinded trampoline
/// path); null when the leg pays <see cref="RecipientBlindedPaths"/>.</param>
/// <param name="NextTrampolinePacket">The peeled trampoline onion to put in the next node's outer final payload (TLV
/// 20); null when the leg pays <see cref="RecipientBlindedPaths"/>.</param>
/// <param name="NextPathKey">For a blinded trampoline path: the <c>current_path_key</c> to put in the next node's outer
/// final payload (TLV 12).</param>
/// <param name="RecipientBlindedPaths">TLV 22: pay the recipient along these blinded paths (no trampoline onion).</param>
/// <param name="RecipientFeatures">TLV 21: the recipient's features (e.g. <c>basic_mpp</c>).</param>
/// <param name="AllowMpp">Whether the leg may split: the next node is a trampoline, or the recipient supports
/// <c>basic_mpp</c>. A split leg uses a fresh random outer <c>payment_secret</c>.</param>
/// <param name="Deadline">Stop retrying at this time; the relay then fails its incoming parts.</param>
public sealed record TrampolineLegRequest(
    Hash PaymentHash,
    LightningMoney Amount,
    uint FinalCltvExpiry,
    uint MaxFirstHopCltvExpiry,
    LightningMoney MaxFee,
    CompactPubKey? NextNodeId,
    byte[]? NextTrampolinePacket,
    CompactPubKey? NextPathKey,
    IReadOnlyList<WireBlindedPaymentPath>? RecipientBlindedPaths,
    FeatureSet? RecipientFeatures,
    bool AllowMpp,
    DateTimeOffset Deadline);