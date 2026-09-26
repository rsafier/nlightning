namespace NLightning.Application.Payments.Send;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Commitments;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Routing;

/// <summary>
/// Decides, from one failed part, whether the payment may be sent again and what the next routes must avoid or change
/// (BOLT 4 "Receiving Failure Codes", NL-270). It writes what it learns into the payment's
/// <see cref="RouteConstraints"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>The payee: <c>mpp_timeout</c> is retried as is; a PERM failure or one this node does not understand stops
///   the payment; any other (e.g. <c>final_incorrect_cltv_expiry</c>) is retried, with more CLTV for the CLTV one.</item>
///   <item>An intermediate hop with the NODE bit: its node is avoided.</item>
///   <item>An intermediate hop's channel failure (its outgoing channel, the next hop's incoming one):
///   <c>fee_insufficient</c>, <c>incorrect_cltv_expiry</c> and <c>amount_below_minimum</c> use the hop's signed
///   <c>channel_update</c> (right chain, the channel of the failure, the hop's direction, a valid signature by the hop,
///   not disabled, newer than one already used) as that channel's policy for this payment, else avoid the channel;
///   <c>expiry_too_soon</c> adds <see cref="PaymentSendOptions.ExpiryTooSoonExtraBlocks"/> to the final CLTV (and uses
///   the update); <c>temporary_channel_failure</c> bounds what the channel may forward to less than the failed HTLC
///   (and uses the update); every other channel failure (PERM ones, <c>unknown_next_peer</c>,
///   <c>channel_disabled</c>, BADONION from downstream) avoids the channel.</item>
///   <item>An error no hop authenticated, or <c>update_fail_malformed_htlc</c> from our peer: our channel of that part is
///   avoided. An HTLC timed out on chain: that channel is closed and avoided.</item>
/// </list>
/// Retries are bounded by the caller (attempts, fee limit, timeout).
/// </remarks>
internal sealed class PaymentRetryPolicy
{
    private readonly ILightningSigner _lightningSigner;
    private readonly ChainHash _chainHash;
    private readonly uint _expiryTooSoonExtraBlocks;

    public PaymentRetryPolicy(ILightningSigner lightningSigner, ChainHash chainHash, uint expiryTooSoonExtraBlocks)
    {
        _lightningSigner = lightningSigner;
        _chainHash = chainHash;
        _expiryTooSoonExtraBlocks = expiryTooSoonExtraBlocks;
    }

