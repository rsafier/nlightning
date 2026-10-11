using System.Security.Cryptography;

namespace NLightning.Application.Payments.Switch;

using Domain.Channels.Commitments;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Onion;

/// <summary>
/// The BOLT 2 / BOLT 4 rules for failing an incoming HTLC inside a blinded route (M5, NL-079), shared by every path
/// that fails an incoming HTLC outside the switch's own decisions (the dust-exposure decorator, the HTLC expiry
/// monitor):
/// <list type="bullet">
///   <item>the incoming <c>update_add_htlc</c> carried a <c>path_key</c>: <c>update_fail_malformed_htlc</c> with
///   <c>invalid_onion_blinding</c> and the onion's sha256, for any local or downstream error;</item>
///   <item>we are the introduction node of the route and not its final node (the payload carried
///   <c>current_path_key</c>): <c>update_fail_htlc</c> with our own <c>invalid_onion_blinding</c>;</item>
///   <item>otherwise the caller's failure unchanged.</item>
/// </list>
/// The introduction role is read from the onion itself, whatever <c>option_route_blinding</c> is set to now, so a
/// restart with the feature off never relays a plain error for a blinded forward made before it.
/// </summary>
public static class BlindedHtlcFailures
{
    /// <summary>
    /// Whether a processing <paramref name="result"/> (of an HTLC without a <c>path_key</c>) makes us the
    /// introduction node of a blinded route that is not its final node: a blinded forward, or the blinded refusal the
    /// processor gives when it cannot or will not read the recipient data.
    /// </summary>
    public static bool IsIntroductionForward(IncomingOnionResult result) =>
        result is IncomingOnionForward { Blinded.IsIntroduction: true }
            or IncomingOnionFailed { Failure.Code: FailureCode.InvalidOnionBlinding };

    /// <summary>
    /// Peels <paramref name="htlc"/>'s onion again (no replay check) to tell whether we are the introduction node of a
    /// blinded route that is not its final node. False for an HTLC with a <c>path_key</c> (not the introduction node)
    /// or without an onion.
    /// </summary>
    public static async Task<bool> IsIntroductionForwardAsync(IncomingOnionProcessor onionProcessor, HtlcRecord htlc)
    {
        ArgumentNullException.ThrowIfNull(onionProcessor);
        ArgumentNullException.ThrowIfNull(htlc);
        if (htlc.PathKey is not null || htlc.OnionRoutingPacket.IsEmpty)
            return false;

        return IsIntroductionForward(await onionProcessor.ProcessAsync(htlc.OnionRoutingPacket, htlc.PaymentHash,
                                                                       replayOwner: null));
    }

    /// <summary>
    /// Fails <paramref name="htlc"/> with <paramref name="failure"/> after applying the blinded-route rules: the
    /// malformed <c>invalid_onion_blinding</c> when it carried a <c>path_key</c>, our own <c>invalid_onion_blinding</c>
    /// in an error onion when <paramref name="isIntroductionForward"/>, else <paramref name="failure"/> in an error
    /// onion. Exceptions of <see cref="IChannelOperations"/> propagate.
    /// </summary>
    /// <returns>A short description of what was sent, for the caller's log.</returns>
    public static async Task<string> FailAsync(IChannelOperations channelOperations,
                                               IFailureOnionService failureOnionService, ChannelId channelId,
                                               HtlcRecord htlc, Secret sharedSecret, FailureMessage failure,
                                               bool isIntroductionForward, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelOperations);
        ArgumentNullException.ThrowIfNull(failureOnionService);
        ArgumentNullException.ThrowIfNull(htlc);

        if (await TryFailMalformedAsync(channelOperations, channelId, htlc, cancellationToken))
            return "update_fail_malformed_htlc invalid_onion_blinding";

        if (isIntroductionForward)
            failure = FailureMessage.InvalidOnionBlinding(Sha256Of(htlc.OnionRoutingPacket));

        await channelOperations.FailHtlcAsync(channelId, htlc.Id,
                                              failureOnionService.CreateErrorPacket(sharedSecret, failure),
                                              cancellationToken);
        return failure.Code.ToString();
    }

    /// <summary>
    /// Sends <c>update_fail_malformed_htlc</c> + <c>invalid_onion_blinding</c> when <paramref name="htlc"/> carried a
    /// <c>path_key</c> in its <c>update_add_htlc</c>; false (nothing sent) otherwise.
    /// </summary>
    public static async Task<bool> TryFailMalformedAsync(IChannelOperations channelOperations, ChannelId channelId,
                                                         HtlcRecord htlc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelOperations);
        ArgumentNullException.ThrowIfNull(htlc);
        if (htlc.PathKey is null)
            return false;

        await channelOperations.FailMalformedHtlcAsync(channelId, htlc.Id, FailureCode.InvalidOnionBlinding,
                                                       new Hash(Sha256Of(htlc.OnionRoutingPacket)), cancellationToken);
        return true;
    }

    private static byte[] Sha256Of(ReadOnlyMemory<byte> bytes) => SHA256.HashData(bytes.Span);
}