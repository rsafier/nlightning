using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;
using Domain.Money;

/// <summary>
/// Response for ListForwards (ClientCommand 40, NL-597): a page of forwards, newest first, plus the totals over the
/// whole filtered set and the refused-HTLC counters (NL-598).
/// </summary>
[MessagePackObject]
public sealed class ListForwardsIpcResponse
{
    /// <summary>The page of forwards, newest first.</summary>
    [Key(0)] public required List<ForwardInfoIpcResponse> Forwards { get; init; }

    /// <summary>The counts and fees over the whole filtered set, with the refused counters (NL-598).</summary>
    [Key(1)] public required ForwardSummaryIpcResponse Summary { get; init; }

    /// <summary>The trampoline payments we relayed (NL-875), newest first; null from a daemon before them.</summary>
    [Key(2)] public List<TrampolineRelayIpcResponse>? TrampolineRelays { get; init; }

    public static ListForwardsIpcResponse FromClientResponse(ListForwardsClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new ListForwardsIpcResponse
        {
            Forwards = clientResponse.Forwards.Select(ForwardInfoIpcResponse.FromClientResponse).ToList(),
            Summary = ForwardSummaryIpcResponse.FromClientResponse(clientResponse.Summary),
            TrampolineRelays = clientResponse.TrampolineRelays.Select(TrampolineRelayIpcResponse.FromClientResponse)
                                             .ToList()
        };
    }
}

/// <summary>One trampoline payment we relayed over the wire (NL-875, a <c>listforwards</c> row of kind
/// <c>trampoline</c>).</summary>
[MessagePackObject]
public sealed class TrampolineRelayIpcResponse
{
    /// <summary>The payment hash, 64 hex characters.</summary>
    [Key(0)] public required string PaymentHash { get; init; }

    /// <summary>The <c>TrampolineRelayStatus</c> (0 collecting, 1 sending, 2 fulfilled, 3 failed).</summary>
    [Key(1)] public required byte Status { get; init; }

    /// <summary>The incoming parts stored.</summary>
    [Key(2)] public required int Parts { get; init; }

    /// <summary>The channels the parts came in on (64 hex each), in part order.</summary>
    [Key(3)] public required List<string> IncomingChannelIds { get; init; }

    /// <summary>Each incoming channel's scid as <c>block x tx x output</c>, or null to show the channel id.</summary>
    [Key(4)] public required List<string?> IncomingChannelScids { get; init; }

    /// <summary>The sum of the incoming parts, msat.</summary>
    [Key(5)] public required long IncomingAmountMsat { get; init; }

    /// <summary>The outer <c>total_msat</c> the incoming set had to reach, msat.</summary>
    [Key(6)] public required long IncomingTotalMsat { get; init; }

    /// <summary>What the next node had to receive, msat.</summary>
    [Key(7)] public required long AmountOutMsat { get; init; }

    /// <summary>What the relay earned once fulfilled, msat; null before.</summary>
    [Key(8)] public long? FeeEarnedMsat { get; init; }

    /// <summary>The next trampoline node (66 hex), or null for the recipient's blinded paths.</summary>
    [Key(9)] public string? NextNodeId { get; init; }

    /// <summary>When the first part arrived, Unix seconds.</summary>
    [Key(10)] public required long CreatedAtUnixSeconds { get; init; }

    /// <summary>When the relay was fulfilled or failed, Unix seconds.</summary>
    [Key(11)] public long? CompletedAtUnixSeconds { get; init; }

    /// <summary>The BOLT 4 failure code we answered with, when we made it ourselves.</summary>
    [Key(12)] public ushort? FailureCode { get; init; }

    /// <summary>The failure code's name (or hex), or null.</summary>
    [Key(13)] public string? FailureCodeName { get; init; }

    /// <summary>A failed attempt a payer's retry replaced (NL-899): its number for the hash, from 1; null for the
    /// hash's relay now (and from a daemon before NL-899).</summary>
    [Key(14)] public int? ReplacedAttempt { get; init; }

    public static TrampolineRelayIpcResponse FromClientResponse(TrampolineRelayInfoClientResponse relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        return new TrampolineRelayIpcResponse
        {
            PaymentHash = relay.PaymentHash.ToString(),
            Status = (byte)relay.Status,
            Parts = relay.Parts,
            IncomingChannelIds = relay.IncomingChannelIds.Select(c => c.ToString()).ToList(),
            IncomingChannelScids = relay.IncomingChannelScids.ToList(),
            IncomingAmountMsat = checked((long)relay.IncomingAmount.MilliSatoshi),
            IncomingTotalMsat = checked((long)relay.IncomingTotal.MilliSatoshi),
            AmountOutMsat = checked((long)relay.AmountOut.MilliSatoshi),
            FeeEarnedMsat = relay.FeeEarned is { } fee ? checked((long)fee.MilliSatoshi) : null,
            NextNodeId = relay.NextNodeId?.ToString(),
            CreatedAtUnixSeconds = relay.CreatedAt.ToUnixTimeSeconds(),
            CompletedAtUnixSeconds = relay.CompletedAt?.ToUnixTimeSeconds(),
            FailureCode = relay.FailureCode,
            FailureCodeName = relay.FailureCodeName,
            ReplacedAttempt = relay.ReplacedAttempt
        };
    }
}

