namespace NLightning.Domain.Client.Responses;

using Payments.Trampoline;

/// <summary>
/// The totals of a <c>listforwards</c> query: the counts per status and the fees earned over the whole filtered set
/// (not just the page), and the HTLCs refused before a forward circuit since the process started (NL-598).
/// </summary>
/// <remarks>
/// NL-981: <see cref="Pending"/>, <see cref="Offered"/>, <see cref="Fulfilled"/>, <see cref="Failed"/> and
/// <see cref="FulfilledFeesMsat"/> count the forwards and the trampoline relays together (a relay is one forward
/// whatever its parts: collecting counts as pending, sending as offered); <see cref="TrampolineRelays"/> is the relays'
/// share of them.
/// </remarks>
public sealed class ForwardSummaryClientResponse
{
    public required int Pending { get; init; }
    public required int Offered { get; init; }
    public required int Fulfilled { get; init; }
    public required int Failed { get; init; }

    public int Total => Pending + Offered + Fulfilled + Failed;

    /// <summary>The fees earned (incoming − outgoing) of the fulfilled forwards over the filtered set, in msat, the
    /// fulfilled trampoline relays' fees included (NL-981).</summary>
    public required long FulfilledFeesMsat { get; init; }

    /// <summary>The trampoline relays' share of the totals (NL-981), the replaced failed attempts (NL-899) counted as
    /// failed; all zero when the node keeps no relays.</summary>
    public TrampolineRelayTotals TrampolineRelays { get; init; }

    /// <summary>How many incoming HTLCs were refused before a forward circuit since the process started, all reasons
    /// together (NL-598); the per-reason counts follow.</summary>
    public required long RefusedTotal { get; init; }

    /// <summary>The refusals of <see cref="RefusedTotal"/> by reason; reasons with a zero count are left out.</summary>
    public required IReadOnlyList<RefusedReasonCount> RefusedByReason { get; init; }
}

/// <summary>One reason's count of <see cref="ForwardSummaryClientResponse.RefusedByReason"/>.</summary>
/// <param name="Reason">The <see cref="RefusedHtlcReason"/> name.</param>
/// <param name="Count">How many HTLCs were refused for it since the process started.</param>
public sealed record RefusedReasonCount(string Reason, long Count);