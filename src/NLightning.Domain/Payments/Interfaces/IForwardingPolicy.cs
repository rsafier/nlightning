namespace NLightning.Domain.Payments.Interfaces;

using Models;

/// <summary>
/// Decides whether a locked-in incoming HTLC may be forwarded under our <c>RoutingOptions</c> (ONION M4-T4,
/// implemented by <c>HtlcForwardingPolicy</c>).
/// </summary>
/// <remarks>
/// <para>Pure and synchronous: no I/O, no locks, no side effects. The switch calls it after the incoming add is
/// irrevocably committed (B2-FWD-01), the onion was peeled and its payload validated, and before any outgoing
/// HTLC is offered.</para>
/// <para>Checks, in this order (the first failure wins; BOLT 4 "Failure Messages" requirements for a forwarding
/// node):</para>
/// <list type="number">
///   <item>Unknown outgoing channel → <c>unknown_next_peer</c>.</item>
///   <item>Outgoing channel not usable → <c>temporary_channel_failure</c>.</item>
///   <item><c>amt_to_forward</c> below the channel's or our <c>htlc_minimum_msat</c> →
///   <c>amount_below_minimum</c> (reports the outgoing amount).</item>
///   <item>Incoming amount below <c>amt_to_forward + fee_base_msat + amt_to_forward * fee_proportional_millionths /
///   1000000</c> (BOLT 7, rounded down) → <c>fee_insufficient</c> (reports the incoming amount).</item>
///   <item><c>cltv_expiry - cltv_expiry_delta &lt; outgoing_cltv_value</c> → <c>incorrect_cltv_expiry</c> (reports
///   <c>outgoing_cltv_value</c>).</item>
///   <item><c>outgoing_cltv_value</c> within <c>RoutingOptions.ExpiryTooSoonBlocks</c> of the current height →
///   <c>expiry_too_soon</c>.</item>
///   <item><c>cltv_expiry</c> more than <c>RoutingOptions.MaxCltvExpiryDistance</c> blocks ahead →
///   <c>expiry_too_far</c>.</item>
///   <item><c>amt_to_forward</c> above <c>RoutingOptions.HtlcMaximumMsat</c> or above what the channel can send →
///   <c>temporary_channel_failure</c>.</item>
/// </list>
/// <para>A forward that passes may still be refused by the channel engine when it is offered
/// (<c>CommitmentRefusedException</c>); the switch maps that to <c>temporary_channel_failure</c>.</para>
/// </remarks>
public interface IForwardingPolicy
{
    ForwardingDecision Evaluate(ForwardingRequest request);
}