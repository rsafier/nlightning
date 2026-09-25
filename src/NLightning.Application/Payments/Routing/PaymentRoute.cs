namespace NLightning.Application.Payments.Routing;

using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// A route for one HTLC (no MPP): the HTLC we offer to our peer and the onion layers after it.
/// </summary>
/// <remarks>
/// <see cref="Hops"/>[0] is our peer (the node our channel leads to) and the last hop is the payee, whose layer has no
/// <c>short_channel_id</c>. For every hop except the last, <c>Hops[i].AmountToForward</c> and
/// <c>Hops[i].OutgoingCltvValue</c> are the HTLC that hop <c>i</c> offers to hop <c>i + 1</c>.
/// </remarks>
public sealed class PaymentRoute
{
    /// <summary>
    /// The onion layers, our peer first, the payee last.
    /// </summary>
    public IReadOnlyList<RouteHop> Hops { get; }

    /// <summary>
    /// <c>amount_msat</c> of the HTLC we offer to <see cref="FirstHopNodeId"/>.
    /// </summary>
    public LightningMoney FirstHopAmount { get; }

    /// <summary>
    /// <c>cltv_expiry</c> of the HTLC we offer to <see cref="FirstHopNodeId"/>.
    /// </summary>
    public uint FirstHopCltvExpiry { get; }

    public Hash PaymentHash { get; }
    public Secret PaymentSecret { get; }
    public ReadOnlyMemory<byte>? PaymentMetadata { get; }

    /// <summary>
    /// The peer our channel leads to.
    /// </summary>
    public CompactPubKey FirstHopNodeId => Hops[0].NodeId;

    public CompactPubKey PayeeNodeId => Hops[^1].NodeId;

    /// <summary>
    /// What the payee receives.
    /// </summary>
    public LightningMoney Amount => Hops[^1].AmountToForward;

    /// <summary>
    /// The routing fees paid to intermediate hops (zero for a direct payment).
    /// </summary>
    public LightningMoney Fee => FirstHopAmount - Amount;

    public PaymentRoute(IReadOnlyList<RouteHop> hops, LightningMoney firstHopAmount, uint firstHopCltvExpiry,
                        Hash paymentHash, Secret paymentSecret, ReadOnlyMemory<byte>? paymentMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(hops);
        ArgumentNullException.ThrowIfNull(firstHopAmount);
        if (hops.Count == 0)
            throw new ArgumentException("A route has at least one hop.", nameof(hops));
        if (!hops[^1].IsFinal)
            throw new ArgumentException("The last hop is the payee and has no short_channel_id.", nameof(hops));
        if (hops.Take(hops.Count - 1).Any(hop => hop.IsFinal))
            throw new ArgumentException("Every hop but the last has a short_channel_id.", nameof(hops));
        if (firstHopAmount < hops[^1].AmountToForward)
            throw new ArgumentException("The first HTLC cannot carry less than the payee receives.",
                                        nameof(firstHopAmount));

        Hops = hops;
        FirstHopAmount = firstHopAmount;
        FirstHopCltvExpiry = firstHopCltvExpiry;
        PaymentHash = paymentHash;
        PaymentSecret = paymentSecret;
        PaymentMetadata = paymentMetadata;
    }
}