/// <summary>One forwarded payment over the wire (NL-597).</summary>
[MessagePackObject]
public sealed class ForwardInfoIpcResponse
{
    /// <summary>The channel of the incoming HTLC, 64 hex characters.</summary>
    [Key(0)] public required string IncomingChannelId { get; init; }

    /// <summary>The id of the incoming HTLC.</summary>
    [Key(1)] public required ulong IncomingHtlcId { get; init; }

    /// <summary>The incoming amount, msat.</summary>
    [Key(2)] public required long IncomingAmountMsat { get; init; }

    /// <summary>The incoming <c>cltv_expiry</c>.</summary>
    [Key(3)] public required uint IncomingCltvExpiry { get; init; }

    /// <summary>The requested outgoing <c>short_channel_id</c>, as <c>block x tx x output</c>.</summary>
    [Key(4)] public required string OutgoingShortChannelId { get; init; }

    /// <summary>The channel the outgoing HTLC was offered on (64 hex), or null when not offered yet.</summary>
    [Key(5)] public string? OutgoingChannelId { get; init; }

    /// <summary>The id of the outgoing HTLC, or null when not offered yet.</summary>
    [Key(6)] public ulong? OutgoingHtlcId { get; init; }

    /// <summary>The outgoing amount, msat.</summary>
    [Key(7)] public required long OutgoingAmountMsat { get; init; }

    /// <summary>The outgoing <c>cltv_expiry</c>.</summary>
    [Key(8)] public required uint OutgoingCltvExpiry { get; init; }

    /// <summary>The fee we earn (incoming − outgoing), msat.</summary>
    [Key(9)] public required long FeeMsat { get; init; }

    /// <summary>The payment hash, 64 hex characters.</summary>
    [Key(10)] public required string PaymentHash { get; init; }

    /// <summary>When the circuit was created, Unix seconds.</summary>
    [Key(11)] public required long CreatedAtUnixSeconds { get; init; }

    /// <summary>When the circuit resolved, Unix seconds, or null while it runs.</summary>
    [Key(12)] public long? ResolvedAtUnixSeconds { get; init; }

    /// <summary>The <c>ForwardCircuitStatus</c> (0 pending, 1 offered, 2 fulfilled, 3 failed).</summary>
    [Key(13)] public required byte Status { get; init; }

    /// <summary>The BOLT 4 failure code, when it was sent in the clear and this circuit failed.</summary>
    [Key(14)] public ushort? FailureCode { get; init; }

    /// <summary>The failure code's name (or hex when unknown), or null when there is no code.</summary>
    [Key(15)] public string? FailureCodeName { get; init; }

    /// <summary>The channel the failure is about (64 hex), or null when the offer never went out.</summary>
    [Key(16)] public string? FailureSource { get; init; }

    /// <summary>The incoming channel's scid as <c>block x tx x output</c>, when known (NL-597); null shows the
    /// channel id instead.</summary>
    [Key(17)] public string? IncomingChannelScid { get; init; }

    /// <summary>The outgoing channel's scid, same rule (NL-597).</summary>
    [Key(18)] public string? OutgoingChannelScid { get; init; }

    /// <summary>The failure source's scid, same rule (NL-597).</summary>
    [Key(19)] public string? FailureSourceScid { get; init; }

    public static ForwardInfoIpcResponse FromClientResponse(ForwardInfoClientResponse forward) =>
        new()
        {
            IncomingChannelId = forward.IncomingChannelId.ToString(),
            IncomingHtlcId = forward.IncomingHtlcId,
            IncomingAmountMsat = ToMsat(forward.IncomingAmount),
            IncomingCltvExpiry = forward.IncomingCltvExpiry,
            OutgoingShortChannelId = forward.OutgoingShortChannelId.ToString(),
            OutgoingChannelId = forward.OutgoingChannelId?.ToString(),
            OutgoingHtlcId = forward.OutgoingHtlcId,
            OutgoingAmountMsat = ToMsat(forward.OutgoingAmount),
            OutgoingCltvExpiry = forward.OutgoingCltvExpiry,
            FeeMsat = ToMsat(forward.Fee),
            PaymentHash = forward.PaymentHash.ToString(),
            CreatedAtUnixSeconds = ToUnixSeconds(forward.CreatedAt),
            ResolvedAtUnixSeconds = forward.ResolvedAt is { } resolved ? ToUnixSeconds(resolved) : null,
            Status = (byte)forward.Status,
            FailureCode = forward.FailureCode,
            FailureCodeName = forward.FailureCodeName,
            FailureSource = forward.FailureSource?.ToString(),
            IncomingChannelScid = forward.IncomingChannelScid,
            OutgoingChannelScid = forward.OutgoingChannelScid,
            FailureSourceScid = forward.FailureSourceScid
        };

