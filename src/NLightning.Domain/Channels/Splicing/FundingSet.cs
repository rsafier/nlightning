namespace NLightning.Domain.Channels.Splicing;

using Bitcoin.ValueObjects;
using Commitments;
using Enums;
using Protocol.InteractiveTx;

/// <summary>
/// The fundings of a channel (splicing plan §3.3, D6, D7): the <see cref="Current"/> one and the ordered
/// <see cref="Pending"/> splices (at most one splice and its RBF attempts, SP-S-01). Immutable: every change returns a
/// new set.
/// </summary>
/// <remarks>
/// The transitions (<see cref="AddPending"/>, <see cref="AddRbfSibling"/> (wave SPR), <see cref="Lock"/>,
/// <see cref="Discard"/>) enforce the rules: a new splice
/// only when nothing is pending, RBF siblings of the pending splice, lock folds the deltas and discards siblings and
/// ancestors. The commitment engine (<c>ChannelCommitments</c>) applies them to its <c>PendingFundings</c> together with
/// the per-funding signatures.
/// <c>ChannelModel.FundingOutput</c> stays a view of <see cref="Current"/> so the single-funding code keeps working.
/// </remarks>
/// <param name="Current">The current funding (<see cref="ChannelFundingStatus.Current"/>).</param>
/// <param name="Pending">The pending splice attempts, oldest first (<see cref="ChannelFundingStatus.Pending"/>).</param>
public sealed record FundingSet(ChannelFunding Current, IReadOnlyList<ChannelFunding> Pending)
{
    /// <summary>A channel that was never spliced.</summary>
    public static FundingSet Single(ChannelFunding current) => new(current, []);

    /// <summary>Whether a splice is negotiated but not locked (SP-S-01: no new <c>splice_init</c> then).</summary>
    public bool HasPending => Pending.Count > 0;

    /// <summary>Every funding commitments are signed for: <see cref="Current"/> first, then <see cref="Pending"/>
    /// (the order of the batched <c>commitment_signed</c>s, SP-OP-03).</summary>
    public IEnumerable<ChannelFunding> Active => Pending.Prepend(Current);

    /// <summary>The number of active fundings (the <c>start_batch</c> size when above 1).</summary>
    public int ActiveCount => Pending.Count + 1;

    /// <summary>The active funding with <paramref name="fundingTxId"/>, or null.</summary>
    public ChannelFunding? Find(TxId fundingTxId) => Active.FirstOrDefault(f => f.FundingTxId == fundingTxId);

    /// <summary>
    /// Adds a negotiated splice (or RBF attempt) as pending.
    /// </summary>
    /// <remarks>
    /// SP-S-01: a <see cref="ChannelFundingKind.Splice"/> only when nothing is pending; a
    /// <see cref="ChannelFundingKind.SpliceRbf"/> only as a sibling of a pending attempt (<see cref="ChannelFunding.RbfOf"/>
    /// names one). The funding must be <see cref="ChannelFundingStatus.Pending"/>, differ from every active funding, and
    /// conserve value: <c>(capacity - current capacity) x 1000 == local delta + remote delta</c> (I6 per funding).
    /// </remarks>
    /// <exception cref="ArgumentException">One of those rules is broken.</exception>
    public FundingSet AddPending(ChannelFunding funding)
    {
        ArgumentNullException.ThrowIfNull(funding);
        if (funding.Status != ChannelFundingStatus.Pending)
            throw new ArgumentException($"Funding {funding.FundingTxId} is {funding.Status}, not Pending",
                                        nameof(funding));
        if (Find(funding.FundingTxId) is not null)
            throw new ArgumentException($"Funding {funding.FundingTxId} is already active", nameof(funding));

        switch (funding.Kind)
        {
            case ChannelFundingKind.Splice when HasPending:
                throw new ArgumentException("SP-S-01: a splice is already pending; only an RBF attempt may be added",
                                            nameof(funding));
            case ChannelFundingKind.Splice:
                break;
            case ChannelFundingKind.SpliceRbf when funding.RbfOf is { } parent
                                                && Pending.Any(p => p.FundingTxId == parent):
                break;
            case ChannelFundingKind.SpliceRbf:
                throw new ArgumentException($"RBF attempt {funding.FundingTxId} does not replace a pending splice",
                                            nameof(funding));
            default:
                throw new ArgumentException($"A {funding.Kind} funding cannot be pending", nameof(funding));
        }

        var capacityDeltaMsat = checked(((long)funding.CapacitySatoshis - (long)Current.CapacitySatoshis) * 1_000);
        if (checked(funding.LocalBalanceDeltaMsat + funding.RemoteBalanceDeltaMsat) != capacityDeltaMsat)
            throw new ArgumentException(
                $"Funding {funding.FundingTxId} deltas {funding.LocalBalanceDeltaMsat} + {funding.RemoteBalanceDeltaMsat} msat do not match the capacity change {capacityDeltaMsat} msat",
                nameof(funding));

        return this with { Pending = [.. Pending, funding] };
    }

    /// <summary>
    /// The latest pending attempt (the one an RBF replaces and whose feerate the next <c>tx_init_rbf</c> must beat,
    /// IT-RBF-01), or null when nothing is pending.
    /// </summary>
    public ChannelFunding? LatestAttempt => Pending.Count == 0 ? null : Pending[^1];

