namespace NLightning.Domain.Payments.Trampoline;

using Crypto.ValueObjects;
using Money;

/// <summary>
/// A trampoline payment we relay (BOLTs PR 836, NL-875): the incoming MPP set of the payment hash
/// <see cref="PaymentHash"/> (its parts are <see cref="TrampolineRelayPartModel"/>s) and the one outgoing payment that
/// continues it, whose HTLCs carry <c>HtlcOrigin.Trampoline(PaymentHash)</c>. It is not a forward circuit: N incoming
/// HTLCs map to M outgoing ones, and the relay answers for every incoming part at once.
/// </summary>
/// <remarks>
/// <para>Keyed by the payment hash: at most one relay per hash. The routing fields come from the peeled trampoline
/// onion and never change; the status moves only forward (<see cref="TrampolineRelayStatus"/>), and the mutators throw
/// <see cref="InvalidOperationException"/> otherwise.</para>
/// <para>Safety rules (mirroring a forward): no incoming part is failed upstream while any outgoing HTLC of the relay
/// is unresolved; once any outgoing HTLC is fulfilled, every incoming part is fulfilled (its record carries the
/// preimage first, in one save, NL-322) and never failed.</para>
/// </remarks>
public sealed class TrampolineRelayModel
{
    /// <summary>The payment hash (the key; the same on every incoming part and on the outgoing payment).</summary>
    public Hash PaymentHash { get; }

    public TrampolineRelayStatus Status { get; private set; }

    /// <summary>The next trampoline node or the recipient (<c>outgoing_node_id</c>); null when we pay blinded paths
    /// (<see cref="RecipientBlindedPaths"/>).</summary>
    public CompactPubKey? NextNodeId { get; }

    /// <summary>The <c>encrypted_recipient_data</c> of a blinded trampoline hop, for the next trampoline node.</summary>
    public byte[]? NextEncryptedRecipientData { get; }

    /// <summary>The <c>path_key</c> (blinding point) that goes with <see cref="NextEncryptedRecipientData"/>.</summary>
    public byte[]? NextPathKey { get; }

    /// <summary>The recipient's features (<c>invoice_features</c>), when the trampoline payload gave them.</summary>
    public byte[]? RecipientFeatures { get; }

    /// <summary>The recipient's blinded paths as raw TLV bytes (a payment to a BOLT 12 invoice), or null.</summary>
    public byte[]? RecipientBlindedPaths { get; }

    /// <summary>The peeled trampoline onion to forward to <see cref="NextNodeId"/>; null when we pay the recipient (its
    /// blinded paths, or the final payload) ourselves.</summary>
    public byte[]? NextTrampolinePacket { get; }

    /// <summary>What the next node must receive (<c>amt_to_forward</c> of the trampoline payload).</summary>
    public LightningMoney AmountOut { get; }

    /// <summary>The next node's <c>outgoing_cltv_value</c> of the trampoline payload.</summary>
    public uint CltvExpiryOut { get; }

    /// <summary>
    /// The <c>cltv_expiry_delta</c> a blinded hop's price check keeps (the largest of its parts': a policy may change
    /// between them), so the completion after a restart evaluates the same delta the parts paid (NL-923); null for an
    /// unblinded relay or a relay stored before the migration (whose completion then uses the safe upper bound).
    /// </summary>
    public ushort? BlindedKeptCltvExpiryDelta { get; private set; }

    /// <summary>The outer onion's <c>total_msat</c>: the sum the incoming set must reach.</summary>
    public LightningMoney IncomingTotal { get; }

    /// <summary>What the relay earned once <see cref="TrampolineRelayStatus.Fulfilled"/>: the incoming sum minus the
    /// outgoing payment (amount and routing fees paid); null before.</summary>
    public LightningMoney? FeeEarned { get; private set; }

    /// <summary>The <c>payment_secret</c> of our outgoing payment (to the next trampoline node or the recipient).</summary>
    public byte[]? OutgoingPaymentSecret { get; private set; }

    /// <summary>The preimage the outgoing payment learnt, once <see cref="TrampolineRelayStatus.Fulfilled"/>.</summary>
    public Secret? Preimage { get; private set; }

    /// <summary>The BOLT 4 failure code returned upstream, once <see cref="TrampolineRelayStatus.Failed"/>.</summary>
    public ushort? FailureCode { get; private set; }