    private static long ToMsat(LightningMoney amount) => checked((long)amount.MilliSatoshi);

    private static long ToUnixSeconds(DateTimeOffset time) =>
        (long)Math.Floor(time.ToUnixTimeMilliseconds() / 1000.0);
}

/// <summary>The totals of a <c>listforwards</c> query over the wire (NL-597/NL-598).</summary>
[MessagePackObject]
public sealed class ForwardSummaryIpcResponse
{
    /// <summary>Circuits not offered yet, over the filtered set.</summary>
    [Key(0)] public required int Pending { get; init; }

    /// <summary>Circuits whose outgoing HTLC is live, over the filtered set.</summary>
    [Key(1)] public required int Offered { get; init; }

    /// <summary>Circuits fulfilled, over the filtered set.</summary>
    [Key(2)] public required int Fulfilled { get; init; }

    /// <summary>Circuits failed, over the filtered set.</summary>
    [Key(3)] public required int Failed { get; init; }

    /// <summary>The fees earned of the fulfilled circuits over the filtered set, msat; the fulfilled trampoline
    /// relays' fees included since NL-981 (as the counts 0-3 include the relays).</summary>
    [Key(4)] public required long FulfilledFeesMsat { get; init; }

    /// <summary>HTLCs refused before a forward circuit since the process started, all reasons (NL-598).</summary>
    [Key(5)] public required long RefusedTotal { get; init; }

    /// <summary>The refusals by reason (the <see cref="Domain.Payments.RefusedHtlcReason"/> name); reasons with a
    /// zero count are left out (NL-598).</summary>
    [Key(6)] public required List<RefusedReasonCountIpc> RefusedByReason { get; init; }

    /// <summary>The trampoline relays still collecting their parts, a share of <see cref="Pending"/> (NL-981).</summary>
    [Key(7)] public int TrampolineCollecting { get; init; }

    /// <summary>The trampoline relays whose outgoing payment runs, a share of <see cref="Offered"/> (NL-981).</summary>
    [Key(8)] public int TrampolineSending { get; init; }

    /// <summary>The trampoline relays fulfilled, a share of <see cref="Fulfilled"/> (NL-981).</summary>
    [Key(9)] public int TrampolineFulfilled { get; init; }

    /// <summary>The trampoline relays failed, replaced attempts (NL-899) included; a share of <see cref="Failed"/>
    /// (NL-981).</summary>
    [Key(10)] public int TrampolineFailed { get; init; }

    /// <summary>The fees the fulfilled trampoline relays earned, msat; a share of <see cref="FulfilledFeesMsat"/>
    /// (NL-981).</summary>
    [Key(11)] public long TrampolineFulfilledFeesMsat { get; init; }

    public static ForwardSummaryIpcResponse FromClientResponse(ForwardSummaryClientResponse summary) =>
        new()
        {
            Pending = summary.Pending,
            Offered = summary.Offered,
            Fulfilled = summary.Fulfilled,
            Failed = summary.Failed,
            FulfilledFeesMsat = summary.FulfilledFeesMsat,
            RefusedTotal = summary.RefusedTotal,
            RefusedByReason = summary.RefusedByReason.Select(RefusedReasonCountIpc.From).ToList(),
            TrampolineCollecting = summary.TrampolineRelays.Collecting,
            TrampolineSending = summary.TrampolineRelays.Sending,
            TrampolineFulfilled = summary.TrampolineRelays.Fulfilled,
            TrampolineFailed = summary.TrampolineRelays.Failed,
            TrampolineFulfilledFeesMsat = summary.TrampolineRelays.FulfilledFeesMsat
        };
}

/// <summary>One refused reason's count over the wire (NL-598).</summary>
[MessagePackObject]
public sealed class RefusedReasonCountIpc
{
    /// <summary>The <see cref="Domain.Payments.RefusedHtlcReason"/> name.</summary>
    [Key(0)] public required string Reason { get; init; }

    /// <summary>How many HTLCs were refused for it since the process started.</summary>
    [Key(1)] public required long Count { get; init; }

    public static RefusedReasonCountIpc From(RefusedReasonCount count) => new()
    {
        Reason = count.Reason,
        Count = count.Count
    };
}