using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Payments.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Extensions;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Validators;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Serialization.Interfaces;

/// <summary>
/// Processes the onion of a locked-in incoming HTLC (ONION M4-T2, M5): peel one layer with the node key, check the
/// replay cache, parse and validate the hop payload, read the recipient's instructions inside a blinded route, and
/// classify the HTLC as a forward or a final-hop payment, or say how to fail it.
/// </summary>
/// <remarks>
/// <para>Order (BOLT 4 "Accepting and Forwarding a Payment", "Route Blinding" and "Returning Errors"):</para>
/// <list type="number">
///   <item>Peel with <see cref="ISphinxService.PeelAsLocalNode"/> and the <c>payment_hash</c> as associated data.
///   A BADONION failure (bad version, key or HMAC; with an <c>update_add_htlc</c> <c>path_key</c> every failure is
///   <c>invalid_onion_blinding</c>) → <see cref="IncomingOnionMalformed"/> with <c>sha256_of_onion</c>. Bad framing
///   after the HMAC verified → <see cref="IncomingOnionFailed"/> with <c>invalid_onion_payload</c>.</item>
///   <item>Record the packet HMAC in <see cref="IOnionReplayStore"/> only now that it is authenticated, owned by the
///   incoming HTLC (<see cref="OnionReplayOwner"/>) and kept until its <c>cltv_expiry</c> (NL-078). The same HTLC
///   processed again (restart, link-up) is not a replay; another HTLC carrying a recorded HMAC is →
///   <see cref="IncomingOnionFailed"/> with <c>temporary_node_failure</c> (we never know the preimage of a forward,
///   and the final hop's invoice logic is not consulted for a replayed onion), or <c>invalid_onion_blinding</c> inside
///   a blinded route.</item>
///   <item>Parse the payload with <see cref="IHopPayloadSerializer.DeserializeAsync"/> and validate it with
///   <see cref="HopPayloadValidator"/> → <c>invalid_onion_payload</c> (type, offset) on failure.</item>
///   <item>Route blinding (M5), only when we advertise <c>option_route_blinding</c> and an
///   <see cref="IRouteBlindingService"/> is registered: decrypt <c>encrypted_recipient_data</c> with the
///   <c>update_add_htlc</c> path_key (reusing the peel's <see cref="PeeledOnion.PathKeySharedSecret"/>) or, at the
///   introduction node, the payload's <c>current_path_key</c>; check it with
///   <see cref="BlindedRecipientDataValidator"/> (with the incoming amount and expiry when given) and compute the
///   forward's amount and expiry from <c>payment_relay</c>. Any failure: <c>update_fail_malformed_htlc</c> +
///   <c>invalid_onion_blinding</c> when the path_key came in <c>update_add_htlc</c>, else (introduction node)
///   <c>update_fail_htlc</c> + <c>invalid_onion_blinding</c>. Without the feature, a blinded HTLC is refused the same
///   way (BOLT 2 <c>update_add_htlc</c> receiver rules).</item>
///   <item>Dummy hops (NL-440): a non-final blinded hop whose <c>next_node_id</c> is our own node id (and no
///   <c>short_channel_id</c>) is a hop of a path we made that relays to ourselves (BOLT 4: the writer "MAY add
///   additional dummy hops at the end of the path (which it will ignore on receipt)"; <c>BlindedPathBuilder</c> writes
///   them the way LND does). Its <c>payment_relay</c> is applied to the amount and expiry as a forward would, then the
///   next layer is peeled here with the next path_key, at most <see cref="MaxSelfRelayHops"/> times, and the result is
///   that of the last layer (final: <see cref="IncomingBlindedHop.DummyHops"/> and the amount and expiry our final hop
///   would have received). Every failure inside is the blinded failure of the outer layer; the shared secret is the
///   outer layer's (the one the sender reads errors with). No replay entry is kept for the inner layers: their HMAC
///   is covered by the outer one.</item>
/// </list>
/// <para>Pure apart from the replay store (which persists the HMAC before this returns): no channel calls.
/// Thread-safe.</para>
/// </remarks>
public sealed class IncomingOnionProcessor
{
    /// <summary>
    /// The most hops relaying to ourselves peeled for one HTLC (a 1300-byte onion holds about 20 blinded hops).
    /// </summary>
    public const int MaxSelfRelayHops = 20;

