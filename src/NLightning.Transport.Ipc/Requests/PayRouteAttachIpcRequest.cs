using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Request for PayRouteAttach (ClientCommand 56, NL-1276): routes to attach to a <c>payroute</c> payment still in
/// flight. Keys 0-6 mirror <see cref="PayRouteIpcRequest"/>'s; the response is a <c>PayRouteIpcResponse</c> whose
/// route outcomes are this call's routes.
/// </summary>
[MessagePackObject]
public sealed class PayRouteAttachIpcRequest
{
    /// <summary>The BOLT 11 invoice being paid, or null when <see cref="PaymentHash"/> is given instead.</summary>
    [Key(0)] public string? Bolt11 { get; init; }

    /// <summary>The raw payment hash, or null when <see cref="Bolt11"/> is given instead.</summary>
    [Key(1)] public Hash? PaymentHash { get; init; }

    /// <summary>The payment secret of the raw form, or null.</summary>
    [Key(2)] public Secret? PaymentSecret { get; init; }

    /// <summary>The payment's <c>total_msat</c>, in msat, or null.</summary>
    [Key(3)] public ulong? TotalMsatMsat { get; init; }

    /// <summary>The routes to attach, our peer first on each.</summary>
    [Key(4)] public required List<PayRouteRouteIpcInfo> Routes { get; init; }

    /// <summary>How long the daemon waits for the payment's outcome, in seconds, or null for its default (60).</summary>
    [Key(5)] public uint? TimeoutSeconds { get; init; }

    /// <summary>The fee limit of the parts in flight and these routes together, in msat, or null for the
    /// payment's.</summary>
    [Key(6)] public ulong? MaxFeeMsat { get; init; }

    public PayRouteAttachClientRequest ToClientRequest()
    {
        return new PayRouteAttachClientRequest
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
            MaxFeeMsat = MaxFeeMsat
        };
    }
}