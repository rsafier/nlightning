using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.Onion;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Extensions;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Validators;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Serialization.Interfaces;

/// <summary>
/// Processes the onion of a locked-in incoming HTLC (ONION M4-T2): peel one layer with the node key, check the replay
/// cache, parse and validate the hop payload, and classify the HTLC as a forward or a final-hop payment, or say how to
/// fail it.
/// </summary>
/// <remarks>
/// <para>Order (BOLT 4 "Accepting and Forwarding a Payment" and "Returning Errors"):</para>
/// <list type="number">
///   <item>Peel with <see cref="ISphinxService.PeelAsLocalNode"/> and the <c>payment_hash</c> as associated data.
///   A BADONION failure (bad version, key or HMAC; with an <c>update_add_htlc</c> <c>path_key</c> every failure is
///   <c>invalid_onion_blinding</c>) → <see cref="IncomingOnionMalformed"/> with <c>sha256_of_onion</c>. Bad framing
///   after the HMAC verified → <see cref="IncomingOnionFailed"/> with <c>invalid_onion_payload</c>.</item>
///   <item>Record the packet HMAC in <see cref="IOnionReplayStore"/> only now that it is authenticated, owned by the
///   incoming HTLC (<see cref="OnionReplayOwner"/>) and kept until its <c>cltv_expiry</c> (NL-078). The same HTLC
///   processed again (restart, link-up) is not a replay; another HTLC carrying a recorded HMAC is →
///   <see cref="IncomingOnionFailed"/> with <c>temporary_node_failure</c> (we never know the preimage of a forward,
///   and the final hop's invoice logic is not consulted for a replayed onion).</item>
///   <item>Parse the payload with <see cref="IHopPayloadSerializer.DeserializeAsync"/> and validate it with
///   <see cref="HopPayloadValidator"/> → <c>invalid_onion_payload</c> (type, offset) on failure.</item>
///   <item>Route blinding (M5) is not implemented. An HTLC with an <c>update_add_htlc</c> <c>path_key</c> is failed
///   with <c>update_fail_malformed_htlc</c> + <c>invalid_onion_blinding</c>; a payload carrying
///   <c>current_path_key</c> (we would be the introduction point) with <c>update_fail_htlc</c> +
///   <c>invalid_onion_blinding</c> (BOLT 2 <c>update_add_htlc</c> receiver rules).</item>
/// </list>
/// <para>Pure apart from the replay store (which persists the HMAC before this returns): no channel calls.
/// Thread-safe.</para>
/// </remarks>
public sealed class IncomingOnionProcessor
{
    private readonly ISphinxService _sphinxService;
    private readonly IHopPayloadSerializer _hopPayloadSerializer;
    private readonly IOnionReplayStore _replayStore;
    private readonly ILogger<IncomingOnionProcessor> _logger;

    // Counts down the HTLC ids of HMACs recorded without an incoming HTLC
    private long _unownedRecords;

    public IncomingOnionProcessor(ISphinxService sphinxService, IHopPayloadSerializer hopPayloadSerializer,
                                  IOnionReplayStore replayStore, ILogger<IncomingOnionProcessor> logger)
    {
        _sphinxService = sphinxService;
        _hopPayloadSerializer = hopPayloadSerializer;
        _replayStore = replayStore;
        _logger = logger;
    }

    /// <summary>
    /// Processes the onion of an incoming HTLC.
    /// </summary>
    /// <param name="onionRoutingPacket">The <c>update_add_htlc</c> <c>onion_routing_packet</c> (1366 bytes).</param>
    /// <param name="paymentHash">The HTLC's <c>payment_hash</c> (the Sphinx associated data).</param>
    /// <param name="updateAddPathKey">The <c>update_add_htlc</c> <c>path_key</c> TLV, if any; never the payload's
    /// <c>current_path_key</c>.</param>
    /// <param name="checkReplay">False only when re-processing an HTLC this node already processed and acted on (for
    /// example to recover its shared secret), so nothing is recorded.</param>
    /// <param name="replayOwner">The incoming HTLC carrying the onion: it owns the recorded HMAC (so processing the
    /// same HTLC again is not a replay) and its <c>cltv_expiry</c> bounds how long the HMAC is kept. Without it
    /// (tests, tools) the HMAC is recorded for no HTLC, never expires and any second use is a replay.</param>
    /// <returns>The classification; never throws for a bad onion.</returns>
    /// <exception cref="ArgumentException">If <paramref name="onionRoutingPacket"/> is not a payment onion length
    /// (the <c>update_add_htlc</c> serializer already guarantees it).</exception>
    public async Task<IncomingOnionResult> ProcessAsync(ReadOnlyMemory<byte> onionRoutingPacket, Hash paymentHash,
                                                        CompactPubKey? updateAddPathKey = null,
                                                        bool checkReplay = true,
                                                        OnionReplayOwner? replayOwner = null)
    {
        var packet = new OnionPacket(onionRoutingPacket.Span);
        var hasPathKey = updateAddPathKey is not null;

        // 1. Peel
        PeeledOnion peeled;
        try
        {
            peeled = _sphinxService.PeelAsLocalNode(packet, paymentHash, updateAddPathKey);
        }
        catch (OnionException e)
        {
            return FromPeelFailure(e, onionRoutingPacket, hasPathKey);
        }

        // 2. Replay protection, only for authenticated packets
        if (checkReplay && !await TryRecordAsync(packet.Hmac, replayOwner))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Replayed onion for payment hash {PaymentHash}", paymentHash);

            return hasPathKey
                       ? Malformed(FailureCode.InvalidOnionBlinding, onionRoutingPacket)
                       : new IncomingOnionFailed(peeled.SharedSecret, FailureMessage.TemporaryNodeFailure());
        }

