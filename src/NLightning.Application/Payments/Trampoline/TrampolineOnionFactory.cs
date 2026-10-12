using System.Security.Cryptography;

namespace NLightning.Application.Payments.Trampoline;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;
using Routing;

/// <summary>
/// Builds the trampoline onion of a payment we send through a trampoline node (NL-875, BOLTs PR 836 "Trampoline
/// Payments", payer side) and the inner hop payloads it carries.
/// </summary>
/// <remarks>
/// <para>Inner payloads (the PR's writer rules):</para>
/// <list type="bullet">
///   <item>An intermediate trampoline node: <c>amt_to_forward</c>, <c>outgoing_cltv_value</c> and
///   <c>outgoing_node_id</c> (<see cref="CreateIntermediatePayload"/>).</item>
///   <item>A BOLT 11 recipient: <c>amt_to_forward</c>, <c>outgoing_cltv_value</c>, <c>payment_data</c> with the
///   invoice's <c>payment_secret</c> and the inner total, and <c>payment_metadata</c> when the invoice has one
///   (<see cref="CreateFinalPayload"/>).</item>
///   <item>A BOLT 12 recipient that supports trampoline: the blinded hops are trampoline hops; each carries its
///   <c>encrypted_recipient_data</c>, the introduction node also <c>current_path_key</c>, and only the final one
///   <c>amt_to_forward</c>, <c>outgoing_cltv_value</c> and <c>total_amount_msat</c>
///   (<see cref="CreateBlindedHops"/>).</item>
///   <item>A BOLT 12 recipient without trampoline support: the last trampoline node gets <c>amt_to_forward</c>,
///   <c>outgoing_cltv_value</c>, the invoice's blinded paths (<c>recipient_blinded_paths</c>) and its features
///   (<c>recipient_features</c>), never <c>outgoing_node_id</c>
///   (<see cref="CreateRecipientBlindedPathsPayload"/>).</item>
/// </list>
/// <para>The session key is 32 bytes from the OS CSPRNG, drawn apart from the outer onion's (the PR: "MUST use a
/// different session_key"), used once and zeroed. The size follows <see cref="TrampolineOnionSizePolicy.Auto"/>: 650
/// bytes of <c>hop_payloads</c> when the payloads fit (decision D-TR3), else the exact size, never above the maximum the
/// outer route leaves (<see cref="GetMaxHopPayloadsLength"/>).</para>
/// </remarks>
public sealed class TrampolineOnionFactory
{
    private readonly ITrampolineOnionService _trampolineOnionService;
    private readonly IHopPayloadSerializer _hopPayloadSerializer;

    public TrampolineOnionFactory(ITrampolineOnionService trampolineOnionService,
                                  IHopPayloadSerializer hopPayloadSerializer)
    {
        _trampolineOnionService = trampolineOnionService;
        _hopPayloadSerializer = hopPayloadSerializer;
    }

    /// <summary>
    /// Builds the trampoline onion with a fresh CSPRNG session key, sized by
    /// <see cref="TrampolineOnionSizePolicy.Auto"/> with <paramref name="maxHopPayloadsLength"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The payloads do not fit in <paramref name="maxHopPayloadsLength"/>, or a hop
    /// is invalid.</exception>
    public async Task<TrampolineOnion> CreateAsync(IReadOnlyList<TrampolineHop> hops, Hash paymentHash,
                                                   int maxHopPayloadsLength)
    {
        ArgumentNullException.ThrowIfNull(hops);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHopPayloadsLength);