    /// <summary>
    /// Learns from the failure of <paramref name="part"/> and says whether the payment may be retried.
    /// </summary>
    /// <param name="part">The failed part.</param>
    /// <param name="removalKind">How the HTLC was removed.</param>
    /// <param name="interpretation">The decrypted and interpreted error onion (for <see cref="HtlcRemovalKind.Fail"/>).
    /// </param>
    /// <param name="constraints">The payment's constraints, updated in place.</param>
    /// <returns>Whether to retry, and a short note of what was learnt (for the failure reason and logs).</returns>
    public (bool Retry, string Note) Decide(PaymentPart part, HtlcRemovalKind removalKind,
                                            FailureInterpretation? interpretation, RouteConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(constraints);

        switch (removalKind)
        {
            case HtlcRemovalKind.FailMalformed:
                constraints.ExcludedLocalChannels.Add(part.Channel.ChannelId);
                return (true, $"our channel {part.Channel.ShortChannelId} is avoided");
            case HtlcRemovalKind.OnchainTimeout:
                constraints.ExcludedLocalChannels.Add(part.Channel.ChannelId);
                return (true, $"our channel {part.Channel.ShortChannelId} closed on chain");
        }

        if (interpretation is null)
            return (false, "the error could not be read");

        if (!interpretation.IsAttributed)
        {
            constraints.ExcludedLocalChannels.Add(part.Channel.ChannelId);
            return (true, $"no hop authenticated the error; our channel {part.Channel.ShortChannelId} is avoided");
        }

        var hops = part.Route.Hops;
        var index = interpretation.ErringHopIndex!.Value;
        if (interpretation.IsFinalNode)
        {
            if (interpretation.Code == FailureCode.MppTimeout)
                return (true, "the payee timed the parts out");
            if (!interpretation.ShouldRetry)
                return (false, "permanent failure from the payee");
            if (interpretation.Code == FailureCode.FinalIncorrectCltvExpiry)
                constraints.ExtraCltvDelta += _expiryTooSoonExtraBlocks;
            return (true, "the payee may accept a new attempt");
        }

        var node = hops[index].NodeId;
        if (interpretation.IsNodeFailure)
        {
            constraints.ExcludedNodes.Add(node);
            return (true, $"node {node} is avoided");
        }

        // The failure is about the hop's outgoing channel
        var shortChannelId = hops[index].OutgoingShortChannelId!.Value;
        var nextNode = hops[index + 1].NodeId;
        switch (interpretation.Code)
        {
            case FailureCode.FeeInsufficient or FailureCode.IncorrectCltvExpiry or FailureCode.AmountBelowMinimum:
                if (TryUsePolicy(interpretation, shortChannelId, node, nextNode, constraints, out var why))
                    return (true, $"channel {shortChannelId} policy updated from the hop's channel_update");

                constraints.ExcludedChannels.Add(shortChannelId);
                return (true, $"channel {shortChannelId} is avoided ({why})");
            case FailureCode.ExpiryTooSoon:
                constraints.ExtraCltvDelta += _expiryTooSoonExtraBlocks;
                TryUsePolicy(interpretation, shortChannelId, node, nextNode, constraints, out _);
                return (true, $"{constraints.ExtraCltvDelta} more blocks of CLTV");
            case FailureCode.TemporaryChannelFailure:
                TryUsePolicy(interpretation, shortChannelId, node, nextNode, constraints, out _);
                var forwarded = hops[index].AmountToForward.MilliSatoshi;
                constraints.BoundChannelLiquidity(shortChannelId, forwarded);
                return (true, $"channel {shortChannelId} could not forward {forwarded} msat");
            default:
                constraints.ExcludedChannels.Add(shortChannelId);
                return (true, $"channel {shortChannelId} is avoided");
        }
    }

    /// <summary>
    /// Uses the <c>channel_update</c> of an UPDATE failure as the channel's policy for this payment (BOLT 4: the origin
    /// MAY, when it is valid and newer than the one it routed with). A disabled channel is avoided instead.
    /// </summary>
    private bool TryUsePolicy(FailureInterpretation interpretation, ShortChannelId shortChannelId,
                              CompactPubKey erringNode, CompactPubKey nextNode, RouteConstraints constraints,
                              out string reason)
    {
        if (interpretation.ChannelUpdate is not { } payload || !ChannelUpdatePayload.TryParse(payload.Span,
                                                                                               out var update))
        {
            reason = "no channel_update";
            return false;
        }

        if (update.ChainHash != _chainHash)
        {
            reason = "channel_update for another chain";
            return false;
        }

        if (update.ShortChannelId != shortChannelId)
        {
            reason = $"channel_update for another channel ({update.ShortChannelId})";
            return false;
        }

        // BOLT 7: direction 0 is the channel end with the lower node id
        var erringIsNode2 = ((ReadOnlySpan<byte>)erringNode).SequenceCompareTo(nextNode) > 0;
        if (update.Direction != erringIsNode2)
        {
            reason = "channel_update for the other direction";
            return false;
        }

        if (!_lightningSigner.VerifyNodeMessage(update.GetSignatureHash(), update.Signature, erringNode))
        {
            reason = "channel_update with an invalid signature";
            return false;
        }

        if (update.IsDisabled)
        {
            constraints.ExcludedChannels.Add(shortChannelId);
            reason = "channel disabled";
            return false;
        }

        if (constraints.PolicyOverrides.TryGetValue(shortChannelId, out var current)
         && update.Timestamp <= current.Timestamp)
        {
            reason = "channel_update not newer than the one already used";
            return false;
        }

        constraints.PolicyOverrides[shortChannelId] =
            new HintChannelPolicy(update.FeeBaseMsat, update.FeeProportionalMillionths, update.CltvExpiryDelta,
                                  update.HtlcMinimumMsat, update.HtlcMaximumMsat, update.Timestamp);
        reason = string.Empty;
        return true;
    }
}