namespace NLightning.Domain.Payments.Models;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;
using Money;

/// <summary>
/// A forward we are carrying: the incoming HTLC and the outgoing HTLC that continues it (ONION M4-T7).
/// </summary>
/// <remarks>
/// <para>Keyed by the incoming side (<see cref="IncomingChannelId"/>, <see cref="IncomingHtlcId"/>); once offered it
/// is also found by the outgoing side. It is persisted (<c>IForwardCircuitDbRepository</c>) as
/// <see cref="ForwardCircuitStatus.Pending"/> before the outgoing HTLC is offered, and the outgoing add carries
/// <c>HtlcOrigin.Forwarded(IncomingChannelId, IncomingHtlcId)</c>, so a restart can always route the outgoing
/// resolution back upstream: the preimage immediately, a failure only once the downstream removal is irrevocable,
/// wrapped with <see cref="IncomingSharedSecret"/>.</para>
/// <para>Status moves only forward: Pending → Offered → Fulfilled | Failed, or Pending → Failed (the offer was
/// refused). The mutators throw <see cref="InvalidOperationException"/> otherwise.</para>
/// </remarks>
public sealed class ForwardCircuitModel
{
    public ChannelId IncomingChannelId { get; }
    public ulong IncomingHtlcId { get; }
    public LightningMoney IncomingAmount { get; }
    public uint IncomingCltvExpiry { get; }
    public Hash PaymentHash { get; }

    /// <summary>
    /// The shared secret of the incoming onion: the key that wraps (or creates) the error returned upstream.
    /// </summary>
    public Secret IncomingSharedSecret { get; }

    /// <summary>
    /// The <c>short_channel_id</c> the incoming onion asked us to forward to (real or alias).
    /// </summary>
    public ShortChannelId OutgoingShortChannelId { get; }

    /// <summary>
    /// <c>amt_to_forward</c>: the amount of the outgoing HTLC.
    /// </summary>
    public LightningMoney OutgoingAmount { get; }

    /// <summary>
    /// <c>outgoing_cltv_value</c>: the <c>cltv_expiry</c> of the outgoing HTLC.
    /// </summary>
    public uint OutgoingCltvExpiry { get; }

    public DateTimeOffset CreatedAt { get; }

    public ForwardCircuitStatus Status { get; private set; }

    /// <summary>
    /// The channel the outgoing HTLC was offered on, once <see cref="ForwardCircuitStatus.Offered"/>.
    /// </summary>
    public ChannelId? OutgoingChannelId { get; private set; }