    private readonly ISphinxService _sphinxService;
    private readonly IHopPayloadSerializer _hopPayloadSerializer;
    private readonly IOnionReplayStore _replayStore;
    private readonly ILogger<IncomingOnionProcessor> _logger;
    private readonly IRouteBlindingService? _routeBlindingService;
    private readonly ISecureKeyManager? _secureKeyManager;

    public IncomingOnionProcessor(ISphinxService sphinxService, IHopPayloadSerializer hopPayloadSerializer,
                                  IOnionReplayStore replayStore, ILogger<IncomingOnionProcessor> logger,
                                  IRouteBlindingService? routeBlindingService = null,
                                  IOptions<NodeOptions>? nodeOptions = null,
                                  ISecureKeyManager? secureKeyManager = null)
    {
        _secureKeyManager = secureKeyManager;
        _sphinxService = sphinxService;
        _hopPayloadSerializer = hopPayloadSerializer;
        _replayStore = replayStore;
        _logger = logger;

        // Blinded payloads are read only when we advertise the feature: nobody should send them to us otherwise
        var advertised = (nodeOptions?.Value.Features.OptionRouteBlinding ?? FeatureSupport.No) != FeatureSupport.No;
        _routeBlindingService = advertised ? routeBlindingService : null;
    }

    /// <summary>
    /// Whether blinded payloads are read (we advertise <c>option_route_blinding</c> and have the service).
    /// </summary>
    public bool ReadsBlindedPayloads => _routeBlindingService is not null;

    /// <summary>
    /// Processes the onion of an incoming HTLC.
    /// </summary>
    /// <param name="onionRoutingPacket">The <c>update_add_htlc</c> <c>onion_routing_packet</c> (1366 bytes).</param>
    /// <param name="paymentHash">The HTLC's <c>payment_hash</c> (the Sphinx associated data).</param>
    /// <param name="replayOwner">The incoming HTLC carrying the onion, which turns on the replay check: it owns the
    /// recorded HMAC (so processing the same HTLC again is not a replay) and its <c>cltv_expiry</c> bounds how long the
    /// HMAC is kept. Pass null only when re-processing an HTLC this node already processed and acted on (for example
    /// to recover its shared secret): then nothing is recorded or checked. There is deliberately no way to record an
    /// HMAC without an owning HTLC, since such a row could never be pruned and would fail its own HTLC after a
    /// restart.</param>
    /// <param name="updateAddPathKey">The <c>update_add_htlc</c> <c>path_key</c> TLV, if any; never the payload's
    /// <c>current_path_key</c>.</param>
    /// <param name="incomingAmount">The incoming HTLC's <c>amount_msat</c>. Needed inside a blinded route to check
    /// <c>payment_constraints</c> and compute the forward's amount; without it those are skipped (use only to recover
    /// the shared secret or the blinded role).</param>
    /// <param name="incomingCltvExpiry">The incoming HTLC's <c>cltv_expiry</c>, needed the same way.</param>
    /// <returns>The classification; never throws for a bad onion.</returns>
    /// <exception cref="ArgumentException">If <paramref name="onionRoutingPacket"/> is not a payment onion length
    /// (the <c>update_add_htlc</c> serializer already guarantees it).</exception>
    public async Task<IncomingOnionResult> ProcessAsync(ReadOnlyMemory<byte> onionRoutingPacket, Hash paymentHash,
                                                        OnionReplayOwner? replayOwner,
                                                        CompactPubKey? updateAddPathKey = null,
                                                        LightningMoney? incomingAmount = null,
                                                        uint? incomingCltvExpiry = null)
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
        if (replayOwner is { } owner
         && !await _replayStore.TryAddAsync(packet.Hmac, owner.ChannelId, owner.HtlcId, owner.CltvExpiry))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Replayed onion for payment hash {PaymentHash}", paymentHash);

            if (hasPathKey)
                return Malformed(FailureCode.InvalidOnionBlinding, onionRoutingPacket);

            // An introduction node that is not the final node answers every error with invalid_onion_blinding
            return !peeled.IsFinal && await CarriesCurrentPathKeyAsync(peeled)
                       ? BlindingFailed(peeled, onionRoutingPacket)
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

        // 4. Route blinding (ONION M5)
        if (hasPathKey || payload.IsBlinded)
            return await ProcessBlindedAsync(peeled, payload, onionRoutingPacket, paymentHash, updateAddPathKey,
                                             incomingAmount, incomingCltvExpiry);