    /// <summary>Why the relay failed (local text, never sent), once <see cref="TrampolineRelayStatus.Failed"/>.</summary>
    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>When the relay was fulfilled or failed.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>True once <see cref="TrampolineRelayStatus.Fulfilled"/> or <see cref="TrampolineRelayStatus.Failed"/>.
    /// </summary>
    public bool IsCompleted => Status is TrampolineRelayStatus.Fulfilled or TrampolineRelayStatus.Failed;

    /// <param name="paymentHash">The payment hash.</param>
    /// <param name="nextNodeId">The next trampoline node or the recipient; null for a payment to blinded paths.</param>
    /// <param name="amountOut">What the next node must receive.</param>
    /// <param name="cltvExpiryOut">The next node's <c>outgoing_cltv_value</c>.</param>
    /// <param name="incomingTotal">The outer onion's <c>total_msat</c>.</param>
    /// <param name="createdAt">When the first incoming part arrived.</param>
    /// <param name="nextTrampolinePacket">The peeled trampoline onion to forward, or null.</param>
    /// <param name="nextEncryptedRecipientData">A blinded trampoline hop's <c>encrypted_recipient_data</c>.</param>
    /// <param name="nextPathKey">Its <c>path_key</c>.</param>
    /// <param name="recipientFeatures">The recipient's features.</param>
    /// <param name="recipientBlindedPaths">The recipient's blinded paths (raw TLV bytes).</param>
    /// <param name="blindedKeptCltvExpiryDelta">The delta a blinded hop's first part's price check keeps.</param>
    /// <exception cref="ArgumentException">Neither a next node nor blinded paths, or a path key without its
    /// encrypted recipient data (or the reverse).</exception>
    public TrampolineRelayModel(Hash paymentHash, CompactPubKey? nextNodeId, LightningMoney amountOut,
                                uint cltvExpiryOut, LightningMoney incomingTotal, DateTimeOffset createdAt,
                                byte[]? nextTrampolinePacket = null, byte[]? nextEncryptedRecipientData = null,
                                byte[]? nextPathKey = null, byte[]? recipientFeatures = null,
                                byte[]? recipientBlindedPaths = null,
                                ushort? blindedKeptCltvExpiryDelta = null)
    {
        ArgumentNullException.ThrowIfNull(amountOut);
        ArgumentNullException.ThrowIfNull(incomingTotal);
        if (amountOut.IsZero)
            throw new ArgumentOutOfRangeException(nameof(amountOut), "A trampoline relay forwards a positive amount.");
        if (nextNodeId is null && recipientBlindedPaths is null)
            throw new ArgumentException("A trampoline relay needs a next node or the recipient's blinded paths.",
                                        nameof(nextNodeId));
        if (nextEncryptedRecipientData is null != nextPathKey is null)
            throw new ArgumentException("The encrypted recipient data and its path key are set together.",
                                        nameof(nextPathKey));

        PaymentHash = paymentHash;
        NextNodeId = nextNodeId;
        AmountOut = amountOut;
        CltvExpiryOut = cltvExpiryOut;
        IncomingTotal = incomingTotal;
        CreatedAt = createdAt;
        NextTrampolinePacket = Copy(nextTrampolinePacket);
        NextEncryptedRecipientData = Copy(nextEncryptedRecipientData);
        NextPathKey = Copy(nextPathKey);
        RecipientFeatures = Copy(recipientFeatures);
        RecipientBlindedPaths = Copy(recipientBlindedPaths);
        BlindedKeptCltvExpiryDelta = blindedKeptCltvExpiryDelta;
        Status = TrampolineRelayStatus.Collecting;
    }