    /// <summary>
    /// The id of the outgoing HTLC, once <see cref="ForwardCircuitStatus.Offered"/>.
    /// </summary>
    public ulong? OutgoingHtlcId { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>
    /// Why the forward failed, once <see cref="ForwardCircuitStatus.Failed"/>: the BOLT 4 failure code when it was
    /// sent in the clear (the <c>fail_malformed</c> of the outgoing HTLC). An opaque failure onion's code is readable
    /// only by the node that created it, and a local refusal carries none.
    /// </summary>
    public ushort? FailureCode { get; private set; }

    /// <summary>
    /// The channel the failure is about, once <see cref="ForwardCircuitStatus.Failed"/>: the outgoing channel whose
    /// HTLC failed (or timed out on chain), or that refused the offer. Null when the offer never went out.
    /// </summary>
    public ChannelId? FailureSource { get; private set; }

    /// <summary>
    /// The fee we earn: incoming amount - outgoing amount.
    /// </summary>
    public LightningMoney Fee => IncomingAmount - OutgoingAmount;

    public ForwardCircuitModel(ChannelId incomingChannelId, ulong incomingHtlcId, LightningMoney incomingAmount,
                               uint incomingCltvExpiry, Hash paymentHash, Secret incomingSharedSecret,
                               ShortChannelId outgoingShortChannelId, LightningMoney outgoingAmount,
                               uint outgoingCltvExpiry, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(incomingAmount);
        ArgumentNullException.ThrowIfNull(outgoingAmount);
        if (outgoingAmount > incomingAmount)
            throw new ArgumentOutOfRangeException(nameof(outgoingAmount),
                                                  "A forward never sends more than it receives.");
        if (outgoingCltvExpiry > incomingCltvExpiry)
            throw new ArgumentOutOfRangeException(nameof(outgoingCltvExpiry),
                                                  "A forward's outgoing HTLC never expires after the incoming one.");

        IncomingChannelId = incomingChannelId;
        IncomingHtlcId = incomingHtlcId;
        IncomingAmount = incomingAmount;
        IncomingCltvExpiry = incomingCltvExpiry;
        PaymentHash = paymentHash;
        IncomingSharedSecret = incomingSharedSecret;
        OutgoingShortChannelId = outgoingShortChannelId;
        OutgoingAmount = outgoingAmount;
        OutgoingCltvExpiry = outgoingCltvExpiry;
        CreatedAt = createdAt;
        Status = ForwardCircuitStatus.Pending;
    }

    /// <summary>
    /// Rebuilds a stored circuit in any state (persistence only). Validates that the fields match the status.
    /// </summary>
    public static ForwardCircuitModel Restore(ChannelId incomingChannelId, ulong incomingHtlcId,
                                              LightningMoney incomingAmount, uint incomingCltvExpiry,
                                              Hash paymentHash, Secret incomingSharedSecret,
                                              ShortChannelId outgoingShortChannelId, LightningMoney outgoingAmount,
                                              uint outgoingCltvExpiry, DateTimeOffset createdAt,
                                              ForwardCircuitStatus status, ChannelId? outgoingChannelId,
                                              ulong? outgoingHtlcId, DateTimeOffset? resolvedAt,
                                              ushort? failureCode = null, ChannelId? failureSource = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown circuit status.");
        if (outgoingChannelId is null != outgoingHtlcId is null)
            throw new ArgumentException("The outgoing channel and HTLC id are set together.", nameof(outgoingHtlcId));
        if (status is ForwardCircuitStatus.Offered or ForwardCircuitStatus.Fulfilled && outgoingHtlcId is null)
            throw new ArgumentException($"A {status} circuit needs its outgoing HTLC.", nameof(outgoingHtlcId));
        if (status == ForwardCircuitStatus.Pending && outgoingHtlcId is not null)
            throw new ArgumentException("A pending circuit has no outgoing HTLC yet.", nameof(outgoingHtlcId));
        if (status is ForwardCircuitStatus.Fulfilled or ForwardCircuitStatus.Failed && resolvedAt is null)
            throw new ArgumentException("A resolved circuit needs its resolution time.", nameof(resolvedAt));
        if ((failureCode is not null || failureSource is not null) && status != ForwardCircuitStatus.Failed)
            throw new ArgumentException("Only a failed circuit carries a failure reason.", nameof(failureCode));

        return new ForwardCircuitModel(incomingChannelId, incomingHtlcId, incomingAmount, incomingCltvExpiry,
                                       paymentHash, incomingSharedSecret, outgoingShortChannelId, outgoingAmount,
                                       outgoingCltvExpiry, createdAt)
        {
            Status = status,
            OutgoingChannelId = outgoingChannelId,
            OutgoingHtlcId = outgoingHtlcId,
            ResolvedAt = resolvedAt,
            FailureCode = failureCode,
            FailureSource = failureSource
        };
    }

    /// <summary>
    /// Records the outgoing HTLC that was offered for this forward.
    /// </summary>
    public void AddOutgoingHtlc(ChannelId outgoingChannelId, ulong outgoingHtlcId)
    {
        if (Status != ForwardCircuitStatus.Pending)
            throw new InvalidOperationException($"Cannot offer the outgoing HTLC of a circuit that is {Status}.");

        OutgoingChannelId = outgoingChannelId;
        OutgoingHtlcId = outgoingHtlcId;
        Status = ForwardCircuitStatus.Offered;
    }

    /// <summary>
    /// The downstream peer revealed the preimage of the recorded outgoing HTLC. Requires
    /// <see cref="ForwardCircuitStatus.Offered"/>; when the circuit may still be
    /// <see cref="ForwardCircuitStatus.Pending"/>, use <see cref="MarkFulfilled(ChannelId, ulong, DateTimeOffset)"/>.
    /// </summary>
    public void MarkFulfilled(DateTimeOffset resolvedAt)
    {
        if (Status != ForwardCircuitStatus.Offered)
            throw new InvalidOperationException($"Cannot fulfill a circuit that is {Status}.");

        ResolvedAt = resolvedAt;
        Status = ForwardCircuitStatus.Fulfilled;
    }

    /// <summary>
    /// The downstream peer revealed the preimage of outgoing HTLC <paramref name="outgoingHtlcId"/> on
    /// <paramref name="outgoingChannelId"/>. Valid from <see cref="ForwardCircuitStatus.Pending"/> too (the outgoing
    /// HTLC is recorded here), because the circuit's <c>Offered</c> update is saved after the outgoing add; from
    /// <see cref="ForwardCircuitStatus.Offered"/> the HTLC must be the recorded one.
    /// </summary>
    public void MarkFulfilled(ChannelId outgoingChannelId, ulong outgoingHtlcId, DateTimeOffset resolvedAt)
    {
        RecordOutgoingForResolution(outgoingChannelId, outgoingHtlcId, "fulfill");
        ResolvedAt = resolvedAt;
        Status = ForwardCircuitStatus.Fulfilled;
    }

    /// <summary>
    /// The outgoing HTLC failed irrevocably, or the offer was refused.
    /// </summary>
    /// <remarks>
    /// Fail a <see cref="ForwardCircuitStatus.Pending"/> circuit this way only once no channel HTLC carries
    /// <c>HtlcOrigin.Forwarded(IncomingChannelId, IncomingHtlcId)</c> (the offer threw, or a startup replay checked
    /// every channel): a Pending circuit can have a live downstream HTLC. When the failed outgoing HTLC is known, use
    /// <see cref="MarkFailed(ChannelId, ulong, DateTimeOffset, ushort?)"/>.
    /// </remarks>
    /// <param name="resolvedAt">When the failure became irrevocable.</param>
    /// <param name="failureCode">The BOLT 4 failure code, when one is known (usually null: a local refusal has
    /// none).</param>
    /// <param name="failureSource">The channel the failure is about, when known (the channel whose offer was
    /// refused).</param>
    public void MarkFailed(DateTimeOffset resolvedAt, ushort? failureCode = null, ChannelId? failureSource = null)
    {
        if (Status is not (ForwardCircuitStatus.Pending or ForwardCircuitStatus.Offered))
            throw new InvalidOperationException($"Cannot fail a circuit that is {Status}.");

        ResolvedAt = resolvedAt;
        FailureCode = failureCode;
        FailureSource = failureSource;
        Status = ForwardCircuitStatus.Failed;
    }

    /// <summary>
    /// Outgoing HTLC <paramref name="outgoingHtlcId"/> on <paramref name="outgoingChannelId"/> failed irrevocably.
    /// Valid from <see cref="ForwardCircuitStatus.Pending"/> (records the HTLC) or
    /// <see cref="ForwardCircuitStatus.Offered"/> (it must be the recorded HTLC).
    /// </summary>
    /// <param name="failureCode">The BOLT 4 failure code of the removal, when it was sent in the clear (a
    /// <c>fail_malformed</c>); an opaque failure onion's code is readable by the origin node alone.</param>
    public void MarkFailed(ChannelId outgoingChannelId, ulong outgoingHtlcId, DateTimeOffset resolvedAt,
                           ushort? failureCode = null)
    {
        RecordOutgoingForResolution(outgoingChannelId, outgoingHtlcId, "fail");
        ResolvedAt = resolvedAt;
        FailureCode = failureCode;
        FailureSource = outgoingChannelId;
        Status = ForwardCircuitStatus.Failed;
    }

    private void RecordOutgoingForResolution(ChannelId outgoingChannelId, ulong outgoingHtlcId, string action)
    {
        switch (Status)
        {
            case ForwardCircuitStatus.Pending:
                OutgoingChannelId = outgoingChannelId;
                OutgoingHtlcId = outgoingHtlcId;
                return;
            case ForwardCircuitStatus.Offered:
                if (OutgoingChannelId != outgoingChannelId || OutgoingHtlcId != outgoingHtlcId)
                    throw new InvalidOperationException(
                        $"Cannot {action} the circuit through an outgoing HTLC it did not offer.");
                return;
            default:
                throw new InvalidOperationException($"Cannot {action} a circuit that is {Status}.");
        }
    }
}