        // 3. Parse and validate the hop payload
        HopPayload payload;
        try
        {
            payload = await _hopPayloadSerializer.DeserializeAsync(peeled.Payload);
        }
        catch (OnionException e)
        {
            return FromPayloadFailure(e, peeled, onionRoutingPacket, hasPathKey, null);
        }

        if (!HopPayloadValidator.TryValidate(payload, peeled.IsFinal, hasPathKey, out var validationError))
            return FromPayloadFailure(validationError, peeled, onionRoutingPacket, hasPathKey, payload);

        // 4. Route blinding is not supported yet (ONION M5)
        if (hasPathKey)
            return Malformed(FailureCode.InvalidOnionBlinding, onionRoutingPacket);

        if (payload.IsBlinded)
            return new IncomingOnionFailed(peeled.SharedSecret,
                                           FailureMessage.InvalidOnionBlinding(Sha256OfOnion(onionRoutingPacket)));

        // 5. Classify
        if (peeled.IsFinal)
            return new IncomingOnionFinal(peeled.SharedSecret, payload);

        return new IncomingOnionForward(peeled.SharedSecret, payload, peeled.NextPacket!.Value);
    }

    private Task<bool> TryRecordAsync(ReadOnlyMemory<byte> hmac, OnionReplayOwner? replayOwner)
    {
        // Without an owner: a fresh id on the all-zero channel (no real channel id) and no expiry, so no later call
        // owns it (every repeat is a replay) and it is never pruned
        var owner = replayOwner
                 ?? new OnionReplayOwner(ChannelId.Zero,
                                         ulong.MaxValue - (ulong)Interlocked.Increment(ref _unownedRecords),
                                         uint.MaxValue);
        return _replayStore.TryAddAsync(hmac, owner.ChannelId, owner.HtlcId, owner.CltvExpiry);
    }

    private IncomingOnionResult FromPeelFailure(OnionException e, ReadOnlyMemory<byte> onion, bool hasPathKey)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Onion peel failed with {FailureCode}: {Message}", e.FailureCode, e.Message);

        if (hasPathKey)
            return Malformed(FailureCode.InvalidOnionBlinding, onion);

        if (e.FailureCode.IsBadOnion())
            return new IncomingOnionMalformed(e.FailureCode, e.FailureData ?? Sha256OfOnion(onion));

        // invalid_onion_payload from bad framing: the HMAC verified, so the failure can be encrypted
        if (e.SharedSecret is { } sharedSecret && TryCreateFailure(e, out var failure))
            return new IncomingOnionFailed(sharedSecret, failure);

        // Cannot encrypt anything without the shared secret: report the onion as unreadable
        return Malformed(FailureCode.InvalidOnionHmac, onion);
    }

    private IncomingOnionResult FromPayloadFailure(OnionException e, PeeledOnion peeled, ReadOnlyMemory<byte> onion,
                                                   bool hasPathKey, HopPayload? payload)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Hop payload rejected with {FailureCode}: {Message}", e.FailureCode, e.Message);

        if (hasPathKey)
            return Malformed(FailureCode.InvalidOnionBlinding, onion);

        // BOLT 4: an erring non-final node with current_path_key in its payload returns invalid_onion_blinding
        if (payload?.CurrentPathKey is not null && !peeled.IsFinal)
            return new IncomingOnionFailed(peeled.SharedSecret,
                                           FailureMessage.InvalidOnionBlinding(Sha256OfOnion(onion)));

        return TryCreateFailure(e, out var failure)
                   ? new IncomingOnionFailed(peeled.SharedSecret, failure)
                   : new IncomingOnionFailed(peeled.SharedSecret, FailureMessage.TemporaryNodeFailure());
    }

    private static bool TryCreateFailure(OnionException e, out FailureMessage failure)
    {
        try
        {
            failure = new FailureMessage(e.FailureCode, e.FailureData ?? ReadOnlyMemory<byte>.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            failure = null!;
            return false;
        }
    }

    private static IncomingOnionMalformed Malformed(FailureCode failureCode, ReadOnlyMemory<byte> onion) =>
        new(failureCode, Sha256OfOnion(onion));

    private static byte[] Sha256OfOnion(ReadOnlyMemory<byte> onion) => SHA256.HashData(onion.Span);
}