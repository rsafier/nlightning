using System.Security.Cryptography;

namespace NLightning.Application.Tests.Payments.Switch;

using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;

/// <summary>
/// NL-897: real onions for the failure paths outside the switch (the HTLC deadline monitor, the dust-exposure
/// decorator). <see cref="Us"/> is the node that fails the HTLC (trampoline routing on, production onion processor,
/// failure onion and attribution services), <see cref="Payer"/> builds the onions and reads the failures as the origin
/// does (<c>DecryptTrampolineErrorPacket</c>, outer secrets first).
/// </summary>
internal sealed class TrampolineFailureTestKit : IDisposable
{
    private static readonly Secret s_invoiceSecret = Enumerable.Repeat((byte)0x5E, 32).ToArray();
    private static readonly Secret s_outerSecret = Enumerable.Repeat((byte)0x0E, 32).ToArray();

    // Our price as the blinded path's recipient set it: 100 msat + 10 ppm, delta 50
    private static readonly BlindedPaymentRelay s_relay = new(50, 10, 100);

    public PaymentsTestNode Payer { get; } = new("payer", 0x71);
    public PaymentsTestNode Us { get; } = new("us", 0x72);
    public PaymentsTestNode Next { get; } = new("next", 0x73);

    public TrampolineFailureTestKit()
    {
        Us.EnableTrampoline();
        Us.Options.Features.OptionRouteBlinding = FeatureSupport.Optional;
    }

    /// <summary>
    /// The payer's onion for an HTLC of <paramref name="amountMsat"/> with <paramref name="cltvExpiry"/> for which we
    /// are the final trampoline node (one inner hop, ours).
    /// </summary>
    public async Task<TrampolineTestOnion> BuildFinalAsync(Hash paymentHash, ulong amountMsat, uint cltvExpiry)
    {
        var amount = LightningMoney.MilliSatoshis(amountMsat);
        var trampoline = await BuildTrampolineAsync(paymentHash,
                                                    (Us.NodeId, new HopPayload(new AmtToForwardTlv(amount),
                                                                               new OutgoingCltvValueTlv(cltvExpiry - 10),
                                                                               new PaymentDataTlv(s_invoiceSecret,
                                                                                   amount))));
        return await BuildOuterAsync(paymentHash, trampoline, amount, cltvExpiry);
    }

    /// <summary>
    /// The payer's onion for an HTLC of <paramref name="amountMsat"/> with <paramref name="cltvExpiry"/> that we relay
    /// to <see cref="Next"/> as a trampoline node.
    /// </summary>
    public async Task<TrampolineTestOnion> BuildRelayAsync(Hash paymentHash, ulong amountMsat, uint cltvExpiry)
    {
        var amount = LightningMoney.MilliSatoshis(amountMsat);
        var forwarded = LightningMoney.MilliSatoshis(amountMsat - 5_000);
        var trampoline = await BuildTrampolineAsync(paymentHash,
                                                    (Us.NodeId, new HopPayload(new AmtToForwardTlv(forwarded),
                                                                               new OutgoingCltvValueTlv(cltvExpiry - 100),
                                                                               new OutgoingNodeIdTlv(Next.NodeId))),
                                                    (Next.NodeId, new HopPayload(new AmtToForwardTlv(forwarded),
                                                                                 new OutgoingCltvValueTlv(
                                                                                     cltvExpiry - 110),
                                                                                 new PaymentDataTlv(s_invoiceSecret,
                                                                                     forwarded))));
        return await BuildOuterAsync(paymentHash, trampoline, amount, cltvExpiry);
    }

