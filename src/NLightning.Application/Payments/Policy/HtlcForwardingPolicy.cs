using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Policy;

using Channels.RoutingPolicies;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.Policies;
using Domain.Protocol.Onion.Enums;

/// <summary>
/// Our forwarding policy (ONION M4-T4): <see cref="IForwardingPolicy"/> over the outgoing channel's routing policy (its
/// <c>setchannelpolicy</c> override where set, <see cref="NodeOptions.Routing"/> elsewhere; wave sp1 lane SP1-G).
/// </summary>
/// <remarks>
/// <para>The checks and their order are the ones documented on <see cref="IForwardingPolicy"/> (BOLT 4 "Failure
/// Messages", forwarding node): unknown channel → <c>unknown_next_peer</c>; unusable channel →
/// <c>temporary_channel_failure</c>; below the channel's or our <c>htlc_minimum_msat</c> →
/// <c>amount_below_minimum</c>; fee below the BOLT 7 formula (<see cref="ForwardingFee"/>), or inside a blinded route a
/// <c>payment_relay</c> below our fee base or rate → <c>fee_insufficient</c>;
/// <c>cltv_expiry - cltv_expiry_delta &lt; outgoing_cltv_value</c> → <c>incorrect_cltv_expiry</c>;
/// <c>outgoing_cltv_value &lt;= height + ExpiryTooSoonBlocks</c> → <c>expiry_too_soon</c>;
/// <c>cltv_expiry &gt; height + MaxCltvExpiryDistance</c> → <c>expiry_too_far</c>; above our
/// <c>htlc_maximum_msat</c> or what the channel can send → <c>temporary_channel_failure</c>.</para>
/// <para>The fee, <c>cltv_expiry_delta</c>, <c>htlc_minimum_msat</c> and <c>htlc_maximum_msat</c> are the outgoing
/// channel's (BOLT 7: the <c>channel_update</c> of the channel an HTLC leaves on sets what the forwarding node charges
/// and accepts), read from the optional <see cref="IChannelPolicyProvider"/> on every call; without one they are
/// <see cref="NodeOptions.Routing"/>'s. <c>ExpiryTooSoonBlocks</c> and <c>MaxCltvExpiryDistance</c> stay node-wide.
/// </para>
/// <para>"Within <c>ExpiryTooSoonBlocks</c>" is inclusive, as in LND (<c>outgoing - delta &lt;= height</c>): an
/// outgoing HTLC that expires exactly <c>ExpiryTooSoonBlocks</c> blocks from now is refused.</para>
/// <para>The options are read on every call, so a reloaded <see cref="IOptions{TOptions}"/> value applies.</para>
/// </remarks>
public sealed class HtlcForwardingPolicy : IForwardingPolicy
{
    private readonly IChannelPolicyProvider? _channelPolicyProvider;
    private readonly IOptions<NodeOptions> _nodeOptions;

    public HtlcForwardingPolicy(IOptions<NodeOptions> nodeOptions, IChannelPolicyProvider? channelPolicyProvider = null)
    {
        _nodeOptions = nodeOptions;
        _channelPolicyProvider = channelPolicyProvider;
    }

    /// <inheritdoc />
    public ForwardingDecision Evaluate(ForwardingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var routing = _nodeOptions.Value.Routing;

        if (request.OutgoingChannel is not { } channel)
            return ForwardingDecision.Fail(FailureCode.UnknownNextPeer);

        if (!channel.IsUsable)
            return ForwardingDecision.Fail(FailureCode.TemporaryChannelFailure);

        var policy = _channelPolicyProvider?.GetConfiguredPolicy(channel.ChannelId)
                  ?? ConfiguredChannelPolicy.From(routing, null);

        var amountToForwardMsat = request.AmountToForward.MilliSatoshi;
        var htlcMinimumMsat = Math.Max(channel.HtlcMinimum.MilliSatoshi, policy.HtlcMinimumMsat);
        if (amountToForwardMsat < htlcMinimumMsat)
            return ForwardingDecision.AmountBelowMinimum(amountToForwardMsat);

        var incomingAmountMsat = request.IncomingAmount.MilliSatoshi;
        if (request.BlindedRelay is { } relay)
        {
            // Inside a blinded route the recipient set our fee (BOLT 4 payment_relay): it must be at least our policy
            if (relay.FeeBaseMsat < policy.FeeBaseMsat
             || relay.FeeProportionalMillionths < policy.FeeProportionalMillionths)
                return ForwardingDecision.FeeInsufficient(incomingAmountMsat);
        }
        else if (!ForwardingFee.PaysSufficientFee(policy.FeeBaseMsat, policy.FeeProportionalMillionths,
                                                  incomingAmountMsat, amountToForwardMsat))
        {
            return ForwardingDecision.FeeInsufficient(incomingAmountMsat);
        }

        // cltv_expiry - cltv_expiry_delta >= outgoing_cltv_value, without underflow
        if ((ulong)request.IncomingCltvExpiry < (ulong)request.OutgoingCltvValue + policy.CltvExpiryDelta)
            return ForwardingDecision.IncorrectCltvExpiry(request.OutgoingCltvValue);

        if ((ulong)request.OutgoingCltvValue <= (ulong)request.CurrentBlockHeight + routing.ExpiryTooSoonBlocks)
            return ForwardingDecision.Fail(FailureCode.ExpiryTooSoon);

        if ((ulong)request.IncomingCltvExpiry > (ulong)request.CurrentBlockHeight + routing.MaxCltvExpiryDistance)
            return ForwardingDecision.Fail(FailureCode.ExpiryTooFar);

        if (policy.HtlcMaximumMsat is { } htlcMaximumMsat && amountToForwardMsat > htlcMaximumMsat)
            return ForwardingDecision.Fail(FailureCode.TemporaryChannelFailure);

        if (amountToForwardMsat > channel.AvailableToSend.MilliSatoshi)
            return ForwardingDecision.Fail(FailureCode.TemporaryChannelFailure);

        return ForwardingDecision.Forward;
    }
}