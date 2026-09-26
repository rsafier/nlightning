namespace NLightning.Application.Payments.Send;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Routing;

/// <summary>
/// The in-memory state of one <c>PayInvoiceAsync</c> call while it has HTLCs in flight or retries to make (NL-270):
/// its parts, what it learnt from failures and its limits. Mutated only under the payment hash's lock.
/// </summary>
internal sealed class PaymentSession
{
    public PaymentSession(PaymentTarget target, string bolt11, LightningMoney amount, LightningMoney maxFee,
                          int maxParts, int maxAttempts, DateTimeOffset? deadline, DateTimeOffset createdAt)
    {
        Target = target;
        Bolt11 = bolt11;
        Amount = amount;
        MaxFee = maxFee;
        MaxParts = maxParts;
        MaxAttempts = maxAttempts;
        Deadline = deadline;
        CreatedAt = createdAt;
    }

    public PaymentTarget Target { get; }
    public string Bolt11 { get; }
    public Hash PaymentHash => Target.PaymentHash;

    /// <summary>What the payee must receive, all parts together.</summary>
    public LightningMoney Amount { get; }

    /// <summary>The most the parts in flight may pay in fees, together.</summary>
    public LightningMoney MaxFee { get; }

    public int MaxParts { get; }

    /// <summary>The most HTLCs (offers, refused ones included) this call may make.</summary>
    public int MaxAttempts { get; }

    /// <summary>No new HTLC after this (null: no deadline).</summary>
    public DateTimeOffset? Deadline { get; }

    /// <summary>When the stored payment was first created (kept by every replacement row).</summary>
    public DateTimeOffset CreatedAt { get; }

    public RouteConstraints Constraints { get; } = new();

    /// <summary>
    /// The BOLT 7 shadow CLTV offset of the payment's graph routes, chosen at its first round with a graph (null
    /// before).
    /// </summary>
    public uint? ShadowCltvOffset { get; set; }

    public List<PaymentPart> Parts { get; } = [];

    /// <summary>HTLCs offered, plus offers the engine refused.</summary>
    public int Attempts { get; set; }

    /// <summary>The most parts that were in flight at once.</summary>
    public int MaxPartsInFlight { get; set; }

    /// <summary>The caller stopped waiting through its cancellation token: no new HTLC.</summary>
    public bool StopRequested { get; set; }

    /// <summary>Why the payment must not be retried any more (a permanent or local failure); null while it may.</summary>
    public string? TerminalReason { get; set; }

    /// <summary>The last failure of a part: code, erring hop index and its description.</summary>
    public (FailureCode? Code, int? SourceIndex, string Reason)? LastFailure { get; set; }

    /// <summary>
    /// The hold times the hops reported in the verified <c>attribution_data</c> of the last part's failure (BOLT 4),
    /// with that part's HTLC, so the row records them when it records that HTLC.
    /// </summary>
    public (ChannelId ChannelId, ulong HtlcId, IReadOnlyList<TimeSpan> HoldTimes)? LastFailureHoldTimes { get; set; }

    /// <summary>A round is queued to run on the thread pool.</summary>
    public bool RoundScheduled { get; set; }

    /// <summary>The part whose route and HTLC the stored payment row records.</summary>
    public PaymentPart? PrimaryPart { get; set; }

    /// <summary>True once the stored row exists (created by the first round).</summary>
    public bool RowCreated { get; set; }

    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsCompleted => Completion.Task.IsCompleted;

    public IEnumerable<PaymentPart> InFlightParts => Parts.Where(p => p.Status == PaymentPartStatus.InFlight);

    public bool HasPartsInFlight => Parts.Any(p => p.Status == PaymentPartStatus.InFlight);

    /// <summary>What the payee still has to be sent: the amount minus what the parts in flight deliver.</summary>
    public ulong RemainingMsat =>
        Amount.MilliSatoshi - (ulong)InFlightParts.Sum(p => (decimal)p.Route.Amount.MilliSatoshi);

    /// <summary>Fees of the parts in flight.</summary>
    public ulong FeesInFlightMsat => (ulong)InFlightParts.Sum(p => (decimal)p.Route.Fee.MilliSatoshi);

    public bool IsPastDeadline(DateTimeOffset now) => Deadline is { } deadline && now >= deadline;

    public PaymentPart? FindPart(ChannelId channelId, ulong htlcId) =>
        Parts.FirstOrDefault(p => p.HtlcId == htlcId && p.Channel.ChannelId == channelId);
}

internal enum PaymentPartStatus
{
    /// <summary>Offered, not resolved yet.</summary>
    InFlight,

    /// <summary>Fulfilled by the payee.</summary>
    Succeeded,

    /// <summary>Failed irrevocably, or never offered.</summary>
    Failed
}

/// <summary>
/// One HTLC of a payment: the route it was sent on, with the per-hop shared secrets to read its failure.
/// </summary>
internal sealed class PaymentPart
{
    public PaymentPart(LocalChannelCandidate channel, PaymentRoute route, IReadOnlyList<PaymentHop> hops,
                       string description)
    {
        Channel = channel;
        Route = route;
        Hops = hops;
        Description = description;
    }

    public LocalChannelCandidate Channel { get; }
    public PaymentRoute Route { get; }

    /// <summary>The persisted form of the route (hop i receives from hop i-1), with the shared secrets.</summary>
    public IReadOnlyList<PaymentHop> Hops { get; }

    public string Description { get; }
    public ulong? HtlcId { get; set; }
    public PaymentPartStatus Status { get; set; } = PaymentPartStatus.InFlight;
}