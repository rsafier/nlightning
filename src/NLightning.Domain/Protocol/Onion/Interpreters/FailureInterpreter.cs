namespace NLightning.Domain.Protocol.Onion.Interpreters;

using Enums;
using Extensions;
using Factories;
using Models;

/// <summary>
/// Origin-side interpretation of a decrypted failure onion (BOLT 4 "Receiving Failure Codes"): whether to fail the
/// payment or retry, and which node or channel of the route to avoid. Pure: it reads only its arguments.
/// </summary>
public static class FailureInterpreter
{
    /// <summary>
    /// Interprets the result of <see cref="Interfaces.IFailureOnionService.DecryptErrorPacket"/>.
    /// </summary>
    /// <remarks>
    /// <para>BOLT 4 rules:</para>
    /// <list type="bullet">
    /// <item>Final node: PERM → fail the payment; otherwise, if the code is understood and valid, the origin MAY
    /// retry (BOLT 4's MAY): this node retries every code but one, the NODE bit (e.g. the payee's
    /// <c>temporary_node_failure</c>): no route can avoid the payee, so the payment ends (NL-593, like LND's
    /// mission control). A final-node failure this node cannot parse is treated as not understood (fail the
    /// payment).</item>
    /// <item>Intermediate hop, NODE set: remove all channels of the erring node from consideration.</item>
    /// <item>Intermediate hop, NODE not set: the failure is about its outgoing channel (for BADONION codes, converted
    /// from <c>update_fail_malformed_htlc</c>, the onion it forwarded on that channel was rejected downstream).</item>
    /// <item>Intermediate hop, UPDATE set: the <c>channel_update</c>, if any, MAY be used to retry this payment only.</item>
    /// <item>Intermediate hop: then retry.</item>
    /// </list>
    /// <para>
    /// Trampoline (BOLTs PR 836): the final node of the route an origin builds to a trampoline node is that trampoline
    /// node, so its <c>temporary_trampoline_failure</c> (NODE|25) and <c>trampoline_fee_or_expiry_insufficient</c>
    /// (NODE|26) arrive as final-node failures. Both are temporary and retryable (the latter with the fee and CLTV delta
    /// it carries, <see cref="FailureMessage.TryGetTrampolinePolicy"/>), so they are the exception to the final-node
    /// NODE rule above; <c>unknown_next_trampoline</c> (PERM|27) is permanent and ends the payment like any PERM
    /// failure of the final node. From an intermediate hop all three follow the generic rules (25/26 exclude the node,
    /// 27 the outgoing channel).
    /// </para>
    /// <para>
    /// Not in BOLT 4, and so an implementation choice: an intermediate failure with no readable code is blamed on
    /// the erring node (it authenticated garbage) as a temporary node failure; an unattributable failure (no HMAC
    /// matched) blames nobody and allows a retry, so the caller must bound retries.
    /// </para>
    /// </remarks>
    /// <param name="decryptedFailure">The decrypted failure, or <c>null</c> if no hop's HMAC matched.</param>
    /// <param name="routeLength">The number of hops of the route the payment was sent on.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// If <paramref name="routeLength"/> is not positive or the erring hop is not on the route.
    /// </exception>
    public static FailureInterpretation Interpret(DecryptedFailure? decryptedFailure, int routeLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(routeLength);

        if (decryptedFailure is null)
            return new FailureInterpretation { ShouldRetry = true };

        var erringHop = decryptedFailure.ErringHopIndex;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(erringHop, routeLength, nameof(decryptedFailure));

        var code = decryptedFailure.Code;
        var message = decryptedFailure.Message;
        var isFinalNode = erringHop == routeLength - 1;
        var isPermanent = code?.IsPerm() ?? false;

        if (isFinalNode)
        {
            var isUnderstood = message is { IsKnownCode: true };
            var isNode = code?.IsNode() ?? false;
            var isRetryableTrampoline = code is FailureCode.TemporaryTrampolineFailure
                                                or FailureCode.TrampolineFeeOrExpiryInsufficient;
            // BOLT 4 leaves a non-permanent, understood final-node failure to the origin's MAY. A NODE-bit failure
            // at the payee cannot be routed around (every route ends at that node): retrying it re-sends the same
            // HTLC to the same node in a tight loop (NL-593, a drain's temporary_node_failure did), so it ends the
            // payment. The channel-level final codes (final_expiry_too_soon, final_incorrect_cltv_expiry,
            // final_incorrect_htlc_amount) and mpp_timeout carry no NODE bit and stay retryable. A trampoline node is
            // the final node of the route to it, and its temporary_trampoline_failure and
            // trampoline_fee_or_expiry_insufficient ask for a retry (the latter at its fee and CLTV delta, NL-875)
            return new FailureInterpretation
            {
                ErringHopIndex = erringHop,
                IsFinalNode = true,
                Code = code,
                Message = message,
                IsPermanent = isPermanent,
                IsNodeFailure = isNode,
                ShouldRetry = !isPermanent && isUnderstood && (!isNode || isRetryableTrampoline)
            };
        }

        // No readable code: the erring node authenticated a failure nobody can read, so blame the node itself
        var isNodeFailure = code?.IsNode() ?? true;

        ReadOnlyMemory<byte>? channelUpdate = null;
        if (!isNodeFailure && code.GetValueOrDefault().IsUpdate() && message?.ChannelUpdate is { } field
         && FailureChannelUpdateFactory.TryGetPayload(field, out var payload))
            channelUpdate = payload;

        return new FailureInterpretation
        {
            ErringHopIndex = erringHop,
            IsFinalNode = false,
            Code = code,
            Message = message,
            IsPermanent = isPermanent,
            IsNodeFailure = isNodeFailure,
            ShouldRetry = true,
            ExcludedNodeHopIndex = isNodeFailure ? erringHop : null,
            FailedChannelHopIndex = isNodeFailure ? null : erringHop + 1,
            ChannelUpdate = channelUpdate
        };
    }
}