    /// <summary>
    /// NL-921: the payer's onion for an HTLC of <paramref name="amountMsat"/> with <paramref name="cltvExpiry"/> that
    /// we relay as a hop of a blinded trampoline route towards <see cref="Next"/> (the recipient, which made us a hop
    /// of its blinded path). At the introduction node (<paramref name="introduction"/>) our trampoline payload carries
    /// the path key; past it <see cref="Payer"/> is the introduction node and the outer payload carries our path key
    /// (TLV 12). <paramref name="maxCltvExpiry"/> is our hop's <c>payment_constraints.max_cltv_expiry</c> (default:
    /// far above the expiry); below <paramref name="cltvExpiry"/> our recipient data fails.
    /// </summary>
    public async Task<TrampolineTestOnion> BuildBlindedRelayAsync(Hash paymentHash, ulong amountMsat, uint cltvExpiry,
                                                                  bool introduction, uint? maxCltvExpiry = null)
    {
        var amount = LightningMoney.MilliSatoshis(amountMsat);
        var amountOut = LightningMoney.MilliSatoshis(amountMsat - 5_000);
        var nodeIds = new List<CompactPubKey>();
        var data = new List<BlindedRecipientData>();
        if (!introduction)
        {
            nodeIds.Add(Payer.NodeId);
            data.Add(new BlindedRecipientData
            {
                NextNodeId = Us.NodeId,
                PaymentRelay = s_relay,
                PaymentConstraints = new BlindedPaymentConstraints(cltvExpiry + 20_000, 1)
            });
        }

        nodeIds.Add(Us.NodeId);
        data.Add(new BlindedRecipientData
        {
            NextNodeId = Next.NodeId,
            PaymentRelay = s_relay,
            PaymentConstraints = new BlindedPaymentConstraints(maxCltvExpiry ?? cltvExpiry + 10_000, 1)
        });
        nodeIds.Add(Next.NodeId);
        data.Add(new BlindedRecipientData { PathId = Enumerable.Repeat((byte)0x3D, 32).ToArray() });
        var path = Payer.RouteBlinding.CreateBlindedPath(nodeIds,
                                                         data.Select(Payer.RouteBlinding.EncodeRecipientData).ToList(),
                                                         Enumerable.Repeat((byte)0x3B, 32).ToArray());

        var ourHop = path.Hops[introduction ? 0 : 1];
        CompactPubKey? outerPathKey = null;
        (CompactPubKey, HopPayload) ours;
        if (introduction)
        {
            ours = (Us.NodeId, new HopPayload(new EncryptedRecipientDataTlv(ourHop.EncryptedRecipientData.Span),
                                              new CurrentPathKeyTlv(path.FirstPathKey)));
        }
        else
        {
            outerPathKey = Payer.RouteBlinding
                                .UnblindAsLocalNode(path.FirstPathKey, path.Hops[0].EncryptedRecipientData)
                                .NextPathKey;
            ours = (ourHop.BlindedNodeId,
                    new HopPayload(new EncryptedRecipientDataTlv(ourHop.EncryptedRecipientData.Span)));
        }

        var last = path.Hops[^1];
        var trampoline = await BuildTrampolineAsync(paymentHash, ours,
                                                    (last.BlindedNodeId,
                                                     new HopPayload(new AmtToForwardTlv(amountOut),
                                                                    new OutgoingCltvValueTlv(cltvExpiry - 200),
                                                                    new EncryptedRecipientDataTlv(
                                                                        last.EncryptedRecipientData.Span),
                                                                    new TotalAmountMsatTlv(amountOut))));
        return await BuildOuterAsync(paymentHash, trampoline, amount, cltvExpiry, outerPathKey);
    }

    /// <summary>
    /// NL-921: the payer's onion for an HTLC of <paramref name="amountMsat"/> with <paramref name="cltvExpiry"/> for
    /// which we are both the introduction node and the recipient of a blinded trampoline route whose
    /// <c>payment_constraints.max_cltv_expiry</c> is <paramref name="maxCltvExpiry"/>: only the HTLC's own expiry
    /// tells whether our recipient data fails.
    /// </summary>
    public async Task<TrampolineTestOnion> BuildBlindedFinalAsync(Hash paymentHash, ulong amountMsat, uint cltvExpiry,
                                                                  uint maxCltvExpiry)
    {
        var amount = LightningMoney.MilliSatoshis(amountMsat);
        var data = new BlindedRecipientData
        {
            PathId = Enumerable.Repeat((byte)0x3D, 32).ToArray(),
            PaymentConstraints = new BlindedPaymentConstraints(maxCltvExpiry, 1)
        };
        var path = Payer.RouteBlinding.CreateBlindedPath([Us.NodeId], [Payer.RouteBlinding.EncodeRecipientData(data)],
                                                         Enumerable.Repeat((byte)0x3B, 32).ToArray());
        var trampoline = await BuildTrampolineAsync(paymentHash,
                                                    (Us.NodeId,
                                                     new HopPayload(new AmtToForwardTlv(amount),
                                                                    new OutgoingCltvValueTlv(cltvExpiry - 10),
                                                                    new EncryptedRecipientDataTlv(
                                                                        path.Hops[0].EncryptedRecipientData.Span),
                                                                    new CurrentPathKeyTlv(path.FirstPathKey),
                                                                    new TotalAmountMsatTlv(amount))));
        return await BuildOuterAsync(paymentHash, trampoline, amount, cltvExpiry);
    }

    /// <summary>
    /// NL-921: a node with our key, trampoline routing on and route blinding off, as <see cref="Us"/> after a restart
    /// with <c>option_route_blinding</c> turned off. The caller disposes it.
    /// </summary>
    public static PaymentsTestNode CreateUsWithoutRouteBlinding()
    {
        var node = new PaymentsTestNode("us", 0x72);
        node.EnableTrampoline();
        node.Options.Features.OptionRouteBlinding = FeatureSupport.No;
        return node;
    }

