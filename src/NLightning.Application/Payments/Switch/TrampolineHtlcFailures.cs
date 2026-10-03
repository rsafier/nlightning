using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.Switch;

using Domain.Channels.Commitments;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
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
    /// HTLC (a malformed or failed outer onion, a forward, an ordinary final hop). A trampoline layer the processor
    /// refused with <c>invalid_onion_blinding</c> (at the introduction node of a blinded trampoline route, NL-921)
    /// keeps that answer as <see cref="TrampolineFailureKeys.IntroductionSha256"/>, so every failure of the HTLC is our
    /// own <c>invalid_onion_blinding</c>, as <see cref="BlindedHtlcFailures.IsIntroductionForward"/> does for an
    /// ordinary blinded HTLC.
    /// </summary>
    public static TrampolineFailureKeys? KeysFrom(IncomingOnionResult result) => result switch
    {
        IncomingOnionTrampolineResult trampoline => TrampolineFailureKeys.From(trampoline),
        IncomingOnionTrampolineFailed failed => new TrampolineFailureKeys(
            failed.OuterSharedSecret, failed.TrampolineSharedSecret, null,
            failed.Failure is { Code: FailureCode.InvalidOnionBlinding, Sha256OfOnion: { } sha256 }
                ? sha256.ToArray()
                : null),
        _ => null
    };

    /// <summary>
    /// The failure keys of a trampoline relay part from the secrets stored with its <c>TrampolineRelayParts</c> row,
    /// for when its onion did not give them. Past the introduction node of a blinded trampoline route the onion peeled
    /// again may only answer <c>update_fail_malformed_htlc</c> + <c>invalid_onion_blinding</c> (route blinding turned
    /// off since the part arrived, NL-921): that answer is kept as
    /// <see cref="TrampolineFailureKeys.BlindedMalformedSha256"/>, as the switch and the relay engine fail such a part.
    /// </summary>
    /// <param name="outerSharedSecret">The row's outer secret.</param>
    /// <param name="trampolineSharedSecret">The row's trampoline secret.</param>
    /// <param name="repeeled">What the onion peeled again gave, or null when it was not peeled.</param>
    public static TrampolineFailureKeys FromStoredPart(Secret outerSharedSecret, Secret trampolineSharedSecret,
                                                       IncomingOnionResult? repeeled) =>
        new(outerSharedSecret, trampolineSharedSecret,
            repeeled is IncomingOnionMalformed { FailureCode: FailureCode.InvalidOnionBlinding } malformed
                ? malformed.Sha256OfOnion.ToArray()
                : null);

    /// <summary>
    /// The trampoline failure keys of the incoming <paramref name="htlc"/>, or null when it is not a trampoline HTLC:
    /// its onion peeled again (no replay check), else the secrets stored with its trampoline relay part. Never throws
    /// for an onion that does not peel.
    /// </summary>
    public static async Task<TrampolineFailureKeys?> ResolveKeysAsync(IncomingOnionProcessor? onionProcessor,
                                                                      IUnitOfWork unitOfWork, ChannelId channelId,
                                                                      HtlcRecord htlc, ILogger? logger = null) =>
        (await ResolveAsync(onionProcessor, unitOfWork, channelId, htlc, logger)).Keys;

    /// <summary>
    /// <see cref="ResolveKeysAsync"/>, with what the onion peeled again gave (null when it was not peeled or the peel
    /// threw), so a caller that goes on to fail an ordinary HTLC reuses it instead of peeling again (NL-921).
    /// </summary>
    /// <remarks>
    /// A relay part's row whose outer secret is not usable (<see cref="IsUsable"/>) gives no keys (a warning is logged):
    /// the caller then fails the HTLC as an ordinary one, with the outer secret stored with the HTLC.
    /// </remarks>
    public static async Task<(TrampolineFailureKeys? Keys, IncomingOnionResult? Processed)> ResolveAsync(
        IncomingOnionProcessor? onionProcessor, IUnitOfWork unitOfWork, ChannelId channelId, HtlcRecord htlc,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(htlc);

        IncomingOnionResult? processed = null;
        if (onionProcessor is not null && !htlc.OnionRoutingPacket.IsEmpty)
        {
            try
            {
                processed = await onionProcessor.ProcessAsync(htlc.OnionRoutingPacket, htlc.PaymentHash,
                                                              replayOwner: null, htlc.PathKey,
                                                              LightningMoney.MilliSatoshis(htlc.AmountMsat),
                                                              htlc.CltvExpiry);
                if (KeysFrom(processed) is { } keys)
                    return (keys, processed);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger?.LogWarning(e, "Could not peel the onion of incoming HTLC {HtlcId} of channel {ChannelId} "
                                    + "again", htlc.Id, channelId);
            }
        }

        if (await TrampolineRelayReads.GetPartAsync(unitOfWork, channelId, htlc.Id) is not { } part)
            return (null, processed);

        var stored = FromStoredPart(part.OuterSharedSecret, part.TrampolineSharedSecret, processed);
        if (stored.BlindedMalformedSha256 is null && !IsUsable(part.OuterSharedSecret))
        {
            logger?.LogWarning("The trampoline relay part of incoming HTLC {HtlcId} of channel {ChannelId} has no "
                             + "usable outer secret; failing it as an ordinary HTLC", htlc.Id, channelId);
            return (null, processed);
        }

        return (stored, processed);
    }

    /// <summary>
    /// Fails <paramref name="htlc"/> as the erring trampoline node with <paramref name="failure"/>: past the
    /// introduction node of a blinded trampoline route <c>update_fail_malformed_htlc</c> + <c>invalid_onion_blinding</c>
    /// with the trampoline packet's sha256; at the introduction node of one that is not its final node our own
    /// <c>invalid_onion_blinding</c>; else <paramref name="failure"/>. The reason is created with the trampoline secret
    /// then the outer one, with <c>attribution_data</c> for the outer layer and our hold time when
    /// <paramref name="attributionDataService"/> is given (the caller passes it only when we advertise
    /// <c>option_attribution_data</c>) and the HTLC carried no <c>path_key</c>. A trampoline secret that is not 32
    /// non-zero bytes (a damaged relay row) is never used: the failure is then created with the outer secret only and
    /// a warning is logged (NL-921), since a fail-back path must not throw. Exceptions of
    /// <see cref="IChannelOperations"/> propagate.
    /// </summary>
    /// <remarks>
    /// BOLT 2 / BOLT 4 say a node SHOULD wait a random delay before it sends an introduction node's
    /// <c>invalid_onion_blinding</c>. The switch and the relay engine do; these out-of-switch paths (the deadline
    /// monitor, the dust switch) do not, like <see cref="BlindedHtlcFailures.FailAsync"/>: they fail an HTLC on a
    /// deadline or a limit, not as the immediate answer to its arrival.
    /// </remarks>
    /// <returns>A short description of what was sent, for the caller's log.</returns>
    public static async Task<string> FailAsync(IChannelOperations channelOperations,
                                               IFailureOnionService failureOnionService,
                                               IAttributionDataService? attributionDataService, ChannelId channelId,
                                               HtlcRecord htlc, TrampolineFailureKeys keys, FailureMessage failure,
                                               ILogger? logger, CancellationToken cancellationToken)
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

        var trampolineUsable = IsUsable(keys.TrampolineSharedSecret);
        if (!trampolineUsable)
            logger?.LogWarning("No usable trampoline secret to fail incoming HTLC {HtlcId} of channel {ChannelId}; "
                             + "failing it with the outer secret only", htlc.Id, channelId);

        if (attributionDataService is not null && htlc.PathKey is null)
        {
            var holdTime = await channelOperations.GetHoldTimeAsync(channelId, htlc.Id, cancellationToken);
            var packet = trampolineUsable
                             ? TrampolineErrorPackets.CreateAttributed(failureOnionService, attributionDataService,
                                                                       keys.TrampolineSharedSecret,
                                                                       keys.OuterSharedSecret, failure, holdTime)
                             : attributionDataService.CreateErrorPacket(keys.OuterSharedSecret, failure, holdTime);
            await channelOperations.FailHtlcAsync(channelId, htlc.Id, packet, cancellationToken);
        }
        else
        {
            var reason = trampolineUsable
                             ? failureOnionService.WrapErrorPacket(keys.OuterSharedSecret,
                                                                   failureOnionService.CreateErrorPacket(
                                                                       keys.TrampolineSharedSecret, failure))
                             : failureOnionService.CreateErrorPacket(keys.OuterSharedSecret, failure);
            await channelOperations.FailHtlcAsync(channelId, htlc.Id, reason, cancellationToken);
        }

        return trampolineUsable ? $"{failure.Code} (trampoline)" : $"{failure.Code} (outer secret only)";
    }

    /// <summary>Whether <paramref name="secret"/> can obfuscate a failure: 32 bytes, not all zero.</summary>
    internal static bool IsUsable(Secret secret)
    {
        var bytes = (ReadOnlySpan<byte>)secret;
        return bytes.Length == CryptoConstants.SecretLen && bytes.IndexOfAnyExcept((byte)0) >= 0;
    }
}