        // 5. Classify
        if (peeled.IsFinal)
            return new IncomingOnionFinal(peeled.SharedSecret, payload);

        return new IncomingOnionForward(peeled.SharedSecret, payload, peeled.NextPacket!.Value);
    }

    private async Task<IncomingOnionResult> ProcessBlindedAsync(PeeledOnion peeled, HopPayload payload,
                                                                ReadOnlyMemory<byte> onion, Hash paymentHash,
                                                                CompactPubKey? updateAddPathKey,
                                                                LightningMoney? incomingAmount,
                                                                uint? incomingCltvExpiry)
    {
        IncomingOnionResult Refuse(string reason)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Blinded HTLC refused: {Reason}", reason);

            return updateAddPathKey is not null
                       ? Malformed(FailureCode.InvalidOnionBlinding, onion)
                       : BlindingFailed(peeled, onion);
        }

        if (_routeBlindingService is null)
            return Refuse("route blinding is not enabled (option_route_blinding is not advertised).");

        // The validator guarantees exactly one path key and the encrypted_recipient_data
        var isIntroduction = updateAddPathKey is null;
        var pathKey = updateAddPathKey ?? payload.CurrentPathKey!.Value;
        BlindedHopUnblinding unblinded;
        try
        {
            unblinded = _routeBlindingService.UnblindAsLocalNode(pathKey, payload.EncryptedRecipientData!.Value,
                                                                 peeled.PathKeySharedSecret);
        }
        catch (OnionException e)
        {
            return Refuse(e.Message);
        }

        var data = unblinded.RecipientData;
        if (!BlindedRecipientDataValidator.TryValidate(data, peeled.IsFinal, incomingAmount?.MilliSatoshi,
                                                       incomingCltvExpiry, out var reason))
            return Refuse(reason);

        if (peeled.IsFinal)
            return new IncomingOnionFinal(peeled.SharedSecret, payload,
                                          new IncomingBlindedHop(isIntroduction, data, unblinded.NextPathKey));

        // BOLT 4: amt_to_forward and outgoing_cltv_value come from payment_relay
        var relay = data.PaymentRelay!;
        LightningMoney? amountToForward = null;
        if (incomingAmount is not null)
        {
            if (!relay.TryComputeAmountToForward(incomingAmount.MilliSatoshi, out var amountToForwardMsat))
                return Refuse($"amount_msat {incomingAmount.MilliSatoshi} does not cover fee_base_msat "
                            + $"{relay.FeeBaseMsat}.");
            amountToForward = LightningMoney.MilliSatoshis(amountToForwardMsat);
        }

        uint? outgoingCltvValue = null;
        if (incomingCltvExpiry is { } expiry)
        {
            if (!relay.TryComputeOutgoingCltvValue(expiry, out var outgoing))
                return Refuse($"cltv_expiry {expiry} is below payment_relay.cltv_expiry_delta "
                            + $"{relay.CltvExpiryDelta}.");
            outgoingCltvValue = outgoing;
        }

        // A hop that relays to ourselves (a dummy hop of our own path, NL-440): peel the next layer here
        if (IsSelfRelay(data))
            return await PeelSelfRelaysAsync(peeled, isIntroduction, peeled.NextPacket!.Value, paymentHash,
                                             unblinded.NextPathKey, amountToForward, outgoingCltvValue, Refuse);

        return new IncomingOnionForward(peeled.SharedSecret, payload, peeled.NextPacket!.Value,
                                        new IncomingBlindedHop(isIntroduction, data, unblinded.NextPathKey,
                                                               amountToForward, outgoingCltvValue));
    }

    /// <summary>
    /// Whether <paramref name="data"/> sends the HTLC on to this very node: <c>next_node_id</c> = our node id and no
    /// <c>short_channel_id</c>.
    /// </summary>
    private bool IsSelfRelay(BlindedRecipientData data) =>
        data.ShortChannelId is null && data.NextNodeId is { } next && _secureKeyManager is not null
     && next == _secureKeyManager.GetNodePubKey();

    /// <summary>
    /// Peels the layers of the hops that relay to ourselves (dummy hops) until one is final or names another node,
    /// applying each hop's <c>payment_relay</c> to the amount and expiry (BOLT 4 reader of a non-final blinded hop).
    /// </summary>
    private async Task<IncomingOnionResult> PeelSelfRelaysAsync(PeeledOnion outer, bool isIntroduction,
                                                                 OnionPacket packet, Hash paymentHash,
                                                                 CompactPubKey pathKey, LightningMoney? amount,
                                                                 uint? cltvExpiry,
                                                                 Func<string, IncomingOnionResult> refuse)
    {
        for (var depth = 1; depth <= MaxSelfRelayHops; depth++)
        {
            PeeledOnion inner;
            HopPayload payload;
            BlindedHopUnblinding unblinded;
            try
            {
                inner = _sphinxService.PeelAsLocalNode(packet, paymentHash, pathKey);
                payload = await _hopPayloadSerializer.DeserializeAsync(inner.Payload);
                if (!HopPayloadValidator.TryValidate(payload, inner.IsFinal, true, out var validationError))
                    return refuse($"hop {depth} relayed to ourselves: {validationError.Message}");

                unblinded = _routeBlindingService!.UnblindAsLocalNode(pathKey, payload.EncryptedRecipientData!.Value,
                                                                      inner.PathKeySharedSecret);
            }
            catch (OnionException e)
            {
                return refuse($"hop {depth} relayed to ourselves: {e.Message}");
            }

            var data = unblinded.RecipientData;
            if (!BlindedRecipientDataValidator.TryValidate(data, inner.IsFinal, amount?.MilliSatoshi, cltvExpiry,
                                                           out var reason))
                return refuse($"hop {depth} relayed to ourselves: {reason}");

            if (inner.IsFinal)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Blinded HTLC for {PaymentHash} reached us after {Count} dummy hop(s)",
                                     paymentHash, depth);

                return new IncomingOnionFinal(outer.SharedSecret, payload,
                                              new IncomingBlindedHop(isIntroduction, data, unblinded.NextPathKey)
                                              {
                                                  DummyHops = depth,
                                                  ReceivedAmount = amount,
                                                  ReceivedCltvExpiry = cltvExpiry
                                              });
            }

            var relay = data.PaymentRelay!;
            if (amount is not null)
            {
                if (!relay.TryComputeAmountToForward(amount.MilliSatoshi, out var forwardMsat))
                    return refuse($"hop {depth} relayed to ourselves: {amount.MilliSatoshi} msat does not cover "
                                + $"fee_base_msat {relay.FeeBaseMsat}.");
                amount = LightningMoney.MilliSatoshis(forwardMsat);
            }

            if (cltvExpiry is { } expiry)
            {
                if (!relay.TryComputeOutgoingCltvValue(expiry, out var outgoing))
                    return refuse($"hop {depth} relayed to ourselves: cltv_expiry {expiry} is below "
                                + $"payment_relay.cltv_expiry_delta {relay.CltvExpiryDelta}.");
                cltvExpiry = outgoing;
            }

            if (!IsSelfRelay(data))
                return new IncomingOnionForward(outer.SharedSecret, payload, inner.NextPacket!.Value,
                                                new IncomingBlindedHop(isIntroduction, data, unblinded.NextPathKey,
                                                                       amount, cltvExpiry)
                                                {
                                                    DummyHops = depth
                                                });

            packet = inner.NextPacket!.Value;
            pathKey = unblinded.NextPathKey;
        }

        return refuse($"more than {MaxSelfRelayHops} hops relay to ourselves.");
    }

    private async Task<bool> CarriesCurrentPathKeyAsync(PeeledOnion peeled)
    {
        try
        {
            return (await _hopPayloadSerializer.DeserializeAsync(peeled.Payload)).CurrentPathKey is not null;
        }
        catch (OnionException)
        {
            return false;
        }
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
            return BlindingFailed(peeled, onion);

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

    private static IncomingOnionFailed BlindingFailed(PeeledOnion peeled, ReadOnlyMemory<byte> onion) =>
        new(peeled.SharedSecret, FailureMessage.InvalidOnionBlinding(Sha256OfOnion(onion)));

    private static IncomingOnionMalformed Malformed(FailureCode failureCode, ReadOnlyMemory<byte> onion) =>
        new(failureCode, Sha256OfOnion(onion));

    private static byte[] Sha256OfOnion(ReadOnlyMemory<byte> onion) => SHA256.HashData(onion.Span);
}