    /// <summary>
    /// The failure as the payer reads it when it was made with the outer secret only (no trampoline layer): ours, with
    /// <paramref name="expected"/>.
    /// </summary>
    public void AssertOuterLayerOnly(TrampolineTestOnion onion, ReadOnlySpan<byte> reason, FailureCode expected)
    {
        var decrypted = Payer.FailureOnion.DecryptErrorPacket([onion.OuterSecret], reason);
        Assert.NotNull(decrypted);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(expected, decrypted.Code);
    }

    /// <summary>The payer's onion for an ordinary (non-trampoline) final HTLC to us.</summary>
    public async Task<ConstructedOnion> BuildOrdinaryFinalAsync(Hash paymentHash, ulong amountMsat, uint cltvExpiry)
    {
        var amount = LightningMoney.MilliSatoshis(amountMsat);
        var payload = new HopPayload(new AmtToForwardTlv(amount), new OutgoingCltvValueTlv(cltvExpiry),
                                     new PaymentDataTlv(s_invoiceSecret, amount));
        return Payer.Sphinx.ConstructWithSharedSecrets([new OnionHop(Us.NodeId, await SerializeAsync(payload))],
                                                       Enumerable.Repeat((byte)0x1D, 32).ToArray(), paymentHash);
    }

    /// <summary>
    /// The failure as the payer reads it: decrypted at the trampoline layer by our hop (index 0), with
    /// <paramref name="expected"/>.
    /// </summary>
    public FailureMessage AssertTrampolineLayer(TrampolineTestOnion onion, ReadOnlySpan<byte> reason,
                                                FailureCode expected)
    {
        var decrypted = Payer.TrampolineFailureOnion.DecryptTrampolineErrorPacket([onion.OuterSecret],
                                                                                  onion.TrampolineSecrets, reason);
        Assert.NotNull(decrypted);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(expected, decrypted.Failure.Code);
        Assert.NotNull(decrypted.Failure.Message);
        return decrypted.Failure.Message;
    }

    public void Dispose()
    {
        Payer.Dispose();
        Us.Dispose();
        Next.Dispose();
    }

    private async Task<TrampolineOnion> BuildTrampolineAsync(Hash paymentHash,
                                                             params (CompactPubKey NodeId, HopPayload Payload)[] hops)
    {
        var onionHops = new List<OnionHop>();
        foreach (var (nodeId, payload) in hops)
            onionHops.Add(new OnionHop(nodeId, await SerializeAsync(payload)));

        return Payer.TrampolineOnion.Build(onionHops, Enumerable.Repeat((byte)0x2C, 32).ToArray(), paymentHash,
                                           TrampolineOnionSizePolicy.Exact);
    }

    private async Task<TrampolineTestOnion> BuildOuterAsync(Hash paymentHash, TrampolineOnion trampoline,
                                                            LightningMoney amount, uint cltvExpiry,
                                                            CompactPubKey? outerPathKey = null)
    {
        var tlvs = new List<Domain.Protocol.Tlv.BaseTlv>
        {
            new AmtToForwardTlv(amount),
            new OutgoingCltvValueTlv(cltvExpiry),
            new PaymentDataTlv(s_outerSecret, amount),
            new TrampolineOnionPacketTlv(trampoline.Packet)
        };
        if (outerPathKey is { } pathKey)
            tlvs.Add(new CurrentPathKeyTlv(pathKey));
        var payload = new HopPayload(tlvs.ToArray());
        var outer = Payer.Sphinx.ConstructWithSharedSecrets([new OnionHop(Us.NodeId, await SerializeAsync(payload))],
                                                            Enumerable.Repeat((byte)0x1C, 32).ToArray(), paymentHash);
        return new TrampolineTestOnion(outer.Packet.ToBytes(), outer.SharedSecrets[0], trampoline.SharedSecrets,
                                       SHA256.HashData(trampoline.Packet.ToBytes()));
    }

    private async Task<byte[]> SerializeAsync(HopPayload payload)
    {
        using var stream = new MemoryStream();
        await Payer.HopPayloadSerializer.SerializeAsync(payload, stream);
        return stream.ToArray();
    }
}

/// <summary>
/// The outer onion's bytes and the secrets the payer keeps to read a failure of it, with the sha256 of the trampoline
/// packet (what a blinded trampoline hop's <c>invalid_onion_blinding</c> carries).
/// </summary>
internal sealed record TrampolineTestOnion(byte[] Packet, Secret OuterSecret, IReadOnlyList<Secret> TrampolineSecrets,
                                           byte[] TrampolineSha256);