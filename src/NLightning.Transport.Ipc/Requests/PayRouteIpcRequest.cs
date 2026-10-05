using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Request for PayRoute (ClientCommand 48): the routes to pay over, exactly as given.
/// </summary>
[MessagePackObject]
public sealed class PayRouteIpcRequest
{
    /// <summary>
    /// The BOLT 11 invoice to pay, or null when <see cref="PaymentHash"/> is given instead.
    /// </summary>
    [Key(0)] public string? Bolt11 { get; init; }

    /// <summary>
    /// A raw payment hash, or null when <see cref="Bolt11"/> is given instead.
    /// </summary>
    [Key(1)] public Hash? PaymentHash { get; init; }

    /// <summary>
    /// The payment secret of the raw form, or null (the invoice form takes it from the invoice).
    /// </summary>
    [Key(2)] public Secret? PaymentSecret { get; init; }

    /// <summary>
    /// The payment's <c>total_msat</c> every shard reports, in msat, or null for the invoice amount.
    /// </summary>
    [Key(3)] public ulong? TotalMsatMsat { get; init; }

    /// <summary>
    /// The routes to pay over, our peer first on each.
    /// </summary>
    [Key(4)] public required List<PayRouteRouteIpcInfo> Routes { get; init; }

    /// <summary>
    /// How long the daemon waits for the outcome, in seconds, or null for its default (60); an older client sends
    /// none.
    /// </summary>
    [Key(5)] public uint? TimeoutSeconds { get; init; }

    /// <summary>
    /// The most the payment may pay in routing fees, in msat, or null for the daemon's default (NL-270).
    /// </summary>
    [Key(6)] public ulong? MaxFeeMsat { get; init; }

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>), or null; an older client sends none.
    /// </summary>
    [Key(7)] public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>), or null for none.
    /// </summary>
    [Key(8)] public List<string>? Tags { get; init; }

    public PayRouteClientRequest ToClientRequest()
    {
        return new PayRouteClientRequest
        {
            Bolt11 = Bolt11,
            PaymentHash = PaymentHash,
            PaymentSecret = PaymentSecret,
            TotalMsatMsat = TotalMsatMsat,
            Routes = Routes.Select(r => new PayRouteRouteClientInfo(
                                     r.FirstHopChannel, r.FirstHopAmountMsat, r.FirstHopCltv,
                                     r.Hops.Select(h => new PayRouteHopClientInfo(h.NodeId, h.OutgoingShortChannelId,
                                                                h.AmountToForwardMsat, h.OutgoingCltvValue))
                                      .ToList()))
                           .ToList(),
            TimeoutSeconds = TimeoutSeconds ?? 60,
            MaxFeeMsat = MaxFeeMsat,
            Label = Label,
            Tags = Tags
        };
    }
}

/// <summary>One caller-supplied route of a <see cref="PayRouteIpcRequest"/>.</summary>
[MessagePackObject]
public sealed class PayRouteRouteIpcInfo
{
    /// <summary>
    /// Our channel of the first HTLC (a channel id, 64 hex characters, or a short channel id
    /// <c>BLOCKxTXxOUTPUT</c>).
    /// </summary>
    [Key(0)] public required string FirstHopChannel { get; init; }

    /// <summary>What our first HTLC carries, in msat.</summary>
    [Key(1)] public ulong FirstHopAmountMsat { get; init; }

    /// <summary>Our first HTLC's <c>cltv_expiry</c>.</summary>
    [Key(2)] public uint FirstHopCltv { get; init; }

    /// <summary>The hops after ours (our peer first, the payee last).</summary>
    [Key(3)] public required List<PayRouteHopIpcInfo> Hops { get; init; }
}

/// <summary>One hop of a <see cref="PayRouteRouteIpcInfo"/>.</summary>
[MessagePackObject]
public sealed class PayRouteHopIpcInfo
{
    /// <summary>The node.</summary>
    [Key(0)] public required CompactPubKey NodeId { get; init; }

    /// <summary>
    /// The channel it forwards over (the 8-byte short channel id as a number); null on the payee's final hop.
    /// </summary>
    [Key(1)] public ulong? OutgoingShortChannelId { get; init; }

    /// <summary>What the hop forwards onward, in msat.</summary>
    [Key(2)] public ulong AmountToForwardMsat { get; init; }

    /// <summary>The HTLC it forwards' absolute <c>outgoing_cltv_value</c>.</summary>
    [Key(3)] public uint OutgoingCltvValue { get; init; }
}