    /// <summary>
    /// Rebuilds a stored relay in any state (persistence only). Validates that the fields match the status.
    /// </summary>
    public static TrampolineRelayModel Restore(Hash paymentHash, TrampolineRelayStatus status, CompactPubKey? nextNodeId,
                                               byte[]? nextEncryptedRecipientData, byte[]? nextPathKey,
                                               byte[]? recipientFeatures, byte[]? recipientBlindedPaths,
                                               byte[]? nextTrampolinePacket, LightningMoney amountOut,
                                               uint cltvExpiryOut, LightningMoney incomingTotal,
                                               LightningMoney? feeEarned, byte[]? outgoingPaymentSecret,
                                               Secret? preimage, ushort? failureCode, string? failureReason,
                                               DateTimeOffset createdAt, DateTimeOffset? completedAt,
                                               ushort? blindedKeptCltvExpiryDelta = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown trampoline relay status.");
        if (status == TrampolineRelayStatus.Fulfilled && (preimage is null || feeEarned is null))
            throw new ArgumentException("A fulfilled relay needs its preimage and fee.", nameof(preimage));
        if (status != TrampolineRelayStatus.Fulfilled && (preimage is not null || feeEarned is not null))
            throw new ArgumentException("Only a fulfilled relay has a preimage and a fee.", nameof(preimage));
        if (status != TrampolineRelayStatus.Failed && (failureCode is not null || failureReason is not null))
            throw new ArgumentException("Only a failed relay carries a failure.", nameof(failureCode));
        if ((status is TrampolineRelayStatus.Fulfilled or TrampolineRelayStatus.Failed) != completedAt is not null)
            throw new ArgumentException("A relay has a completion time exactly when it is completed.",
                                        nameof(completedAt));

        return new TrampolineRelayModel(paymentHash, nextNodeId, amountOut, cltvExpiryOut, incomingTotal, createdAt,
                                        nextTrampolinePacket, nextEncryptedRecipientData, nextPathKey,
                                        recipientFeatures, recipientBlindedPaths)
        {
            Status = status,
            BlindedKeptCltvExpiryDelta = blindedKeptCltvExpiryDelta,
            FeeEarned = feeEarned,
            OutgoingPaymentSecret = Copy(outgoingPaymentSecret),
            Preimage = preimage,
            FailureCode = failureCode,
            FailureReason = failureReason,
            CompletedAt = completedAt
        };
    }

    /// <summary>
    /// Raises <see cref="BlindedKeptCltvExpiryDelta"/> to a later part's kept delta (a policy may change between
    /// parts) and returns whether it grew (the caller persists the row then).
    /// </summary>
    public bool KeepBlindedDelta(ushort keptCltvExpiryDelta)
    {
        if (BlindedKeptCltvExpiryDelta >= keptCltvExpiryDelta)
            return false;

        BlindedKeptCltvExpiryDelta = keptCltvExpiryDelta;
        return true;
    }

    /// <summary>
    /// The incoming set is complete and the outgoing payment starts (Collecting → Sending). Persist this before the
    /// first outgoing HTLC is offered.
    /// </summary>
    /// <param name="outgoingPaymentSecret">The <c>payment_secret</c> of the outgoing payment, when there is one.</param>
    public void MarkSending(byte[]? outgoingPaymentSecret = null)
    {
        if (Status != TrampolineRelayStatus.Collecting)
            throw new InvalidOperationException($"Cannot start sending a trampoline relay that is {Status}.");

        if (outgoingPaymentSecret is not null)
            OutgoingPaymentSecret = Copy(outgoingPaymentSecret);
        Status = TrampolineRelayStatus.Sending;
    }

    /// <summary>
    /// The outgoing payment learnt the preimage (Sending → Fulfilled): every incoming part is owed its fulfill.
    /// </summary>
    /// <param name="preimage">The preimage.</param>
    /// <param name="feeEarned">The incoming sum minus what the outgoing payment cost (amount and routing fees).</param>
    /// <param name="completedAt">When the preimage was learnt.</param>
    public void MarkFulfilled(Secret preimage, LightningMoney feeEarned, DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(feeEarned);
        if (Status != TrampolineRelayStatus.Sending)
            throw new InvalidOperationException($"Cannot fulfill a trampoline relay that is {Status}.");

        Preimage = preimage;
        FeeEarned = feeEarned;
        CompletedAt = completedAt;
        Status = TrampolineRelayStatus.Fulfilled;
    }

    /// <summary>
    /// The relay failed for good (Collecting or Sending → Failed). From Sending, call it only once no outgoing HTLC of
    /// the relay is unresolved (a <c>Pending</c> outgoing HTLC may still be fulfilled downstream).
    /// </summary>
    /// <param name="failureCode">The BOLT 4 failure code returned upstream, when there is one.</param>
    /// <param name="failureReason">Why (local text).</param>
    /// <param name="completedAt">When the relay failed.</param>
    public void MarkFailed(ushort? failureCode, string? failureReason, DateTimeOffset completedAt)
    {
        if (Status is not (TrampolineRelayStatus.Collecting or TrampolineRelayStatus.Sending))
            throw new InvalidOperationException($"Cannot fail a trampoline relay that is {Status}.");

        FailureCode = failureCode;
        FailureReason = failureReason;
        CompletedAt = completedAt;
        Status = TrampolineRelayStatus.Failed;
    }

    private static byte[]? Copy(byte[]? bytes) => bytes?.ToArray();
}