    /// <summary>
    /// Adds an RBF attempt of the pending splice (wave SPR, SPR-T2): a <see cref="ChannelFundingKind.SpliceRbf"/>
    /// funding whose <see cref="ChannelFunding.RbfOf"/> is <see cref="LatestAttempt"/>, whose
    /// <see cref="ChannelFunding.FeeratePerKw"/> is at least <c>InteractiveTxRbfRules.GetMinimumNextFeerate</c> of it,
    /// and that keeps <see cref="ActiveCount"/> within <c>ChannelCommitments.MaxActiveFundings</c> (the
    /// <c>start_batch</c> limit of 20, SP-OP-04); otherwise the <see cref="AddPending"/> rules. Every attempt spends the
    /// current funding output, so the attempts double-spend each other (BOLT 2 splicing rationale).
    /// </summary>
    /// <exception cref="ArgumentException">One of those rules is broken, or nothing is pending.</exception>
    public FundingSet AddRbfSibling(ChannelFunding attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (LatestAttempt is not { } latest)
            throw new ArgumentException($"RBF attempt {attempt.FundingTxId}: no splice is pending", nameof(attempt));
        if (attempt.Kind != ChannelFundingKind.SpliceRbf)
            throw new ArgumentException($"Funding {attempt.FundingTxId} is a {attempt.Kind}, not an RBF attempt",
                                        nameof(attempt));
        if (attempt.RbfOf != latest.FundingTxId)
            throw new ArgumentException(
                $"RBF attempt {attempt.FundingTxId} replaces {attempt.RbfOf}, not the latest attempt {latest.FundingTxId}",
                nameof(attempt));
        if (latest.FeeratePerKw is { } previous
         && (attempt.FeeratePerKw ?? 0) < InteractiveTxRbfRules.GetMinimumNextFeerate(previous))
            throw new ArgumentException(
                $"[IT-RBF-01] RBF attempt {attempt.FundingTxId} at {attempt.FeeratePerKw} sat/kw is below "
              + $"{InteractiveTxRbfRules.GetMinimumNextFeerate(previous)} sat/kw", nameof(attempt));
        if (ActiveCount >= ChannelCommitments.MaxActiveFundings)
            throw new ArgumentException(
                $"[SP-OP-04] a channel may have at most {ChannelCommitments.MaxActiveFundings} active fundings",
                nameof(attempt));

        return AddPending(attempt);
    }

    /// <summary>
    /// The other attempts of the same splice as the pending <paramref name="fundingTxId"/> (its RBF siblings and
    /// ancestors, SP-LK-03): what <see cref="Lock"/> discards when <paramref name="fundingTxId"/> locks, and the
    /// candidates a peer's <c>splice_locked</c> for "different RBF candidates" names (D11). Empty when it is the only
    /// attempt.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="fundingTxId"/> is not pending.</exception>
    public IReadOnlyList<ChannelFunding> Siblings(TxId fundingTxId)
    {
        // SP-S-01: at most one splice is pending, so every other pending funding is an attempt of the same splice
        if (Pending.All(p => p.FundingTxId != fundingTxId))
            throw new ArgumentException($"Funding {fundingTxId} is not pending", nameof(fundingTxId));

        return Pending.Where(p => p.FundingTxId != fundingTxId).ToList();
    }

    /// <summary>
    /// Locks the pending funding <paramref name="fundingTxId"/> (<c>splice_locked</c> sent and received for it): it
    /// becomes <see cref="Current"/> with its deltas folded (0), the former current becomes
    /// <see cref="ChannelFundingStatus.Replaced"/>, every other pending attempt (its RBF siblings and ancestors, SP-LK-03)
    /// <see cref="ChannelFundingStatus.Discarded"/>.
    /// </summary>
    /// <returns>The new set and the fundings that left it (replaced first, then the discarded ones), for the lock's
    /// save.</returns>
    /// <exception cref="ArgumentException"><paramref name="fundingTxId"/> is not pending (SP-LK-02).</exception>
    public (FundingSet Next, IReadOnlyList<ChannelFunding> Retired) Lock(TxId fundingTxId)
    {
        var locked = Pending.FirstOrDefault(p => p.FundingTxId == fundingTxId)
                  ?? throw new ArgumentException($"Funding {fundingTxId} is not pending", nameof(fundingTxId));

        var current = locked with
        {
            Status = ChannelFundingStatus.Current,
            LocalBalanceDeltaMsat = 0,
            RemoteBalanceDeltaMsat = 0
        };
        var retired = new List<ChannelFunding> { Current with { Status = ChannelFundingStatus.Replaced } };
        retired.AddRange(Siblings(fundingTxId).Select(p => p with { Status = ChannelFundingStatus.Discarded }));
        return (Single(current), retired);
    }

    /// <summary>
    /// Discards every pending funding (a commitment of the current funding confirmed, or the splice was abandoned before
    /// it could confirm; splicing plan §3.6).
    /// </summary>
    /// <returns>The set with only <see cref="Current"/>, and the discarded fundings.</returns>
    public (FundingSet Next, IReadOnlyList<ChannelFunding> Discarded) Discard() =>
        (Single(Current), Pending.Select(p => p with { Status = ChannelFundingStatus.Discarded }).ToList());
}