using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Trampoline.Harness;

using Application.Payments.Routing;
using Application.Payments.Trampoline;
using Bolt11.Models;
using Channels.Harness;
using Domain.Channels.Commitments.Events;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.ValueObjects;
using Domain.Serialization.Interfaces;

/// <summary>
/// Onion helpers of <see cref="TrampolineHarness"/>: plain routes over the harness's channels, hand-built trampoline
/// onions (<see cref="ITrampolineOnionService"/>) carried in an outer Sphinx onion (<see cref="ISphinxService"/>) whose
/// final payload holds TLV 20, and the payer's two-layer failure decryption.
/// </summary>
internal sealed partial class TrampolineHarness
{
    /// <summary>Blocks added above the invoice's <c>min_final_cltv_expiry_delta</c> for the final CLTV.</summary>
    public const uint FinalCltvMargin = 3;

    /// <summary>Creates an invoice on <paramref name="payee"/> (BOLT 11, bit 57 when it advertises trampoline).</summary>
    public static Task<InvoiceModel> CreateInvoiceAsync(SwitchNode payee, LightningMoney amount,
                                                        string description = "trampoline") =>
        payee.Invoices.CreateInvoiceAsync(amount, description, null, TestContext.Current.CancellationToken);

    /// <summary>The BOLT 11 invoice as a payer reads it.</summary>
    public static Invoice Decode(InvoiceModel invoice) => Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest);

    /// <summary>The final CLTV a payer uses for <paramref name="invoice"/> at <see cref="BlockHeight"/>.</summary>
    public static uint FinalCltvOf(InvoiceModel invoice) =>
        BlockHeight + Decode(invoice).MinFinalCltvExpiry + FinalCltvMargin;

    /// <summary>
    /// The route over <paramref name="hops"/> (the payer's peer first, the payee last), each forwarder charging its
    /// node's routing policy (fee on the amount it forwards, its CLTV delta): the payee gets <paramref name="amount"/>
    /// at <paramref name="finalCltv"/>. Each forwarder's <c>short_channel_id</c> is its first channel to the next hop.
    /// </summary>
    public PaymentRoute BuildRoute(IReadOnlyList<SwitchNode> hops, LightningMoney amount, uint finalCltv,
                                   Hash paymentHash, Secret paymentSecret, LightningMoney? totalAmount = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(hops.Count);

        var routeHops = new RouteHop[hops.Count];
        routeHops[^1] = new RouteHop(hops[^1].NodeId, amount, finalCltv, null);
        var incomingAmount = amount;
        var incomingCltv = finalCltv;
        for (var i = hops.Count - 2; i >= 0; i--)
        {
            var forwarder = hops[i];
            routeHops[i] = new RouteHop(forwarder.NodeId, incomingAmount, incomingCltv,
                                        ChannelBetween(forwarder, hops[i + 1]).Scid);
            incomingAmount += ThreeNodeHarness.ForwardingFeeOf(forwarder.Options.Routing, incomingAmount);
            incomingCltv += forwarder.Options.Routing.CltvExpiryDelta;
        }

        return new PaymentRoute(routeHops, incomingAmount, incomingCltv, paymentHash, paymentSecret,
                                totalAmount: totalAmount);
    }

    /// <summary>
    /// <paramref name="payer"/> builds the outer onion of <paramref name="route"/> (every hop's payload as
    /// <see cref="PaymentOnionFactory.CreatePayload"/> makes it, except the final one when
    /// <paramref name="finalPayload"/> is given) and offers the first HTLC on <paramref name="firstChannel"/> with
    /// origin <c>Local</c>. The returned onion carries the outer shared secrets (to decrypt failures).
    /// </summary>
    public async Task<PaymentOnion> OfferAsync(SwitchNode payer, ChannelId firstChannel, PaymentRoute route,
                                               HopPayload? finalPayload = null)
    {
        var serializer = payer.Services.GetRequiredService<IHopPayloadSerializer>();
        var hops = new List<OnionHop>();
        foreach (var hop in route.Hops)
        {
            var payload = hop.IsFinal && finalPayload is not null
                              ? finalPayload
                              : PaymentOnionFactory.CreatePayload(hop, route);
            hops.Add(new OnionHop(hop.NodeId, await SerializeAsync(serializer, payload)));
        }

        var constructed = payer.Services.GetRequiredService<ISphinxService>()
                               .ConstructWithSharedSecrets(hops, new PrivKey(PaymentOnionFactory.CreateSessionKey()),
                                                           route.PaymentHash);
        var onion = new PaymentOnion(route, constructed.Packet, constructed.SharedSecrets);
        await payer.Operations.OfferHtlcAsync(firstChannel, route.FirstHopAmount, route.PaymentHash,
                                              route.FirstHopCltvExpiry, onion.Packet, null,
                                              HtlcOrigin.Local(route.PaymentHash));
        return onion;
    }

    /// <summary>
    /// A trampoline onion built by <paramref name="payer"/> over <paramref name="hops"/> (first trampoline hop first),
    /// with a fresh session key and <see cref="TrampolineOnionSizePolicy.Auto"/> (650 bytes).
    /// </summary>
    public static async Task<TrampolineOnion> BuildTrampolineOnionAsync(
        SwitchNode payer, Hash paymentHash, params (CompactPubKey NodeId, HopPayload Payload)[] hops)
    {
        var serializer = payer.Services.GetRequiredService<IHopPayloadSerializer>();
        var onionHops = new List<OnionHop>();
        foreach (var (nodeId, payload) in hops)
            onionHops.Add(new OnionHop(nodeId, await SerializeAsync(serializer, payload)));

        return payer.Services.GetRequiredService<ITrampolineOnionService>()
                    .Build(onionHops, new PrivKey(RandomNumberGenerator.GetBytes(32)), paymentHash,
                           TrampolineOnionSizePolicy.Auto(650));
    }

    /// <summary>
    /// The trampoline payload of the final trampoline node: <c>amt_to_forward</c>, <c>outgoing_cltv_value</c> and
    /// <c>payment_data</c> (the invoice's secret and total).
    /// </summary>
    public static HopPayload TrampolineFinalPayload(LightningMoney amount, uint cltv, Secret paymentSecret,
                                                    LightningMoney total) =>
        new(new AmtToForwardTlv(amount), new OutgoingCltvValueTlv(cltv), new PaymentDataTlv(paymentSecret, total));

    /// <summary>
    /// The outer final payload that carries a trampoline onion: <c>amt_to_forward</c>, <c>outgoing_cltv_value</c>,
    /// <c>payment_data</c> with the outer secret and total (the outer MPP set), and TLV 20.
    /// </summary>
    public static HopPayload OuterTrampolinePayload(LightningMoney amount, uint cltv, Secret outerSecret,
                                                    LightningMoney outerTotal, TrampolineOnion trampoline) =>
        new(new AmtToForwardTlv(amount), new OutgoingCltvValueTlv(cltv), new PaymentDataTlv(outerSecret, outerTotal),
            new TrampolineOnionPacketTlv(trampoline.Packet));

    /// <summary>
    /// <paramref name="payer"/>'s plan to pay <paramref name="invoice"/> to <paramref name="recipient"/> as the final
    /// (and only) trampoline hop: the trampoline onion with the recipient's final payload (<paramref name="amount"/>
    /// towards the invoice's total with its secret, unless overridden) and a random outer secret.
    /// </summary>
    /// <param name="amount">The inner <c>amt_to_forward</c> (default: the total).</param>
    /// <param name="total">The inner <c>payment_data.total_msat</c> (default: the invoice's amount).</param>
    /// <param name="paymentSecret">The inner payment secret (default: the invoice's).</param>
    public static async Task<TrampolinePaymentPlan> PlanFinalTrampolineAsync(SwitchNode payer, SwitchNode recipient,
                                                                             InvoiceModel invoice,
                                                                             LightningMoney? amount = null,
                                                                             LightningMoney? total = null,
                                                                             Secret? paymentSecret = null)
    {
        var innerTotal = total ?? invoice.Amount
                       ?? throw new ArgumentException("An amountless invoice needs a total.", nameof(total));
        var finalCltv = FinalCltvOf(invoice);
        var onion = await BuildTrampolineOnionAsync(payer, invoice.PaymentHash,
                                                    (recipient.NodeId,
                                                     TrampolineFinalPayload(amount ?? innerTotal, finalCltv,
                                                                            paymentSecret ?? invoice.PaymentSecret,
                                                                            innerTotal)));
        return new TrampolinePaymentPlan(payer, invoice, onion, finalCltv,
                                         new Secret(RandomNumberGenerator.GetBytes(32)), innerTotal);
    }

    /// <summary>
    /// <paramref name="payer"/>'s hand-built payment of <paramref name="invoice"/> through <paramref name="trampoline"/>
    /// as an intermediate trampoline node (the relay engine's input): the trampoline onion [trampoline: forward the
    /// invoice's amount to <paramref name="nextNodeId"/> (default: the recipient) with the final CLTV
    /// (<c>outgoing_node_id</c>, TLV 14); next node: the recipient's final payload], an outer CLTV of the final CLTV
    /// plus <paramref name="cltvDelta"/> and an outer total of the amount plus <paramref name="trampolineFee"/>: what the
    /// trampoline node's parts must add up to. Send its parts with <see cref="SendTrampolinePartAsync"/> and
    /// <c>[trampoline]</c> as the outer hops.
    /// </summary>
    public static async Task<TrampolinePaymentPlan> PlanRelayAsync(SwitchNode payer, SwitchNode trampoline,
                                                                   SwitchNode recipient, InvoiceModel invoice,
                                                                   LightningMoney trampolineFee, uint cltvDelta,
                                                                   CompactPubKey? nextNodeId = null)
    {
        var amount = invoice.Amount
                  ?? throw new ArgumentException("An amountless invoice cannot be relayed by hand.", nameof(invoice));
        var finalCltv = FinalCltvOf(invoice);
        var next = nextNodeId ?? recipient.NodeId;
        var relayPayload = TrampolineOnionFactory.CreateIntermediatePayload(amount, finalCltv, next);
        var finalPayload = TrampolineFinalPayload(amount, finalCltv, invoice.PaymentSecret, amount);
        var onion = await BuildTrampolineOnionAsync(payer, invoice.PaymentHash, (trampoline.NodeId, relayPayload),
                                                    (next, finalPayload));
        return new TrampolinePaymentPlan(payer, invoice, onion, finalCltv + cltvDelta,
                                         new Secret(RandomNumberGenerator.GetBytes(32)), amount + trampolineFee);
    }

    /// <summary>
    /// Sends one HTLC of <paramref name="plan"/>: <paramref name="part"/> to the last of <paramref name="outerHops"/>
    /// (the payer's peer first; every hop before the last forwards the outer onion as a plain hop) on
    /// <paramref name="firstChannel"/>, whose outer final payload promises <paramref name="outerTotal"/> (default: the
    /// plan's total) with the plan's outer secret and carries the plan's trampoline onion.
    /// </summary>
    public Task<PaymentOnion> SendTrampolinePartAsync(TrampolinePaymentPlan plan, ChannelId firstChannel,
                                                      IReadOnlyList<SwitchNode> outerHops, LightningMoney part,
                                                      LightningMoney? outerTotal = null)
    {
        var route = BuildRoute(outerHops, part, plan.FinalCltv, plan.Invoice.PaymentHash, plan.OuterSecret,
                               outerTotal ?? plan.Total);
        return OfferAsync(plan.Payer, firstChannel, route,
                          OuterTrampolinePayload(part, plan.FinalCltv, plan.OuterSecret, route.TotalAmount,
                                                 plan.Onion));
    }

    /// <summary>
    /// The payer's two-layer decryption of a failed HTLC (<see cref="ITrampolineFailureOnionService"/>): the outer
    /// route's secrets, then the trampoline route's. Null when neither layer authenticates it.
    /// </summary>
    public static TrampolineDecryptedFailure? DecryptTrampolineFailure(SwitchNode payer, PaymentOnion onion,
                                                                      TrampolineOnion trampoline,
                                                                      OutgoingHtlcFailed failed) =>
        payer.Services.GetRequiredService<ITrampolineFailureOnionService>()
             .DecryptTrampolineErrorPacket(onion.SharedSecrets, trampoline.SharedSecrets,
                                           failed.Removal.Reason.Span);

    private static async Task<byte[]> SerializeAsync(IHopPayloadSerializer serializer, HopPayload payload)
    {
        using var stream = new MemoryStream();
        await serializer.SerializeAsync(payload, stream);
        return stream.ToArray();
    }
}

/// <summary>
/// A payer's hand-built trampoline payment to a final trampoline recipient: the trampoline onion (built once, shared by
/// every part), the final CLTV of both layers, the outer payment secret and the total.
/// </summary>
internal sealed record TrampolinePaymentPlan(
    SwitchNode Payer,
    InvoiceModel Invoice,
    TrampolineOnion Onion,
    uint FinalCltv,
    Secret OuterSecret,
    LightningMoney Total);