        var sessionKey = PaymentOnionFactory.CreateSessionKey();
        try
        {
            return await CreateAsync(hops, new PrivKey(sessionKey), paymentHash,
                                     TrampolineOnionSizePolicy.Auto(maxHopPayloadsLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }

    /// <summary>
    /// Builds the trampoline onion with the given session key and size policy (tests; production uses
    /// <see cref="CreateAsync(IReadOnlyList{TrampolineHop}, Hash, int)"/>). The key must never be reused.
    /// </summary>
    public async Task<TrampolineOnion> CreateAsync(IReadOnlyList<TrampolineHop> hops, PrivKey sessionKey,
                                                   Hash paymentHash, TrampolineOnionSizePolicy sizePolicy)
    {
        ArgumentNullException.ThrowIfNull(hops);
        if (hops.Count == 0)
            throw new ArgumentException("A trampoline route has at least one hop.", nameof(hops));

        var onionHops = new List<OnionHop>(hops.Count);
        foreach (var hop in hops)
        {
            using var stream = new MemoryStream();
            await _hopPayloadSerializer.SerializeAsync(hop.Payload, stream);
            onionHops.Add(new OnionHop(hop.NodeId, stream.ToArray()));
        }

        return _trampolineOnionService.Build(onionHops, sessionKey, paymentHash, sizePolicy);
    }

    /// <summary>
    /// The largest trampoline <c>hop_payloads</c> length an outer route leaves (see
    /// <see cref="ITrampolineOnionService.GetMaxHopPayloadsLength"/>).
    /// </summary>
    /// <param name="outerHopsFramedLength">The framed payloads of the outer hops before the trampoline node's.</param>
    /// <param name="finalPayloadOtherTlvsLength">The trampoline node's outer payload without TLV 20.</param>
    public int GetMaxHopPayloadsLength(int outerHopsFramedLength, int finalPayloadOtherTlvsLength) =>
        _trampolineOnionService.GetMaxHopPayloadsLength(outerHopsFramedLength, finalPayloadOtherTlvsLength);

    /// <summary>An intermediate trampoline node's payload: forward <paramref name="amount"/> to
    /// <paramref name="nextNodeId"/> with the expiry <paramref name="outgoingCltvValue"/>.</summary>
    public static HopPayload CreateIntermediatePayload(LightningMoney amount, uint outgoingCltvValue,
                                                       CompactPubKey nextNodeId)
    {
        ArgumentNullException.ThrowIfNull(amount);
        return new HopPayload(new AmtToForwardTlv(amount), new OutgoingCltvValueTlv(outgoingCltvValue),
                              new OutgoingNodeIdTlv(nextNodeId));
    }

    /// <summary>A BOLT 11 recipient's payload: the invoice's <paramref name="paymentSecret"/> with the inner
    /// <paramref name="totalAmount"/>, and its <paramref name="paymentMetadata"/> when it has one.</summary>
    public static HopPayload CreateFinalPayload(LightningMoney amount, uint outgoingCltvValue, Secret paymentSecret,
                                                LightningMoney totalAmount,
                                                ReadOnlyMemory<byte>? paymentMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(totalAmount);

        var tlvs = new List<BaseTlv>
        {
            new AmtToForwardTlv(amount),
            new OutgoingCltvValueTlv(outgoingCltvValue),
            new PaymentDataTlv(paymentSecret, totalAmount)
        };
        if (paymentMetadata is { Length: > 0 } metadata)
            tlvs.Add(new PaymentMetadataTlv(metadata.Span));

        return new HopPayload(tlvs.ToArray());
    }

    /// <summary>The last trampoline node's payload for a BOLT 12 recipient without trampoline support: pay
    /// <paramref name="amount"/> along <paramref name="paths"/> with the final expiry
    /// <paramref name="outgoingCltvValue"/>.</summary>
    public static HopPayload CreateRecipientBlindedPathsPayload(LightningMoney amount, uint outgoingCltvValue,
                                                                IReadOnlyList<WireBlindedPaymentPath> paths,
                                                                FeatureSet? recipientFeatures)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
            throw new ArgumentException("At least one blinded path is required.", nameof(paths));

        var tlvs = new List<BaseTlv>
        {
            new AmtToForwardTlv(amount),
            new OutgoingCltvValueTlv(outgoingCltvValue),
            new RecipientBlindedPathsTlv(paths)
        };
        if (recipientFeatures is not null && recipientFeatures.GetWireBytes() is { Length: > 0 })
            tlvs.Add(new RecipientFeaturesTlv(recipientFeatures));

        return new HopPayload(tlvs.ToArray());
    }

    /// <summary>
    /// The hops of a blinded path used as trampoline hops (a BOLT 12 recipient that supports trampoline): the
    /// introduction node under its real id with <c>current_path_key</c>, the others under their blinded ids, the last
    /// with the final amount, expiry and <paramref name="totalAmount"/>.
    /// </summary>
    public static IReadOnlyList<TrampolineHop> CreateBlindedHops(BlindedPath path, LightningMoney amount,
                                                                 uint finalCltvExpiry, LightningMoney totalAmount)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(totalAmount);
        if (path.Hops.Count == 0)
            throw new ArgumentException("The blinded path has no hop.", nameof(path));

        var hops = new List<TrampolineHop>(path.Hops.Count);
        for (var i = 0; i < path.Hops.Count; i++)
        {
            var isFinal = i == path.Hops.Count - 1;
            var tlvs = new List<BaseTlv>();
            if (isFinal)
            {
                tlvs.Add(new AmtToForwardTlv(amount));
                tlvs.Add(new OutgoingCltvValueTlv(finalCltvExpiry));
                tlvs.Add(new TotalAmountMsatTlv(totalAmount));
            }

            tlvs.Add(new EncryptedRecipientDataTlv(path.Hops[i].EncryptedRecipientData.Span));
            if (i == 0)
                tlvs.Add(new CurrentPathKeyTlv(path.FirstPathKey));

            var nodeId = i == 0 ? path.FirstNodeId : path.Hops[i].BlindedNodeId;
            hops.Add(new TrampolineHop(nodeId, new HopPayload(tlvs.ToArray()), amount, finalCltvExpiry));
        }

        return hops;
    }
}

/// <summary>
/// One layer of a trampoline onion: the node that peels it, its payload and, for the record
/// (<c>PaymentTrampolineHops</c>), what it forwards and its <c>outgoing_cltv_value</c>.
/// </summary>
/// <param name="NodeId">The trampoline node (or the recipient, or a blinded node id).</param>
/// <param name="Payload">The hop's inner payload.</param>
/// <param name="Amount">Its <c>amt_to_forward</c> (inside a blinded path: the final amount, for bookkeeping).</param>
/// <param name="CltvExpiry">Its <c>outgoing_cltv_value</c> (inside a blinded path: the final expiry).</param>
public sealed record TrampolineHop(CompactPubKey NodeId, HopPayload Payload, LightningMoney Amount, uint CltvExpiry);