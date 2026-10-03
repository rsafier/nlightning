using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.Switch;

using Domain.Channels.Commitments;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Onion;
using Trampoline;

/// <summary>
/// NL-897: how the failure paths outside the switch and the relay engine (the HTLC deadline monitor, the dust-exposure
/// decorator) fail an incoming HTLC that reached us as a trampoline node, so the payer reads the failure at the
/// trampoline layer (BOLTs PR 836 "Returning Errors", TR-R-14): the reason is created with the trampoline secret, then
/// obfuscated with the outer one (<see cref="TrampolineErrorPackets"/>, <c>attribution_data</c> on the outer layer only),
/// as <see cref="HtlcSwitch"/> and the relay engine create theirs.
/// </summary>
/// <remarks>
/// Only the outer secret is stored with an incoming HTLC, so the trampoline secret is rebuilt by peeling the stored
/// onion again without the replay check (as the switch's replays do), else, for a part of a trampoline relay, read
/// from its <c>TrampolineRelayParts</c> row. An HTLC that is not a trampoline HTLC yields no keys and is failed by the
/// caller exactly as before.
/// </remarks>
internal static class TrampolineHtlcFailures
{
    /// <summary>
    /// The trampoline failure keys a processing <paramref name="result"/> carries, or null when it is not a trampoline
    /// HTLC (a malformed or failed outer onion, a forward, an ordinary final hop).
    /// </summary>
    public static TrampolineFailureKeys? KeysFrom(IncomingOnionResult result) => result switch
    {
        IncomingOnionTrampolineResult trampoline => TrampolineFailureKeys.From(trampoline),
        IncomingOnionTrampolineFailed failed => new TrampolineFailureKeys(failed.OuterSharedSecret,
                                                                          failed.TrampolineSharedSecret, null),
        _ => null
    };

    /// <summary>
    /// The trampoline failure keys of the incoming <paramref name="htlc"/>, or null when it is not a trampoline HTLC:
    /// its onion peeled again (no replay check), else the secrets stored with its trampoline relay part (outside any
    /// blinded route). Never throws for an onion that does not peel.
    /// </summary>
    public static async Task<TrampolineFailureKeys?> ResolveKeysAsync(IncomingOnionProcessor? onionProcessor,
                                                                      IUnitOfWork unitOfWork, ChannelId channelId,
                                                                      HtlcRecord htlc, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(htlc);

        if (onionProcessor is not null && !htlc.OnionRoutingPacket.IsEmpty)
        {
            try
            {
                var result = await onionProcessor.ProcessAsync(htlc.OnionRoutingPacket, htlc.PaymentHash,
                                                               replayOwner: null, htlc.PathKey,
                                                               LightningMoney.MilliSatoshis(htlc.AmountMsat),
                                                               htlc.CltvExpiry);
                if (KeysFrom(result) is { } keys)
                    return keys;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger?.LogWarning(e, "Could not peel the onion of incoming HTLC {HtlcId} of channel {ChannelId} "
                                    + "again", htlc.Id, channelId);
            }
        }

        return await TrampolineRelayReads.GetPartAsync(unitOfWork, channelId, htlc.Id) is { } part
                   ? new TrampolineFailureKeys(part.OuterSharedSecret, part.TrampolineSharedSecret, null)
                   : null;
    }

    /// <summary>
    /// Fails <paramref name="htlc"/> as the erring trampoline node with <paramref name="failure"/>: past the
    /// introduction node of a blinded trampoline route <c>update_fail_malformed_htlc</c> + <c>invalid_onion_blinding</c>
    /// with the trampoline packet's sha256; at the introduction node of one that is not its final node our own
    /// <c>invalid_onion_blinding</c>; else <paramref name="failure"/>. The reason is created with the trampoline secret
    /// then the outer one, with <c>attribution_data</c> for the outer layer and our hold time when
    /// <paramref name="attributionDataService"/> is given (the caller passes it only when we advertise
    /// <c>option_attribution_data</c>) and the HTLC carried no <c>path_key</c>. Exceptions of
    /// <see cref="IChannelOperations"/> propagate.
    /// </summary>
    /// <returns>A short description of what was sent, for the caller's log.</returns>
    public static async Task<string> FailAsync(IChannelOperations channelOperations,
                                               IFailureOnionService failureOnionService,
                                               IAttributionDataService? attributionDataService, ChannelId channelId,
                                               HtlcRecord htlc, TrampolineFailureKeys keys, FailureMessage failure,
                                               CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelOperations);
        ArgumentNullException.ThrowIfNull(failureOnionService);
        ArgumentNullException.ThrowIfNull(htlc);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(failure);

        if (keys.BlindedMalformedSha256 is { } sha256)
        {
            await channelOperations.FailMalformedHtlcAsync(channelId, htlc.Id, FailureCode.InvalidOnionBlinding,
                                                           new Hash(sha256), cancellationToken);
            return "update_fail_malformed_htlc invalid_onion_blinding (trampoline)";
        }

        if (keys.IntroductionSha256 is { } introductionSha256)
            failure = FailureMessage.InvalidOnionBlinding(introductionSha256);

        if (attributionDataService is not null && htlc.PathKey is null)
        {
            var holdTime = await channelOperations.GetHoldTimeAsync(channelId, htlc.Id, cancellationToken);
            await channelOperations.FailHtlcAsync(channelId, htlc.Id,
                                                  TrampolineErrorPackets.CreateAttributed(
                                                      failureOnionService, attributionDataService,
                                                      keys.TrampolineSharedSecret, keys.OuterSharedSecret, failure,
                                                      holdTime),
                                                  cancellationToken);
        }
        else
        {
            var trampolineLayer = failureOnionService.CreateErrorPacket(keys.TrampolineSharedSecret, failure);
            await channelOperations.FailHtlcAsync(channelId, htlc.Id,
                                                  failureOnionService.WrapErrorPacket(keys.OuterSharedSecret,
                                                                                      trampolineLayer),
                                                  cancellationToken);
        }

        return $"{failure.Code} (trampoline)";
    }
}