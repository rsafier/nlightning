namespace NLightning.Domain.LiquidityAds.Models;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// A liquidity purchase we made or sold (BOLT PR #1153, plan <c>docs/agents/LIQUIDITY_ADS_PLAN.md</c> LA3,
/// table <c>LiquidityPurchases</c>): the request, the seller's signed <c>will_fund</c> and the fee, bound to the funding
/// attempt (an open, an RBF attempt or a splice) it rides on.
/// </summary>
/// <remarks>
/// <para><see cref="Id"/> is assigned by the database: 0 until the purchase is saved (the repository fills it in after
/// the save). One purchase per (<see cref="ChannelId"/>, <see cref="FundingTxId"/>).</para>
/// <para>Status moves only forward: Pending → Active → Closed, Pending → Replaced or Pending → Closed. The lease
/// (D-L4) runs <see cref="LeaseBlocks"/> blocks from the height the attempt confirmed at; nothing enforces it on the
/// wire, the seller's close guard and the buyer's record are all there is.</para>
/// </remarks>
public sealed class LiquidityPurchaseModel
{
    /// <summary>The length of the seller's <c>will_fund</c> signature (compact ECDSA).</summary>
    public const int SignatureLength = 64;

    /// <summary>The database id; 0 until the purchase is saved.</summary>
    public long Id { get; private set; }

    public ChannelId ChannelId { get; }

    /// <summary>The txid of the funding attempt the purchase belongs to: an open, an RBF attempt or a splice.</summary>
    public TxId FundingTxId { get; }

    public LiquidityPurchaseRole Role { get; }
    public LiquidityPurchaseKind Kind { get; }

    /// <summary><c>request_funding.requested_sats</c>: the amount the buyer asked the seller to contribute.</summary>
    public ulong RequestedSat { get; }

    /// <summary>What the seller contributed to the funding output in this attempt.</summary>
    public ulong ContributedSat { get; }

    /// <summary>The rate the purchase was made at (<c>will_fund.funding_rate</c>).</summary>
    public FundingRate Rate { get; }

    public LiquidityPaymentType PaymentType { get; }

    /// <summary>The mining fee the buyer refunds the seller (<c>funding_weight</c> at the attempt's feerate).</summary>
    public ulong MiningFeeSat { get; }

    /// <summary>The seller's service fee.</summary>
    public ulong ServiceFeeSat { get; }

    /// <summary>The seller's signature of the rate and the funding script (<c>will_fund.signature</c>).</summary>
    public CompactSignature Signature { get; }

    /// <summary>The new funding output's script the signature covers (<c>will_fund.funding_script</c>).</summary>
    public byte[] FundingScript { get; }

    /// <summary>The other side: the seller when we bought, the buyer when we sold.</summary>
    public CompactPubKey PeerNodeId { get; }

    /// <summary>How many blocks the seller should keep the channel open after the funding confirmed.</summary>
    public uint LeaseBlocks { get; }

    /// <summary>
    /// The fee limit the buyer gave with the request (<c>--max-liquidity-fee</c>), null when it gave none (the node's
    /// <c>Node:LiquidityAds:MaxFeeSat</c> applied) and for a sale. An RBF that repeats the purchase without a new
    /// request keeps it (NL-871).
    /// </summary>
    public ulong? MaxFeeSat { get; }

    public DateTimeOffset CreatedAt { get; }

    public LiquidityPurchaseStatus Status { get; private set; }

    /// <summary>The height the funding attempt confirmed at; null until then.</summary>
    public uint? LeaseStartHeight { get; private set; }

    /// <summary>The height the channel closed at, once <see cref="LiquidityPurchaseStatus.Closed"/>.</summary>
    public uint? ClosedAtHeight { get; private set; }

    /// <summary>
    /// Whether the channel closed before the lease ended (or before the funding confirmed), once
    /// <see cref="LiquidityPurchaseStatus.Closed"/>.
    /// </summary>
    public bool ClosedEarly { get; private set; }

    /// <summary>The fee the buyer pays the seller.</summary>
    public LiquidityFees Fees => new(MiningFeeSat, ServiceFeeSat);

