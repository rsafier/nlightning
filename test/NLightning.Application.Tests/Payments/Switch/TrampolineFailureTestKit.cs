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
    public void AssertTrampolineLayer(TrampolineTestOnion onion, ReadOnlySpan<byte> reason, FailureCode expected)
    {
        var decrypted = Payer.TrampolineFailureOnion.DecryptTrampolineErrorPacket([onion.OuterSecret],
                                                                                  onion.TrampolineSecrets, reason);
        Assert.NotNull(decrypted);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(expected, decrypted.Failure.Code);
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
                                                            LightningMoney amount, uint cltvExpiry)
    {
        var payload = new HopPayload(new AmtToForwardTlv(amount), new OutgoingCltvValueTlv(cltvExpiry),
                                     new PaymentDataTlv(s_outerSecret, amount),
                                     new TrampolineOnionPacketTlv(trampoline.Packet));
        var outer = Payer.Sphinx.ConstructWithSharedSecrets([new OnionHop(Us.NodeId, await SerializeAsync(payload))],
                                                            Enumerable.Repeat((byte)0x1C, 32).ToArray(), paymentHash);
        return new TrampolineTestOnion(outer.Packet.ToBytes(), outer.SharedSecrets[0], trampoline.SharedSecrets);
    }

    private async Task<byte[]> SerializeAsync(HopPayload payload)
    {
        using var stream = new MemoryStream();
        await Payer.HopPayloadSerializer.SerializeAsync(payload, stream);
        return stream.ToArray();
    }
}

/// <summary>The outer onion's bytes and the secrets the payer keeps to read a failure of it.</summary>
internal sealed record TrampolineTestOnion(byte[] Packet, Secret OuterSecret, IReadOnlyList<Secret> TrampolineSecrets);