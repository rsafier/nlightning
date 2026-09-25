using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Policy;

using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.Policies;
using Domain.Protocol.Onion.Enums;

/// <summary>
/// Our forwarding policy (ONION M4-T4): <see cref="IForwardingPolicy"/> over <see cref="NodeOptions.Routing"/>.
/// </summary>
/// <remarks>
/// <para>The checks and their order are the ones documented on <see cref="IForwardingPolicy"/> (BOLT 4 "Failure
/// Messages", forwarding node): unknown channel → <c>unknown_next_peer</c>; unusable channel →
/// <c>temporary_channel_failure</c>; below the channel's or our <c>htlc_minimum_msat</c> →
/// <c>amount_below_minimum</c>; fee below the BOLT 7 formula (<see cref="ForwardingFee"/>) → <c>fee_insufficient</c>;
/// <c>cltv_expiry - cltv_expiry_delta &lt; outgoing_cltv_value</c> → <c>incorrect_cltv_expiry</c>;
/// <c>outgoing_cltv_value &lt;= height + ExpiryTooSoonBlocks</c> → <c>expiry_too_soon</c>;
/// <c>cltv_expiry &gt; height + MaxCltvExpiryDistance</c> → <c>expiry_too_far</c>; above our
/// <c>htlc_maximum_msat</c> or what the channel can send → <c>temporary_channel_failure</c>.</para>
/// <para>"Within <c>ExpiryTooSoonBlocks</c>" is inclusive, as in LND (<c>outgoing - delta &lt;= height</c>): an
/// outgoing HTLC that expires exactly <c>ExpiryTooSoonBlocks</c> blocks from now is refused.</para>
/// <para>The options are read on every call, so a reloaded <see cref="IOptions{TOptions}"/> value applies.</para>
/// </remarks>
public sealed class HtlcForwardingPolicy : IForwardingPolicy
{
    private readonly IOptions<NodeOptions> _nodeOptions;

    public HtlcForwardingPolicy(IOptions<NodeOptions> nodeOptions)
    {
        _nodeOptions = nodeOptions;
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

        var amountToForwardMsat = request.AmountToForward.MilliSatoshi;
        var htlcMinimumMsat = Math.Max(channel.HtlcMinimum.MilliSatoshi, routing.HtlcMinimumMsat);
        if (amountToForwardMsat < htlcMinimumMsat)
            return ForwardingDecision.AmountBelowMinimum(amountToForwardMsat);

        var incomingAmountMsat = request.IncomingAmount.MilliSatoshi;
        if (!ForwardingFee.PaysSufficientFee(routing.FeeBaseMsat, routing.FeeProportionalMillionths,
                                             incomingAmountMsat, amountToForwardMsat))
            return ForwardingDecision.FeeInsufficient(incomingAmountMsat);

        // cltv_expiry - cltv_expiry_delta >= outgoing_cltv_value, without underflow
        if ((ulong)request.IncomingCltvExpiry < (ulong)request.OutgoingCltvValue + routing.CltvExpiryDelta)
            return ForwardingDecision.IncorrectCltvExpiry(request.OutgoingCltvValue);

        if ((ulong)request.OutgoingCltvValue <= (ulong)request.CurrentBlockHeight + routing.ExpiryTooSoonBlocks)
            return ForwardingDecision.Fail(FailureCode.ExpiryTooSoon);

        if ((ulong)request.IncomingCltvExpiry > (ulong)request.CurrentBlockHeight + routing.MaxCltvExpiryDistance)
            return ForwardingDecision.Fail(FailureCode.ExpiryTooFar);

        if (routing.HtlcMaximumMsat is { } htlcMaximumMsat && amountToForwardMsat > htlcMaximumMsat)
            return ForwardingDecision.Fail(FailureCode.TemporaryChannelFailure);

        if (amountToForwardMsat > channel.AvailableToSend.MilliSatoshi)
            return ForwardingDecision.Fail(FailureCode.TemporaryChannelFailure);

        return ForwardingDecision.Forward;
    }
}