    /// <summary>The whole fee in millisatoshi.</summary>
    public ulong TotalFeeMsat => Fees.TotalMsat;

    /// <summary>The first height the lease no longer covers; null until the funding confirmed.</summary>
    public uint? LeaseEndHeight => LeaseStartHeight is { } start ? checked(start + LeaseBlocks) : null;

    public LiquidityPurchaseModel(ChannelId channelId, TxId fundingTxId, LiquidityPurchaseRole role,
                                  LiquidityPurchaseKind kind, ulong requestedSat, ulong contributedSat,
                                  FundingRate rate, LiquidityPaymentType paymentType, ulong miningFeeSat,
                                  ulong serviceFeeSat, CompactSignature signature, byte[] fundingScript,
                                  CompactPubKey peerNodeId, uint leaseBlocks, DateTimeOffset createdAt,
                                  ulong? maxFeeSat = null)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown purchase role.");
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown purchase kind.");
        if (!Enum.IsDefined(paymentType))
            throw new ArgumentOutOfRangeException(nameof(paymentType), paymentType, "Unknown payment type.");
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Value.Length != SignatureLength)
            throw new ArgumentException($"The will_fund signature is {SignatureLength} bytes.", nameof(signature));
        ArgumentNullException.ThrowIfNull(fundingScript);
        if (fundingScript.Length == 0)
            throw new ArgumentException("The funding script is empty.", nameof(fundingScript));
        if (fundingScript.Length > ushort.MaxValue)
            throw new ArgumentException("The funding script is longer than 65535 bytes.", nameof(fundingScript));
        if ((byte[]?)peerNodeId is null)
            throw new ArgumentException("The peer node id is missing.", nameof(peerNodeId));
        if ((byte[]?)channelId is null || (byte[]?)fundingTxId is null)
            throw new ArgumentException("The channel id and the funding txid are required.", nameof(fundingTxId));

        // The amounts are stored as signed 64-bit columns, and the fee must be expressible in msat
        if (requestedSat > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestedSat), requestedSat, "The amount is too large.");
        if (contributedSat > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(contributedSat), contributedSat, "The amount is too large.");
        if (miningFeeSat > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(miningFeeSat), miningFeeSat, "The fee is too large.");
        if (serviceFeeSat > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(serviceFeeSat), serviceFeeSat, "The fee is too large.");
        if (maxFeeSat > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxFeeSat), maxFeeSat, "The fee limit is too large.");
        if (maxFeeSat is not null && role != LiquidityPurchaseRole.Buyer)
            throw new ArgumentException("Only a purchase we made carries our fee limit.", nameof(maxFeeSat));
        _ = new LiquidityFees(miningFeeSat, serviceFeeSat).TotalMsat;

        ChannelId = channelId;
        FundingTxId = fundingTxId;
        Role = role;
        Kind = kind;
        RequestedSat = requestedSat;
        ContributedSat = contributedSat;
        Rate = rate;
        PaymentType = paymentType;
        MiningFeeSat = miningFeeSat;
        ServiceFeeSat = serviceFeeSat;
        Signature = new CompactSignature(signature.Value.ToArray());
        FundingScript = fundingScript.ToArray();
        PeerNodeId = peerNodeId;
        LeaseBlocks = leaseBlocks;
        CreatedAt = createdAt;
        MaxFeeSat = maxFeeSat;
        Status = LiquidityPurchaseStatus.Pending;
    }

    /// <summary>
    /// Rebuilds a stored purchase in any state (persistence only). Validates that the fields match the status.
    /// </summary>
    public static LiquidityPurchaseModel Restore(long id, ChannelId channelId, TxId fundingTxId,
                                                 LiquidityPurchaseRole role, LiquidityPurchaseKind kind,
                                                 ulong requestedSat, ulong contributedSat, FundingRate rate,
                                                 LiquidityPaymentType paymentType, ulong miningFeeSat,
                                                 ulong serviceFeeSat, CompactSignature signature,
                                                 byte[] fundingScript, CompactPubKey peerNodeId, uint leaseBlocks,
                                                 DateTimeOffset createdAt, LiquidityPurchaseStatus status,
                                                 uint? leaseStartHeight, uint? closedAtHeight, bool closedEarly,
                                                 ulong? maxFeeSat = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown purchase status.");
        if (status == LiquidityPurchaseStatus.Active && leaseStartHeight is null)
            throw new ArgumentException("An active purchase needs its lease start.", nameof(leaseStartHeight));
        if (status is LiquidityPurchaseStatus.Pending or LiquidityPurchaseStatus.Replaced && leaseStartHeight is not null)
            throw new ArgumentException($"A {status} purchase has no lease start.", nameof(leaseStartHeight));
        if (status == LiquidityPurchaseStatus.Closed && closedAtHeight is null)
            throw new ArgumentException("A closed purchase needs its close height.", nameof(closedAtHeight));
        if (status != LiquidityPurchaseStatus.Closed && (closedAtHeight is not null || closedEarly))
            throw new ArgumentException("Only a closed purchase carries a close.", nameof(closedAtHeight));

        return new LiquidityPurchaseModel(channelId, fundingTxId, role, kind, requestedSat, contributedSat, rate,
                                          paymentType, miningFeeSat, serviceFeeSat, signature, fundingScript,
                                          peerNodeId, leaseBlocks, createdAt, maxFeeSat)
        {
            Id = id,
            Status = status,
            LeaseStartHeight = leaseStartHeight,
            ClosedAtHeight = closedAtHeight,
            ClosedEarly = closedEarly
        };
    }

    /// <summary>
    /// Records the database id of a purchase that was just saved (persistence only).
    /// </summary>
    /// <exception cref="InvalidOperationException">The purchase already has another id.</exception>
    public void AssignId(long id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        if (Id != 0 && Id != id)
            throw new InvalidOperationException($"The purchase already has id {Id}.");

        Id = id;
    }

    /// <summary>
    /// The funding attempt confirmed at <paramref name="height"/>: the lease starts there. Calling it again on an active
    /// purchase moves the start (the attempt confirmed again at another height after a reorg).
    /// </summary>
    /// <exception cref="InvalidOperationException">The purchase was replaced or closed.</exception>
    public void MarkActive(uint height)
    {
        if (Status is not (LiquidityPurchaseStatus.Pending or LiquidityPurchaseStatus.Active))
            throw new InvalidOperationException($"A {Status} purchase cannot become active.");

        Status = LiquidityPurchaseStatus.Active;
        LeaseStartHeight = height;
    }

    /// <summary>
    /// Another attempt of the same funding confirmed (or replaced this one), or the attempt was abandoned before it
    /// could confirm (a splice aborted before our <c>tx_signatures</c>, NL-870). Idempotent.
    /// </summary>
    /// <exception cref="InvalidOperationException">The purchase is active or closed.</exception>
    public void MarkReplaced()
    {
        if (Status == LiquidityPurchaseStatus.Replaced)
            return;
        if (Status != LiquidityPurchaseStatus.Pending)
            throw new InvalidOperationException($"A {Status} purchase cannot be replaced.");

        Status = LiquidityPurchaseStatus.Replaced;
    }

    /// <summary>
    /// The channel closed at <paramref name="height"/>. <see cref="ClosedEarly"/> is set when the lease had not ended
    /// (or had not started). Idempotent: a second call keeps the first close.
    /// </summary>
    /// <exception cref="InvalidOperationException">The purchase was replaced.</exception>
    public void MarkClosed(uint height)
    {
        if (Status == LiquidityPurchaseStatus.Closed)
            return;
        if (Status == LiquidityPurchaseStatus.Replaced)
            throw new InvalidOperationException("A replaced purchase cannot be closed.");

        ClosedEarly = LeaseEndHeight is not { } end || height < end;
        ClosedAtHeight = height;
        Status = LiquidityPurchaseStatus.Closed;
    }

    /// <summary>
    /// Whether the lease still binds the seller at <paramref name="height"/>: the purchase is pending (the lease has
    /// not started) or active with <paramref name="height"/> below <see cref="LeaseEndHeight"/>.
    /// </summary>
    public bool IsLeaseInForce(uint height) =>
        Status == LiquidityPurchaseStatus.Pending
     || (Status == LiquidityPurchaseStatus.Active && LeaseEndHeight is { } end && height < end);
}