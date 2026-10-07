namespace NLightning.Application.Payments.Send;

using Domain.Accounting.Labels;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Models;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Keysend;
using Routing;
using Trampoline;

/// <summary>
/// The in-memory state of one <c>PayInvoiceAsync</c> call while it has HTLCs in flight or retries to make (NL-270):
/// its parts, what it learnt from failures and its limits. Mutated only under the payment hash's lock.
/// </summary>
internal sealed class PaymentSession
{
    public PaymentSession(PaymentTarget target, string? bolt11, LightningMoney amount, LightningMoney maxFee,
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

    /// <summary>
    /// What the rounds route to. A payment through a trampoline node (NL-875) targets that node, with the attempt's
    /// random outer secret and total, and changes it with every trampoline attempt.
    /// </summary>
    public PaymentTarget Target { get; set; }

    /// <summary>
    /// The outgoing leg of a trampoline relay this session sends (NL-875, <see cref="ITrampolineLegSender"/>): its HTLCs
    /// carry <c>HtlcOrigin.Trampoline</c>, its row is marked <c>IsTrampolineRelay</c> and its end goes to the
    /// <see cref="ITrampolineLegObserver"/>. Null for our own payments.
    /// </summary>
    public TrampolineLegRequest? Leg { get; init; }

    /// <summary>Whether this session sends a trampoline relay's outgoing leg (<see cref="Leg"/>).</summary>
    public bool IsTrampolineRelay => Leg is not null;

    /// <summary>The payment's trampoline route and budget when we pay through a trampoline node (NL-875, payer side);
    /// null otherwise.</summary>
    public TrampolinePayerState? Trampoline { get; init; }

    /// <summary>The origin every HTLC of the session carries: <c>Trampoline(hash)</c> for a relay leg, else
    /// <c>Local(hash)</c>.</summary>
    public HtlcOrigin Origin => IsTrampolineRelay ? HtlcOrigin.Trampoline(PaymentHash) : HtlcOrigin.Local(PaymentHash);

    /// <summary>The payee's absolute final <c>outgoing_cltv_value</c> (a relay leg's next node, or the trampoline node
    /// of a payment through one), for the planner; null to compute it from the height and the target's delta.</summary>
    public uint? AbsoluteFinalCltv { get; set; }

    /// <summary>The highest <c>cltv_expiry</c> our HTLCs may carry (a relay leg); null: no cap.</summary>
    public uint? MaxFirstHopCltvExpiry { get; init; }

    /// <summary>The payee the stored row names when it is not the rounds' target (a payment through a trampoline
    /// node names the real payee); null for <see cref="Target"/>'s.</summary>
    public CompactPubKey? RecordedPayeeNodeId { get; init; }

    /// <summary>The payee the stored row names.</summary>
    public CompactPubKey PayeeNodeId => RecordedPayeeNodeId ?? Target.PayeeNodeId;

    /// <summary>How a relay leg ended when a part's failure decided it (an error from the next trampoline); null
    /// otherwise.</summary>
    public TrampolineLegFailure? LegFailure { get; set; }

    /// <summary>The message of the last part's decrypted failure, when one was read (a relay leg reports it).</summary>
    public FailureMessage? LastFailureMessage { get; set; }

    /// <summary>The relay leg's end was handed to the observer (once per session).</summary>
    public bool LegReported { get; set; }

    /// <summary>
    /// The recipient's blinded paths for a payment sent through one (ONION M5); null for an invoice payment.
    /// </summary>
    public IReadOnlyList<BlindedPaymentPath>? BlindedPaths { get; init; }

    /// <summary>
    /// The BOLT 12 offer and invoice the payment is for, stored with every row of the payment; null otherwise.
    /// </summary>
    public Bolt12PaymentDetails? Bolt12 { get; init; }

    /// <summary>
    /// The keysend records of the payee's payload (our preimage and the custom records) for a keysend payment; null
    /// for an invoice payment.
    /// </summary>
    public KeysendFinalRecords? Keysend { get; init; }

    /// <summary>The operator's label and tags every stored row of the payment carries (NL-602 A3-T1).</summary>
    public SourceLabels Labels { get; init; } = SourceLabels.None;

    /// <summary>
    /// The caller-supplied routes of a <c>payroute</c> call (NL-1082): offered exactly as given, in this order, and
    /// never re-planned — a refusal or failure ends its route and the caller decides what is next. Null for every
    /// planner-driven session.
    /// </summary>
    public IReadOnlyList<SuppliedRoutePart>? SuppliedRoutes { get; init; }

    /// <summary>Whether this session pays over caller-supplied routes (<see cref="SuppliedRoutes"/>).</summary>
    public bool ManualRoutes => SuppliedRoutes is not null;

    /// <summary>
    /// What the stored row keeps of <see cref="Keysend"/> (its custom records), or null.
    /// </summary>
    public KeysendDetails? KeysendDetails => Keysend is null ? null : new KeysendDetails(Keysend.CustomRecords);

    /// <summary>
    /// The payment pays one of our own invoices over a circular route (a rebalance, NL-609): every part leaves through
    /// one of our channels and comes back in through another.
    /// </summary>
    public bool IsCircular { get; init; }

    /// <summary>The only channel of ours the parts may leave through (<c>payinvoice --out</c>); null: any.</summary>
    public ChannelId? OutgoingChannelId { get; init; }

    /// <summary>Restricts every first hop to this set (LND outgoing_chan_ids); null allows any channel.</summary>
    public IReadOnlySet<ChannelId>? OutgoingChannelIds { get; init; }

    /// <summary>For a circular payment, the only channel of ours the parts may come back in through
    /// (<c>payinvoice --in</c>); null: any.</summary>
    public ChannelId? IncomingChannelId { get; init; }

    public string? Bolt11 { get; }
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

    /// <summary>
    /// The index the next stored part row gets (NL-321); reset when a retry replaces the attempt's rows.
    /// </summary>
    public int NextPartIndex { get; set; }

    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsCompleted => Completion.Task.IsCompleted;

    public IEnumerable<PaymentPart> InFlightParts => Parts.Where(p => p.Status == PaymentPartStatus.InFlight);

    public bool HasPartsInFlight => Parts.Any(p => p.Status == PaymentPartStatus.InFlight);

    /// <summary>
    /// What the rounds' target must receive, all parts together: <see cref="Amount"/>, or for a payment through a
    /// trampoline node the attempt's outer total (the amount plus the trampoline's fee).
    /// </summary>
    public ulong SendAmountMsat => Trampoline?.OuterTotal?.MilliSatoshi ?? Amount.MilliSatoshi;

    /// <summary>What the payee still has to be sent: the amount minus what the parts in flight deliver.</summary>
    public ulong RemainingMsat =>
        SendAmountMsat - (ulong)InFlightParts.Sum(p => (decimal)p.Route.Amount.MilliSatoshi);

    /// <summary>Routing fees of the parts in flight.</summary>
    public ulong FeesInFlightMsat => (ulong)InFlightParts.Sum(p => (decimal)p.Route.Fee.MilliSatoshi);

    /// <summary>
    /// What the payment pays beyond its routes' fees: the trampoline's fee (and, for a BOLT 12 recipient behind it, the
    /// blinded path's) of the current attempt; 0 without a trampoline.
    /// </summary>
    public ulong TrampolineFeeMsat => SendAmountMsat - Amount.MilliSatoshi;

    /// <summary>The fee the stored row records while parts are in flight: their routing fees plus the trampoline's.
    /// </summary>
    public ulong RecordedFeesInFlightMsat => FeesInFlightMsat + (HasPartsInFlight ? TrampolineFeeMsat : 0);

    /// <summary>Whether any HTLC of the session was offered (a part has an HTLC id).</summary>
    public bool EverOffered => Parts.Any(p => p.HtlcId is not null);

    public bool IsPastDeadline(DateTimeOffset now) => Deadline is { } deadline && now >= deadline;

    public PaymentPart? FindPart(ChannelId channelId, ulong htlcId) =>
        Parts.FirstOrDefault(p => p.HtlcId == htlcId && p.Channel.ChannelId == channelId);

    private readonly List<(IReadOnlyList<PaymentPart> Parts, TaskCompletionSource Resolved)> _partWaiters = [];

    /// <summary>
    /// Completes when none of <paramref name="parts"/> is in flight any more, or the session ends (NL-1276: a
    /// <c>payroute</c> shard call of an LND <c>SendToRouteV2</c> set answers with its own routes' outcomes).
    /// <see cref="SignalPartsChanged"/> wakes it after a part is resolved.
    /// </summary>
    public Task WhenPartsResolvedAsync(IReadOnlyList<PaymentPart> parts)
    {
        lock (_partWaiters)
        {
            if (IsCompleted || parts.All(p => p.Status != PaymentPartStatus.InFlight))
                return Task.CompletedTask;

            var resolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _partWaiters.Add((parts, resolved));
            return Task.WhenAny(resolved.Task, Completion.Task);
        }
    }

    /// <summary>Completes the <see cref="WhenPartsResolvedAsync"/> waits whose parts are all resolved.</summary>
    public void SignalPartsChanged()
    {
        lock (_partWaiters)
        {
            for (var i = _partWaiters.Count - 1; i >= 0; i--)
            {
                var (parts, resolved) = _partWaiters[i];
                if (parts.Any(p => p.Status == PaymentPartStatus.InFlight))
                    continue;

                resolved.TrySetResult();
                _partWaiters.RemoveAt(i);
            }
        }
    }
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

    /// <summary>
    /// The part's failure once it failed (NL-1082): the attributed code, the erring hop index and the local
    /// description — what a <c>payroute</c> call reports per route. Null while in flight or when fulfilled.
    /// </summary>
    public (FailureCode? Code, int? SourceIndex, string? Reason)? Failure { get; set; }

    /// <summary>When the part's HTLC was offered (null until then); a <c>payroute --attach</c> must come within the
    /// payee's <c>mpp_timeout</c> of the oldest part still in flight (NL-1276).</summary>
    public DateTimeOffset? OfferedAt { get; set; }

    /// <summary>For a payment through a trampoline node: the attempt whose trampoline onion the part carries.</summary>
    public int? TrampolineAttempt { get; init; }

    /// <summary>For a payment through a trampoline node: the shared secrets of that trampoline onion, first trampoline
    /// hop first.</summary>
    public IReadOnlyList<Secret>? TrampolineSecrets { get; init; }
}