using System.Collections.Immutable;

namespace NLightning.Domain.Channels.Commitments;

using Bitcoin.Transactions.Enums;
using Bitcoin.Transactions.Factories;
using Bitcoin.ValueObjects;
using Crypto.Constants;
using Crypto.Hashes;
using Crypto.ValueObjects;
using Enums;
using Events;
using Exceptions;
using Interfaces;
using Splicing;
using Splicing.Enums;
using Validators;
using ValueObjects;

/// <summary>
/// The pure BOLT 2 commitment state machine of one channel (plan §3.3, decisions D1, D3, D7, D9): an immutable
/// snapshot of both commitments, every HTLC with its <see cref="HtlcState"/>, the fee updates and the counters.
/// </summary>
/// <remarks>
/// <para>
/// Every operation validates its input, returns a new snapshot in a <see cref="CommitmentsResult"/>, and leaves this
/// one untouched. Local operations that break a rule throw <see cref="CommitmentRefusedException"/>; peer input that
/// breaks a rule throws <see cref="CommitmentViolationException"/>. No I/O, persistence or cryptography happens here:
/// signatures go through <see cref="ICommitmentSigner"/>/<see cref="ICommitmentVerifier"/>, revocation secrets through
/// <see cref="IRevocationVerifier"/>, and the <c>revoke_and_ack</c> secret is left to the caller
/// (<see cref="OutboundRevokeAndAck"/>).
/// </para>
/// <para>
/// Each result also carries the domain events of the operation (N4-T4, <see cref="ChannelDomainEvents"/>):
/// <see cref="IncomingHtlcLockedIn"/> and the irrevocable <see cref="OutgoingHtlcFailed"/>/<see cref="OutgoingHtlcSettled"/>
/// come from <see cref="ReceiveRevoke"/>, <see cref="OutgoingHtlcFulfilled"/> from <see cref="ReceiveFulfill"/>.
/// </para>
/// <para>
/// Amounts are <c>ulong</c> msat with checked arithmetic (plan §3.2); balances are "settled" balances: HTLCs are kept
/// separately and folded in only when final.
/// </para>
/// </remarks>
public sealed record ChannelCommitments
{
    /// <summary>BOLT 2: <c>cltv_expiry</c> values at or above this are timestamps, which the protocol does not use.</summary>
    public const uint MaxCltvExpiry = 500_000_000;

    /// <summary>The <c>BADONION</c> bit of a BOLT 4 failure code.</summary>
    public const ushort BadOnionFlag = 0x8000;

    public ChannelId ChannelId { get; private init; }
    public CommitmentParams Params { get; private init; }

    /// <summary>
    /// Our settled balance: every final HTLC folded in; open HTLCs are still counted in their offerer's balance (they
    /// are debited per commitment by <see cref="BuildSpec"/>). <c>LocalBalanceMsat + RemoteBalanceMsat</c> is always
    /// the funding amount.
    /// </summary>
    public ulong LocalBalanceMsat { get; private init; }

    /// <summary>The peer's settled balance (see <see cref="LocalBalanceMsat"/>).</summary>
    public ulong RemoteBalanceMsat { get; private init; }

    /// <summary>Every HTLC not yet final, by (direction, id).</summary>
    public ImmutableSortedDictionary<HtlcKey, HtlcRecord> Htlcs { get; private init; }

    /// <summary>Fee updates by sequence; the first one is always in both commitments.</summary>
    public ImmutableList<FeeUpdate> FeeUpdates { get; private init; }

    /// <summary>The id of the next HTLC we offer (starts at 0, never reset: B2-ADD-S10).</summary>
    public ulong LocalNextHtlcId { get; private init; }

    /// <summary>The id we expect on the next <c>update_add_htlc</c> from the peer (B2-ADD-R07).</summary>
    public ulong RemoteNextHtlcId { get; private init; }

    public LocalCommit LocalCommit { get; private init; }
    public RemoteCommit RemoteCommit { get; private init; }

    /// <summary>The peer commitment we signed and are waiting to see revoked, if any (D7).</summary>
    public RemoteNextCommit? RemoteNextCommit { get; private init; }

    /// <summary>The peer's per-commitment point for <c>RemoteCommit.Number + 1</c>; needed to sign (B2-CS-S06).</summary>
    public CompactPubKey? RemoteNextPerCommitmentPoint { get; private init; }

    /// <summary>
    /// The negotiated splices not locked yet, oldest first (splicing plan §3.3, D7): the HTLC set, fee updates and
    /// commitment numbers are shared, and every commitment is signed on each of them too (SP-OP-01/03). Empty for a
    /// channel without a pending splice, where the engine behaves exactly as the single-funding engine. The current
    /// funding is <see cref="CommitmentParams.Funding"/>.
    /// </summary>
    public ImmutableList<ChannelFunding> PendingFundings { get; private init; } = ImmutableList<ChannelFunding>.Empty;

    /// <summary>The current funding and the pending ones, or null when the engine has no funding data.</summary>
    public FundingSet? Fundings => Params.Funding is { } current ? new FundingSet(current, PendingFundings) : null;

    /// <summary>
    /// Simple taproot channels (bolt-simple-taproot.md, NL-877 T3): the peer's verification nonce for its next
    /// commitment, per active funding txid (<c>next_local_nonce</c> of <c>channel_ready</c>, then the
    /// <c>next_local_nonces</c> map of <c>revoke_and_ack</c> or <c>channel_reestablish</c>). Our partial signature of the
    /// peer's commitment on a funding needs its entry, and signing consumes it (one nonce signs one session); the next
    /// one comes with the peer's <c>revoke_and_ack</c>. Always empty for the other channel types.
    /// </summary>
    public ImmutableDictionary<TxId, MusigPublicNonce> RemoteNextNonces { get; private init; } =
        ImmutableDictionary<TxId, MusigPublicNonce>.Empty;

    /// <summary>True for a simple taproot channel (<see cref="CommitmentParams.OptionSimpleTaproot"/>).</summary>
    public bool IsSimpleTaproot => Params.OptionSimpleTaproot;

    private ChannelCommitments(ChannelId channelId, CommitmentParams @params, ulong localBalanceMsat,
                               ulong remoteBalanceMsat, ImmutableSortedDictionary<HtlcKey, HtlcRecord> htlcs,
                               ImmutableList<FeeUpdate> feeUpdates, ulong localNextHtlcId, ulong remoteNextHtlcId,
                               LocalCommit localCommit, RemoteCommit remoteCommit, RemoteNextCommit? remoteNextCommit,
                               CompactPubKey? remoteNextPerCommitmentPoint)
    {
        ChannelId = channelId;
        Params = @params;
        LocalBalanceMsat = localBalanceMsat;
        RemoteBalanceMsat = remoteBalanceMsat;
        Htlcs = htlcs;
        FeeUpdates = feeUpdates;
        LocalNextHtlcId = localNextHtlcId;
        RemoteNextHtlcId = remoteNextHtlcId;
        LocalCommit = localCommit;
        RemoteCommit = remoteCommit;
        RemoteNextCommit = remoteNextCommit;
        RemoteNextPerCommitmentPoint = remoteNextPerCommitmentPoint;
    }

    #region Creation

    /// <summary>
    /// The state right after the opening: no HTLCs, one final fee update, commitment numbers as given (0 for a fresh
    /// channel).
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="params">Static parameters.</param>
    /// <param name="localBalanceMsat">Our opening balance.</param>
    /// <param name="remoteBalanceMsat">The peer's opening balance (<c>push_msat</c> when we funded).</param>
    /// <param name="feeratePerKw">The opening <c>feerate_per_kw</c>.</param>
    /// <param name="remoteCurrentPerCommitmentPoint">The peer's point for its current commitment.</param>
    /// <param name="remoteNextPerCommitmentPoint">The peer's point for its next commitment (<c>channel_ready</c>).</param>
    /// <param name="localCommitRemoteSignatures">The peer's signatures of our current commitment.</param>
    /// <param name="localCommitmentNumber">Our current commitment number.</param>
    /// <param name="remoteCommitmentNumber">The peer's current commitment number.</param>
    /// <param name="remoteNextNonce">Simple taproot channels: the peer's verification nonce for its next commitment on
    /// the current funding (<c>channel_ready</c>'s <c>next_local_nonce</c>); null when not known yet (it can be given
    /// later with <see cref="ReceiveChannelReadyNonce"/>). Must be null for the other channel types.</param>
    /// <exception cref="ArgumentException">The balances don't add up to the funding amount, or a nonce is given for a
    /// channel that is not simple taproot or has no funding data.</exception>
    public static ChannelCommitments Create(ChannelId channelId, CommitmentParams @params, ulong localBalanceMsat,
                                            ulong remoteBalanceMsat, uint feeratePerKw,
                                            CompactPubKey remoteCurrentPerCommitmentPoint,
                                            CompactPubKey? remoteNextPerCommitmentPoint,
                                            CommitmentSignatures? localCommitRemoteSignatures = null,
                                            ulong localCommitmentNumber = 0, ulong remoteCommitmentNumber = 0,
                                            MusigPublicNonce? remoteNextNonce = null)
    {
        ArgumentNullException.ThrowIfNull(@params);
        if (checked(localBalanceMsat + remoteBalanceMsat) != @params.FundingMsat)
            throw new ArgumentException(
                $"Balances {localBalanceMsat} + {remoteBalanceMsat} msat != funding {@params.FundingMsat} msat");

        var feeOwner = @params.LocalIsFunder ? HtlcState.SentAddAckRevocation : HtlcState.RcvdAddAckRevocation;
        var fees = ImmutableList.Create(new FeeUpdate(0, feeratePerKw, feeOwner));
        var noHtlcs = ImmutableSortedDictionary<HtlcKey, HtlcRecord>.Empty;
        var localSpec = new CommitmentSpec(CommitmentSide.Local, feeratePerKw, localBalanceMsat, remoteBalanceMsat, []);
        var remoteSpec =
            new CommitmentSpec(CommitmentSide.Remote, feeratePerKw, localBalanceMsat, remoteBalanceMsat, []);

        var created = new ChannelCommitments(channelId, @params, localBalanceMsat, remoteBalanceMsat, noHtlcs, fees, 0,
                                             0,
                                             new LocalCommit(localCommitmentNumber, localSpec,
                                                             localCommitRemoteSignatures),
                                             new RemoteCommit(remoteCommitmentNumber, remoteSpec,
                                                              remoteCurrentPerCommitmentPoint),
                                             null, remoteNextPerCommitmentPoint);
        if (remoteNextNonce is not { } nonce)
            return created;

        if (!@params.OptionSimpleTaproot || @params.Funding is not { } funding)
            throw new ArgumentException("A remote nonce needs a simple taproot channel with funding data",
                                        nameof(remoteNextNonce));

        return created with
        {
            RemoteNextNonces = ImmutableDictionary<TxId, MusigPublicNonce>.Empty.Add(funding.FundingTxId, nonce)
        };
    }

    /// <summary>
    /// Rebuilds a snapshot from persisted parts (plan N5), checking the structural invariants.
    /// </summary>
    /// <exception cref="ArgumentException">A part is inconsistent (unknown HTLC state, owner/direction mismatch, id at
    /// or above the next id, balances not adding up to the funding amount, no fee update).</exception>
    public static ChannelCommitments Restore(ChannelId channelId, CommitmentParams @params, ulong localBalanceMsat,
                                             ulong remoteBalanceMsat, IEnumerable<HtlcRecord> htlcs,
                                             IEnumerable<FeeUpdate> feeUpdates, ulong localNextHtlcId,
                                             ulong remoteNextHtlcId, LocalCommit localCommit,
                                             RemoteCommit remoteCommit, RemoteNextCommit? remoteNextCommit,
                                             CompactPubKey? remoteNextPerCommitmentPoint,
                                             IEnumerable<ChannelFunding>? pendingFundings = null,
                                             IReadOnlyDictionary<TxId, MusigPublicNonce>? remoteNextNonces = null)
    {
        ArgumentNullException.ThrowIfNull(@params);
        if (remoteNextNonces is { Count: > 0 } && !@params.OptionSimpleTaproot)
            throw new ArgumentException("Remote nonces are stored for a channel that is not simple taproot",
                                        nameof(remoteNextNonces));

        var htlcMap = ImmutableSortedDictionary.CreateBuilder<HtlcKey, HtlcRecord>();
        foreach (var htlc in htlcs)
        {
            if (!HtlcStateTable.IsDefined(htlc.State))
                throw new ArgumentException($"HTLC {htlc.Key} has legacy or unknown state {htlc.State}");
            if (HtlcStateTable.Owner(htlc.State) != htlc.Direction)
                throw new ArgumentException($"HTLC {htlc.Key} state {htlc.State} does not match its direction");
            if (htlc.Id >= (htlc.Direction == HtlcDirection.Outgoing ? localNextHtlcId : remoteNextHtlcId))
                throw new ArgumentException($"HTLC {htlc.Key} id is not below the next id");
            if (HtlcStateTable.IsRemoval(htlc.State) != htlc.Removal is not null)
                throw new ArgumentException($"HTLC {htlc.Key} removal does not match state {htlc.State}");

            htlcMap.Add(htlc.Key, htlc);
        }

        var fees = feeUpdates.OrderBy(f => f.Sequence).ToImmutableList();
        if (fees.IsEmpty)
            throw new ArgumentException("At least one fee update is required", nameof(feeUpdates));
        var funder = @params.LocalIsFunder ? HtlcDirection.Outgoing : HtlcDirection.Incoming;
        if (fees.Any(f => !HtlcStateTable.IsDefined(f.State) || HtlcStateTable.IsRemoval(f.State)
                       || f.Owner != funder))
            throw new ArgumentException("Fee updates must be owned by the funder and in an add state",
                                        nameof(feeUpdates));
        if (!fees[0].IsInCommit(CommitmentSide.Local) || !fees[0].IsInCommit(CommitmentSide.Remote))
            throw new ArgumentException("The first fee update must be in both commitments", nameof(feeUpdates));

        var restored = new ChannelCommitments(channelId, @params, localBalanceMsat, remoteBalanceMsat,
                                              htlcMap.ToImmutable(), fees, localNextHtlcId, remoteNextHtlcId,
                                              localCommit, remoteCommit, remoteNextCommit,
                                              remoteNextPerCommitmentPoint)
        {
            RemoteNextNonces = remoteNextNonces is null
                                   ? ImmutableDictionary<TxId, MusigPublicNonce>.Empty
                                   : remoteNextNonces.ToImmutableDictionary()
        };
        var total = checked(localBalanceMsat + remoteBalanceMsat);
        if (total != @params.FundingMsat)
            throw new ArgumentException($"Balances add up to {total} msat, not {@params.FundingMsat}");

        var pending = pendingFundings?.ToImmutableList() ?? ImmutableList<ChannelFunding>.Empty;
        if (pending.IsEmpty)
        {
            if (localCommit.PendingFundingSignatures.Count != 0
             || remoteNextCommit?.PendingFundingSignatures.Count is > 0)
                throw new ArgumentException("Per-funding signatures without a pending funding");

            return restored;
        }

        if (@params.Funding is not { } current)
            throw new ArgumentException("Pending fundings need the current funding", nameof(@params));

        var set = FundingSet.Single(current);
        foreach (var funding in pending)
            set = set.AddPending(funding);

        restored = restored with { PendingFundings = pending };
        restored.CheckFundingSignatures(localCommit.PendingFundingSignatures, "local commitment");
        if (remoteNextCommit is not null)
            restored.CheckFundingSignatures(remoteNextCommit.PendingFundingSignatures, "unacked remote commitment");
        foreach (var funding in pending)
        {
            SpecFor(localCommit.Spec, funding);
            SpecFor(remoteCommit.Spec, funding);
            if (remoteNextCommit is not null)
                SpecFor(remoteNextCommit.Commit.Spec, funding);
        }

        return restored;
    }

    /// <summary>Per-funding signatures must name exactly the pending fundings, in order (SP-I2).</summary>
    private void CheckFundingSignatures(IReadOnlyList<FundingSignatures> signatures, string what)
    {
        if (!signatures.Select(s => s.FundingTxId).SequenceEqual(PendingFundings.Select(f => f.FundingTxId)))
            throw new ArgumentException($"The {what} does not hold one signature set per pending funding, in order");
    }

    #endregion

    #region Views

    /// <summary>
    /// The feerate of the latest <paramref name="side"/> commitment: the last fee update included in it (B2-FEE-X01).
    /// </summary>
    public uint FeeratePerKw(CommitmentSide side) => FeeUpdates.Last(f => f.IsInCommit(side)).FeeratePerKw;

    /// <summary>The feerate the next commitments converge to once every pending fee update is committed.</summary>
    public uint LatestFeeratePerKw => FeeUpdates[^1].FeeratePerKw;

    /// <summary>
    /// Builds the content of the latest <paramref name="side"/> commitment from the HTLC states ("reduce"): an HTLC in
    /// the commitment is debited from its offerer; one whose removal is committed there is moved to the receiver when
    /// fulfilled and returned to the offerer when failed; the feerate is the last fee update in that commitment.
    /// </summary>
    /// <remarks>After a <c>commitment_signed</c> is sent or received this equals the stored
    /// <see cref="RemoteNextCommit"/>/<see cref="LocalCommit"/> spec.</remarks>
    public CommitmentSpec BuildSpec(CommitmentSide side)
    {
        var view = BuildView(side, prospective: false, extra: null, feerateOverride: null);
        if (view.LocalMsat < 0 || view.RemoteMsat < 0)
            throw new InvalidOperationException($"Negative balance in the {side} commitment: engine invariant broken");

        return view.ToSpec();
    }

    /// <summary>
    /// The latest <paramref name="side"/> commitment on <paramref name="funding"/> (splicing plan §3.3): the shared
    /// reduce with the main balances moved by the funding's deltas. On the current funding (deltas 0) it is
    /// <see cref="BuildSpec(CommitmentSide)"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A balance is negative on that funding (engine invariant SP-I6
    /// broken).</exception>
    public CommitmentSpec BuildSpec(CommitmentSide side, ChannelFunding funding) => SpecFor(BuildSpec(side), funding);

    /// <summary>
    /// The same commitment on another funding: <paramref name="spec"/> (built on the current funding) with the main
    /// balances moved by the deltas of <paramref name="funding"/> (splicing plan §3.3; I6 per funding:
    /// <c>TotalMsat</c> becomes that funding's capacity). Returns <paramref name="spec"/> itself when the deltas are 0.
    /// </summary>
    /// <exception cref="InvalidOperationException">A balance would be negative (SP-I6 broken).</exception>
    public static CommitmentSpec SpecFor(CommitmentSpec spec, ChannelFunding funding)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(funding);
        if (funding.LocalBalanceDeltaMsat == 0 && funding.RemoteBalanceDeltaMsat == 0)
            return spec;

        var local = checked((long)spec.LocalMsat + funding.LocalBalanceDeltaMsat);
        var remote = checked((long)spec.RemoteMsat + funding.RemoteBalanceDeltaMsat);
        if (local < 0 || remote < 0)
            throw new InvalidOperationException(
                $"Negative balance in the {spec.Holder} commitment on funding {funding.FundingTxId}: invariant SP-I6 broken");

        return new CommitmentSpec(spec.Holder, spec.FeeratePerKw, (ulong)local, (ulong)remote, spec.Htlcs);
    }

    /// <summary>
    /// Every active funding as the update rules see it (SP-OP-01, SP-I6): the current funding first, with its deltas 0,
    /// then each pending funding. The reserves follow D9 (<see cref="CommitmentParams.LocalReserveMsatOn"/>).
    /// </summary>
    internal IEnumerable<FundingView> ActiveFundingViews()
    {
        yield return new FundingView(Params.Funding, true, 0, 0, (long)Params.LocalReserveMsatOn(Params.Funding),
                                     (long)Params.RemoteReserveMsatOn(Params.Funding),
                                     (long)Params.RemoteReceiveReserveMsatOn(Params.Funding));
        foreach (var funding in PendingFundings)
            yield return new FundingView(funding, false, funding.LocalBalanceDeltaMsat, funding.RemoteBalanceDeltaMsat,
                                         (long)Params.LocalReserveMsatOn(funding),
                                         (long)Params.RemoteReserveMsatOn(funding),
                                         (long)Params.RemoteReceiveReserveMsatOn(funding));
    }

    /// <summary>
    /// The "pending" view of a <paramref name="side"/> commitment used for update validation: every HTLC counts as
    /// present unless its removal is already committed in that commitment (adds count early, removals late, so the
    /// view is never more generous than any commitment that can actually be signed). <paramref name="extra"/> is a
    /// candidate HTLC. The feerate is the higher of the current and the latest pending one.
    /// </summary>
    /// <param name="side">The commitment.</param>
    /// <param name="extra">A candidate HTLC to include.</param>
    /// <param name="feerateOverride">The feerate to use instead.</param>
    /// <param name="peerView">Judge the peer's update by what it provably knew when it sent it: leave out our adds it
    /// has not signed yet (<see cref="HtlcState.SentAddHtlc"/>, <see cref="HtlcState.SentAddCommit"/>,
    /// <see cref="HtlcState.RcvdAddRevocation"/>), and count our removals it has already acked
    /// (<see cref="HtlcState.RcvdRemoveRevocation"/>) as done (credited to the offerer on a fail, to the receiver on a
    /// fulfill). The two directions cross, so the peer may have offered before it received our adds; its
    /// <c>commitment_signed</c> that covers them precedes (in its stream) every update it sends afterwards, and this
    /// stays true when updates are retransmitted after a reconnection. Likewise the <c>commitment_signed</c> that
    /// follows its <c>revoke_and_ack</c> always carries our acked removal, so the commitment that first holds the
    /// judged update holds the removal too. Only the fee is re-checked at commit time
    /// (<see cref="UpdateValidator.ValidateReceivedCommitFee"/>).</param>
    internal CommitmentView BuildProspectiveView(CommitmentSide side, HtlcRecord? extra = null,
                                                 uint? feerateOverride = null, bool peerView = false) =>
        BuildView(side, prospective: true, extra, feerateOverride, peerView);

    private CommitmentView BuildView(CommitmentSide side, bool prospective, HtlcRecord? extra, uint? feerateOverride,
                                     bool peerView = false)
    {
        var local = (long)LocalBalanceMsat;
        var remote = (long)RemoteBalanceMsat;
        var specHtlcs = new List<SpecHtlc>(Htlcs.Count + 1);
        var records = extra is null ? Htlcs.Values : Htlcs.Values.Append(extra);

        foreach (var htlc in records)
        {
            if (peerView && htlc.State is HtlcState.SentAddHtlc or HtlcState.SentAddCommit
                                                  or HtlcState.RcvdAddRevocation)
                continue;

            // Our removal the peer has acked (its revoke_and_ack covers it) is in the peer's own commitment and will be
            // in the next one it signs for us, ahead of any update it sends afterwards: the peer may already spend
            // what it frees (LND counts it as removed from then on).
            var removed = HtlcStateTable.IsRemovedFrom(htlc.State, side)
                       || (peerView && htlc.State == HtlcState.RcvdRemoveRevocation);
            var present = prospective ? !removed : htlc.IsInCommit(side);
            var amount = checked((long)htlc.AmountMsat);
            if (present)
            {
                if (htlc.Direction == HtlcDirection.Outgoing)
                    local = checked(local - amount);
                else
                    remote = checked(remote - amount);

                specHtlcs.Add(new SpecHtlc(htlc.Direction, htlc.Id, htlc.AmountMsat, htlc.PaymentHash,
                                           htlc.CltvExpiry));
            }
            else if (removed && htlc.Removal is { IsFulfill: true })
            {
                if (htlc.Direction == HtlcDirection.Outgoing)
                {
                    local = checked(local - amount);
                    remote = checked(remote + amount);
                }
                else
                {
                    remote = checked(remote - amount);
                    local = checked(local + amount);
                }
            }
        }

        var feerate = feerateOverride
                   ?? (prospective ? Math.Max(FeeratePerKw(side), LatestFeeratePerKw) : FeeratePerKw(side));
        return new CommitmentView(side, feerate, local, remote, specHtlcs);
    }

    /// <summary>
    /// True when a <c>commitment_signed</c> from us would carry at least one update (B2-CS-S01): an HTLC or fee update
    /// that <see cref="HtlcEvent.SendCommit"/> moves (states 10, 17, 32, 35).
    /// </summary>
    public bool HasPendingChangesForRemote => HasMovable(HtlcEvent.SendCommit);

    /// <summary>True when the peer has sent updates that its next <c>commitment_signed</c> must cover (12, 15, 30, 37).
    /// </summary>
    public bool HasPendingChangesForLocal => HasMovable(HtlcEvent.RecvCommit);

    /// <summary>
    /// We may sign now: changes are pending, no signed commitment is waiting for its <c>revoke_and_ack</c> (D7,
    /// B2-CS-S06), we know the peer's next per-commitment point and, for a simple taproot channel, its verification
    /// nonce on every active funding (<see cref="RemoteNextNonces"/>).
    /// </summary>
    public bool CanSendCommit => RemoteNextCommit is null && RemoteNextPerCommitmentPoint.HasValue
                              && HasPendingChangesForRemote && HasRemoteNoncesForActiveFundings;

    /// <summary>
    /// True when every active funding has a verification nonce of the peer in <see cref="RemoteNextNonces"/>; always
    /// true for a channel that is not simple taproot.
    /// </summary>
    public bool HasRemoteNoncesForActiveFundings =>
        !IsSimpleTaproot || (Params.Funding is { } current && RemoteNextNonces.ContainsKey(current.FundingTxId)
                                                            && PendingFundings.All(
                                                                   f => RemoteNextNonces.ContainsKey(f.FundingTxId)));

    /// <summary>
    /// No HTLC is left in either commitment (dust ones included), every fee update is in both commitments and no
    /// <c>revoke_and_ack</c> is owed (BOLT 2 "Channel Close": once this holds after both <c>shutdown</c>s, no update
    /// may be sent and the closing negotiation starts). The balances are then final.
    /// </summary>
    public bool IsCleared => Htlcs.IsEmpty && RemoteNextCommit is null && FeeUpdates.All(f => f.IsFinal);

    private bool HasMovable(HtlcEvent htlcEvent) =>
        Htlcs.Values.Any(h => HtlcStateTable.TryNext(h.State, htlcEvent, out _))
     || FeeUpdates.Any(f => HtlcStateTable.TryNext(f.State, htlcEvent, out _));

    /// <summary>Finds an HTLC.</summary>
    public HtlcRecord? GetHtlc(HtlcDirection direction, ulong id) =>
        Htlcs.TryGetValue(new HtlcKey(direction, id), out var htlc) ? htlc : null;

    /// <summary>
    /// This snapshot with <paramref name="records"/> in place of the HTLCs of the same keys, everything else kept (the
    /// pending fundings and the peer's taproot nonces included): for annotations outside the state machine such as
    /// <see cref="HtlcRecord.KnownPreimage"/>. Rebuilding the snapshot with <see cref="Restore"/> instead dropped the
    /// nonces, so a simple taproot channel could not sign again until a reconnection (NL-1090).
    /// </summary>
    /// <exception cref="ArgumentException">A record is unknown or changes its HTLC's state.</exception>
    public ChannelCommitments WithHtlcRecords(IEnumerable<HtlcRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var htlcs = Htlcs;
        foreach (var record in records)
        {
            if (!htlcs.TryGetValue(record.Key, out var current) || current.State != record.State)
                throw new ArgumentException($"HTLC {record.Key} is not in state {record.State}", nameof(records));

            htlcs = htlcs.SetItem(record.Key, record);
        }

        return this with { Htlcs = htlcs };
    }

    #endregion

    #region Adds

    /// <summary>
    /// Offers an HTLC to the peer (<c>update_add_htlc</c>): validates the BOLT 2 sender rules and assigns the next id.
    /// </summary>
    /// <param name="amountMsat">The amount.</param>
    /// <param name="paymentHash">The payment hash.</param>
    /// <param name="cltvExpiry">The absolute expiry height.</param>
    /// <param name="onionRoutingPacket">The onion to carry (opaque).</param>
    /// <param name="pathKey">The blinded-path <c>path_key</c>, if relaying inside a blinded route.</param>
    /// <param name="currentBlockHeight">When given, an HTLC that is already expired is refused (B2-CLTV-02).</param>
    /// <param name="wireCustomRecords">The custom records of the add's extension (a TLV stream of types of 65536 or
    /// more, <see cref="HtlcRecord.WireCustomRecords"/>; opaque to the engine), if any.</param>
    /// <exception cref="CommitmentRefusedException">A sender rule would be broken.</exception>
    public CommitmentsResult SendAdd(ulong amountMsat, Hash paymentHash, uint cltvExpiry,
                                     ReadOnlyMemory<byte> onionRoutingPacket, CompactPubKey? pathKey = null,
                                     uint? currentBlockHeight = null, ReadOnlyMemory<byte> wireCustomRecords = default)
    {
        var htlc = new HtlcRecord(HtlcDirection.Outgoing, LocalNextHtlcId, amountMsat, paymentHash, cltvExpiry,
                                  HtlcStateTable.Initial(HtlcDirection.Outgoing), null, onionRoutingPacket, pathKey,
                                  WireCustomRecords: wireCustomRecords);
        UpdateValidator.ValidateSendAdd(this, htlc, currentBlockHeight);

        var next = this with
        {
            Htlcs = Htlcs.Add(htlc.Key, htlc),
            LocalNextHtlcId = checked(LocalNextHtlcId + 1)
        };
        return Result(next, [new OutboundAddHtlc(htlc)]);
    }

    /// <summary>
    /// Accepts an <c>update_add_htlc</c> from the peer after checking the BOLT 2 receiver rules.
    /// </summary>
    /// <exception cref="CommitmentViolationException">A receiver rule is broken.</exception>
    public CommitmentsResult ReceiveAdd(ulong id, ulong amountMsat, Hash paymentHash, uint cltvExpiry,
                                        ReadOnlyMemory<byte> onionRoutingPacket, CompactPubKey? pathKey = null,
                                        ReadOnlyMemory<byte> wireCustomRecords = default)
    {
        if (id != RemoteNextHtlcId)
            throw Violation("B2-ADD-R07", $"update_add_htlc id {id}, expected {RemoteNextHtlcId}");

        var htlc = new HtlcRecord(HtlcDirection.Incoming, id, amountMsat, paymentHash, cltvExpiry,
                                  HtlcStateTable.Initial(HtlcDirection.Incoming), null, onionRoutingPacket, pathKey,
                                  WireCustomRecords: wireCustomRecords);
        UpdateValidator.ValidateReceiveAdd(this, htlc);

        var next = this with
        {
            Htlcs = Htlcs.Add(htlc.Key, htlc),
            RemoteNextHtlcId = checked(RemoteNextHtlcId + 1)
        };
        return Result(next, []);
    }

    #endregion

    #region Removals

    /// <summary>Fulfills an HTLC the peer offered (<c>update_fulfill_htlc</c>).</summary>
    /// <exception cref="CommitmentRefusedException">Unknown/own HTLC (B2-DEL-00), not locked in (B2-DEL-03), already
    /// removed (B2-DEL-R07) or wrong preimage (B2-DEL-R02).</exception>
    /// <param name="id">The peer's id of the HTLC.</param>
    /// <param name="paymentPreimage">The preimage.</param>
    /// <param name="sha256">A hasher for the preimage check.</param>
    /// <param name="attributionData">The <c>attribution_data</c> to send (opaque, 920 bytes), or empty for none.</param>
    /// <param name="fulfillmentPayload">The <c>fulfillment_payload</c> to send (opaque), or empty for none.</param>
    public CommitmentsResult SendFulfill(ulong id, Secret paymentPreimage, ISha256 sha256,
                                         ReadOnlyMemory<byte> attributionData = default,
                                         ReadOnlyMemory<byte> fulfillmentPayload = default)
    {
        var htlc = GetRemovableIncoming(id);
        if (!PreimageMatches(paymentPreimage, htlc.PaymentHash, sha256))
            throw new CommitmentRefusedException("B2-DEL-R02", $"Preimage does not match the hash of HTLC {id}");

        return SendRemove(htlc, HtlcRemoval.Fulfill(paymentPreimage, attributionData, fulfillmentPayload),
                          new OutboundFulfillHtlc(id, paymentPreimage, attributionData, fulfillmentPayload));
    }

    /// <summary>Fails an HTLC the peer offered (<c>update_fail_htlc</c>) with an opaque, already encrypted reason.</summary>
    /// <param name="id">The peer's id of the HTLC.</param>
    /// <param name="reason">The encrypted return packet.</param>
    /// <param name="attributionData">The <c>attribution_data</c> to send (opaque, 920 bytes), or empty for none.</param>
    /// <exception cref="CommitmentRefusedException">See <see cref="SendFulfill"/>.</exception>
    public CommitmentsResult SendFail(ulong id, ReadOnlyMemory<byte> reason,
                                      ReadOnlyMemory<byte> attributionData = default)
    {
        var htlc = GetRemovableIncoming(id);
        return SendRemove(htlc, HtlcRemoval.Fail(reason, attributionData),
                          new OutboundFailHtlc(id, reason, attributionData));
    }

    /// <summary>Fails an HTLC the peer offered because its onion is malformed (<c>update_fail_malformed_htlc</c>).</summary>
    /// <exception cref="CommitmentRefusedException">See <see cref="SendFulfill"/>; also a failure code without the
    /// <c>BADONION</c> bit (B2-DEL-R04) or a hash that is not 32 bytes.</exception>
    public CommitmentsResult SendFailMalformed(ulong id, ushort failureCode, ReadOnlyMemory<byte> sha256OfOnion)
    {
        if ((failureCode & BadOnionFlag) == 0)
            throw new CommitmentRefusedException("B2-DEL-R04", $"Failure code 0x{failureCode:x4} lacks BADONION");
        if (sha256OfOnion.Length != CryptoConstants.Sha256HashLen)
            throw new CommitmentRefusedException("B2-DEL-R04", "sha256_of_onion must be 32 bytes");

        var htlc = GetRemovableIncoming(id);
        return SendRemove(htlc, HtlcRemoval.FailMalformed(failureCode, sha256OfOnion),
                          new OutboundFailMalformedHtlc(id, failureCode, sha256OfOnion));
    }

    /// <summary>Applies the peer's <c>update_fulfill_htlc</c> for an HTLC we offered.</summary>
    /// <remarks>Raises <see cref="OutgoingHtlcFulfilled"/> at once (B2-FWD-05) unless the preimage was already known
    /// (a fulfill re-sent after a reconnection).</remarks>
    /// <exception cref="CommitmentViolationException">Unknown id or HTLC not in our current commitment (B2-DEL-R01),
    /// already removed (B2-DEL-R07) or wrong preimage (B2-DEL-R02).</exception>
    /// <param name="id">Our id of the HTLC.</param>
    /// <param name="paymentPreimage">The preimage.</param>
    /// <param name="sha256">A hasher for the preimage check.</param>
    /// <param name="attributionData">The received <c>attribution_data</c> (opaque), or empty for none. It is kept with
    /// the removal and carried by <see cref="OutgoingHtlcFulfilled"/>.</param>
    /// <param name="fulfillmentPayload">The received <c>fulfillment_payload</c> (opaque), or empty for none.</param>
    public CommitmentsResult ReceiveFulfill(ulong id, Secret paymentPreimage, ISha256 sha256,
                                            ReadOnlyMemory<byte> attributionData = default,
                                            ReadOnlyMemory<byte> fulfillmentPayload = default)
    {
        var htlc = GetRemovableOutgoing(id);
        if (!PreimageMatches(paymentPreimage, htlc.PaymentHash, sha256))
            throw Violation("B2-DEL-R02", $"Preimage does not hash to the payment_hash of HTLC {id}");

        return ReceiveRemove(htlc, HtlcRemoval.Fulfill(paymentPreimage, attributionData, fulfillmentPayload));
    }

    /// <summary>Applies the peer's <c>update_fail_htlc</c> for an HTLC we offered.</summary>
    /// <param name="id">Our id of the HTLC.</param>
    /// <param name="reason">The encrypted return packet.</param>
    /// <param name="attributionData">The received <c>attribution_data</c> (opaque), or empty for none. It is kept with
    /// the removal and carried by <see cref="OutgoingHtlcFailed"/>.</param>
    /// <exception cref="CommitmentViolationException">See <see cref="ReceiveFulfill"/>.</exception>
    public CommitmentsResult ReceiveFail(ulong id, ReadOnlyMemory<byte> reason,
                                         ReadOnlyMemory<byte> attributionData = default)
    {
        var htlc = GetRemovableOutgoing(id);
        return ReceiveRemove(htlc, HtlcRemoval.Fail(reason, attributionData));
    }

    /// <summary>Applies the peer's <c>update_fail_malformed_htlc</c> for an HTLC we offered.</summary>
    /// <exception cref="CommitmentViolationException">See <see cref="ReceiveFulfill"/>; also no <c>BADONION</c> bit
    /// (B2-DEL-R04, NL-023).</exception>
    public CommitmentsResult ReceiveFailMalformed(ulong id, ushort failureCode, ReadOnlyMemory<byte> sha256OfOnion)
    {
        var htlc = GetRemovableOutgoing(id);
        if ((failureCode & BadOnionFlag) == 0)
            throw Violation("B2-DEL-R04", $"update_fail_malformed_htlc failure_code 0x{failureCode:x4} lacks BADONION");

        return ReceiveRemove(htlc, HtlcRemoval.FailMalformed(failureCode, sha256OfOnion));
    }

    private HtlcRecord GetRemovableIncoming(ulong id)
    {
        var htlc = GetHtlc(HtlcDirection.Incoming, id)
                ?? throw new CommitmentRefusedException("B2-DEL-00", $"No HTLC {id} offered by the peer");
        if (HtlcStateTable.IsRemoval(htlc.State))
            throw new CommitmentRefusedException("B2-DEL-R07", $"HTLC {id} is already being removed");
        if (htlc.State != HtlcState.RcvdAddAckRevocation)
            throw new CommitmentRefusedException("B2-DEL-03", $"HTLC {id} is not irrevocably committed ({htlc.State})");

        return htlc;
    }

    private HtlcRecord GetRemovableOutgoing(ulong id)
    {
        var htlc = GetHtlc(HtlcDirection.Outgoing, id)
                ?? throw Violation("B2-DEL-R01", $"No HTLC {id} offered by us");
        if (HtlcStateTable.IsRemoval(htlc.State))
            throw Violation("B2-DEL-R07", $"HTLC {id} is already being removed");
        if (htlc.State != HtlcState.SentAddAckRevocation)
            throw Violation("B2-DEL-R01", $"HTLC {id} is not in our current commitment yet ({htlc.State})");

        return htlc;
    }

    private CommitmentsResult SendRemove(HtlcRecord htlc, HtlcRemoval removal, CommitmentOutbound outbound)
    {
        var removed = htlc with { State = HtlcStateTable.Next(htlc.State, HtlcEvent.SendRemove), Removal = removal };
        return Result(this with { Htlcs = Htlcs.SetItem(htlc.Key, removed) }, [outbound]);
    }

    private CommitmentsResult ReceiveRemove(HtlcRecord htlc, HtlcRemoval removal)
    {
        var removed = htlc with
        {
            State = HtlcStateTable.Next(htlc.State, HtlcEvent.RecvRemove),
            Removal = removal,
            KnownPreimage = removal.PaymentPreimage ?? htlc.KnownPreimage
        };
        return Result(this with { Htlcs = Htlcs.SetItem(htlc.Key, removed) }, []);
    }

    private static bool PreimageMatches(Secret preimage, Hash paymentHash, ISha256 sha256)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        Span<byte> hash = stackalloc byte[CryptoConstants.Sha256HashLen];
        sha256.ComputeHash(preimage, hash);
        return hash.SequenceEqual(paymentHash);
    }

    #endregion

    #region Fees

    /// <summary>
    /// Changes the feerate (<c>update_fee</c>, funder only). An unsigned fee update of ours is replaced.
    /// </summary>
    /// <exception cref="CommitmentRefusedException">We are not the funder (B2-FEE-S02) or could not pay the new fee
    /// above our reserve on the peer's next commitment (B2-FEE-R03, which the peer would enforce).</exception>
    public CommitmentsResult SendFee(uint feeratePerKw)
    {
        if (!Params.LocalIsFunder)
            throw new CommitmentRefusedException("B2-FEE-S02", "Only the funder sends update_fee");

        var next = this with { FeeUpdates = WithNewFeeUpdate(feeratePerKw, HtlcDirection.Outgoing) };
        UpdateValidator.ValidateSendFee(next);
        return Result(next, [new OutboundUpdateFee(feeratePerKw)]);
    }

    /// <summary>
    /// Applies the peer's <c>update_fee</c>. An unsigned fee update of the peer is replaced.
    /// </summary>
    /// <param name="feeratePerKw">The new feerate.</param>
    /// <param name="minAcceptableFeeratePerKw">Below this the feerate is "too low for timely processing".</param>
    /// <param name="maxAcceptableFeeratePerKw">Above this the feerate is "unreasonably large".</param>
    /// <exception cref="CommitmentViolationException">We are the funder (B2-FEE-R02), unreasonable feerate
    /// (B2-FEE-R01) or the peer cannot afford it on our commitment (B2-FEE-R03).</exception>
    public CommitmentsResult ReceiveFee(uint feeratePerKw, uint minAcceptableFeeratePerKw,
                                        uint maxAcceptableFeeratePerKw)
    {
        if (Params.LocalIsFunder)
            throw Violation("B2-FEE-R02", "update_fee from the non-funder");
        if (feeratePerKw < minAcceptableFeeratePerKw || feeratePerKw > maxAcceptableFeeratePerKw)
            throw Violation("B2-FEE-R01",
                            $"update_fee feerate {feeratePerKw} outside [{minAcceptableFeeratePerKw}, {maxAcceptableFeeratePerKw}]");

        var next = this with { FeeUpdates = WithNewFeeUpdate(feeratePerKw, HtlcDirection.Incoming) };
        UpdateValidator.ValidateReceiveFee(next);
        return Result(next, []);
    }

    private ImmutableList<FeeUpdate> WithNewFeeUpdate(uint feeratePerKw, HtlcDirection owner)
    {
        var initial = HtlcStateTable.Initial(owner);
        var sequence = checked(FeeUpdates[^1].Sequence + 1);
        var fees = FeeUpdates[^1].State == initial ? FeeUpdates.RemoveAt(FeeUpdates.Count - 1) : FeeUpdates;
        return fees.Add(new FeeUpdate(sequence, feeratePerKw, initial));
    }

    #endregion

    #region Commit and revoke

    /// <summary>
    /// Signs the peer's next commitment (<c>commitment_signed</c>) with every pending change.
    /// </summary>
    /// <exception cref="CommitmentRefusedException">Waiting for a <c>revoke_and_ack</c> or no next point (B2-CS-S06), or
    /// nothing to sign (B2-CS-S01).</exception>
    /// <exception cref="InvalidOperationException">The signer returned the wrong number of HTLC signatures.</exception>
    public CommitmentsResult SendCommit(ICommitmentSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        if (RemoteNextCommit is not null)
            throw new CommitmentRefusedException("B2-CS-S06", "Waiting for revoke_and_ack of the previous commitment");
        if (RemoteNextPerCommitmentPoint is not { } point)
            throw new CommitmentRefusedException("B2-CS-S06", "The peer's next per-commitment point is unknown");
        if (!HasPendingChangesForRemote)
            throw new CommitmentRefusedException("B2-CS-S01", "No updates to sign");
        if (!HasRemoteNoncesForActiveFundings)
            throw new CommitmentRefusedException("TAPROOT-NONCE",
                                                 "The peer's verification nonce for its next commitment is unknown");

        var advanced = Advance(HtlcEvent.SendCommit, out var settled);
        var spec = advanced.BuildSpec(CommitmentSide.Remote);
        var number = checked(RemoteCommit.Number + 1);
        var signatures = SignRemote(signer, Params.Funding, number, spec, point);

        // SP-OP-03: the same commitment number on every pending splice funding, the current funding first
        var pendingSignatures = PendingFundings
                               .Select(f => new FundingSignatures(
                                           f.FundingTxId,
                                           SignRemote(signer, f, number, SpecFor(spec, f), point)))
                               .ToList();
        // Every funding the commitment is signed on (the current one first) stays with it until it is revoked, so the
        // revocation log covers a funding discarded or replaced before the revoke_and_ack (SP-I5)
        var commit = new RemoteCommit(number, spec, point)
        {
            SignedOnFundings = PendingFundings.IsEmpty ? null : [Params.Funding!, .. PendingFundings]
        };
        var next = advanced with
        {
            RemoteNextCommit = new RemoteNextCommit(commit, signatures)
            {
                PendingFundingSignatures = pendingSignatures
            },
            // Each nonce signed one session: the peer's revoke_and_ack brings the next ones
            RemoteNextNonces = ImmutableDictionary<TxId, MusigPublicNonce>.Empty
        };

        // The funding_txid TLV names the engine's current funding, which a lock moves (byte-identical before a splice)
        if (pendingSignatures.Count == 0)
            return Result(next, [new OutboundCommitmentSigned(number, signatures, Params.Funding?.FundingTxId)],
                          settled);

        var outbound = new List<CommitmentOutbound>(pendingSignatures.Count + 2)
        {
            new OutboundStartBatch(pendingSignatures.Count + 1),
            new OutboundCommitmentSigned(number, signatures, Params.Funding!.FundingTxId)
        };
        outbound.AddRange(pendingSignatures.Select(s => new OutboundCommitmentSigned(number, s.Signatures,
                                                                                     s.FundingTxId)));
        return Result(next, outbound, settled);
    }

    /// <summary>Signs one remote commitment and checks the HTLC signature count.</summary>
    private CommitmentSignatures SignRemote(ICommitmentSigner signer, ChannelFunding? funding, ulong number,
                                            CommitmentSpec spec, CompactPubKey point,
                                            MusigPublicNonce? remoteNonce = null)
    {
        var nonce = remoteNonce;
        if (IsSimpleTaproot && nonce is null)
        {
            var fundingTxId = (funding ?? Params.Funding)?.FundingTxId
                           ?? throw new InvalidOperationException("A simple taproot engine needs its funding data");
            nonce = RemoteNextNonces.TryGetValue(fundingTxId, out var found)
                        ? found
                        : throw new CommitmentRefusedException(
                              "TAPROOT-NONCE", $"No verification nonce of the peer for funding {fundingTxId}");
        }

        var signatures = signer.SignRemoteCommitment(ChannelId, funding, number, spec, point, nonce);
        if (IsSimpleTaproot && signatures.PartialSignature is null)
            throw new InvalidOperationException("The signer returned no partial signature for a simple taproot channel");
        var expected =
            CommitmentFeeCalculator.UntrimmedHtlcCount(spec, Params.Remote.DustLimitSatoshis, Params.Format);
        if (signatures.HtlcSignatures.Count != expected)
            throw new InvalidOperationException(
                $"Signer returned {signatures.HtlcSignatures.Count} HTLC signatures, expected {expected}");

        return signatures;
    }

    /// <summary>
    /// Applies the peer's <c>commitment_signed</c> for our next commitment and revokes the previous one.
    /// </summary>
    /// <remarks>
    /// BOLT 2 puts no receiver requirement on a <c>commitment_signed</c> without updates (the MUST NOT is on the
    /// sender), so one is accepted: it only advances the commitment number. The returned
    /// <see cref="OutboundRevokeAndAck"/> must be sent (B2-CS-R05) after the new commitment is persisted (B2-CS-R06).
    /// </remarks>
    /// <exception cref="CommitmentViolationException">The funder (the peer) cannot pay the fee of the new commitment
    /// (B2-FEE-R03 when it carries a new feerate, else B2-ADD-R02), <c>num_htlcs</c> mismatch (B2-CS-R02) or invalid
    /// signature (B2-CS-R01, B2-CS-R03).</exception>
    /// <exception cref="CommitmentViolationException">Also (must fail the channel) when a splice is pending: a lone
    /// <c>commitment_signed</c> outside a <c>start_batch</c> (SP-OP-05); use <see cref="ReceiveCommitBatch"/>.</exception>
    public CommitmentsResult ReceiveCommit(CommitmentSignatures signatures, ICommitmentVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(verifier);
        if (!PendingFundings.IsEmpty)
            throw FailChannel("SP-OP-05",
                              $"commitment_signed outside a start_batch while {PendingFundings.Count} splice(s) are pending");

        return ReceiveCommitCore(signatures, [], verifier);
    }

    /// <summary>
    /// Applies a <c>start_batch</c> group of <c>commitment_signed</c> (BOLT 2 "Batching channel messages"; SP-OP-05,
    /// 06, 07): all verified first, then our commitment moves to the next number on every active funding at once and
    /// <b>one</b> <c>revoke_and_ack</c> is owed.
    /// </summary>
    /// <remarks>
    /// <para>A member without <c>funding_txid</c> fails the channel. Every active funding needs exactly one member (a
    /// missing or duplicated one fails the channel, SP-OP-05). Members whose <c>funding_txid</c> matches no active
    /// funding are ignored, with or without pending splices: they are obsolete ones the peer sent before our
    /// <c>splice_locked</c> or a sibling discard reached it (SP-OP-06; BOLT 2 rationale "we can safely ignore them by
    /// filtering on funding_txid", Eclair matches members to its active commitments the same way).</para>
    /// <para>All-or-nothing (splicing plan risk 1): any invalid member leaves this snapshot unchanged, so no secret is
    /// ever released for a partially verified batch.</para>
    /// </remarks>
    /// <param name="batch">The members in arrival order.</param>
    /// <param name="verifier">The signature verifier.</param>
    /// <exception cref="CommitmentViolationException">See above and <see cref="ReceiveCommit"/>.</exception>
    /// <exception cref="InvalidOperationException">The engine has no funding data (a batch cannot be matched).</exception>
    public CommitmentsResult ReceiveCommitBatch(IReadOnlyList<ReceivedCommitmentSigned> batch,
                                                ICommitmentVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(verifier);
        var current = Params.Funding
                   ?? throw new InvalidOperationException("A commitment_signed batch needs the channel's funding data");
        if (batch.Count == 0)
            throw FailChannel("SP-OP-05", "Empty commitment_signed batch");
        if (batch.Any(m => m.FundingTxId is null))
            throw FailChannel("SP-OP-05", "A batched commitment_signed has no funding_txid");

        // Obsolete members (fundings no longer active) are dropped before any check (SP-OP-06)
        var byFunding = batch.Where(m => IsActiveFunding(m.FundingTxId!.Value))
                             .GroupBy(m => m.FundingTxId!.Value)
                             .ToList();
        if (byFunding.FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw FailChannel("SP-OP-05", $"The batch holds {duplicate.Count()} commitment_signed for funding {duplicate.Key}");

        var currentSignatures = byFunding.FirstOrDefault(g => g.Key == current.FundingTxId)?.Single().Signatures
                             ?? throw FailChannel(PendingFundings.IsEmpty ? "SP-OP-06" : "SP-OP-05",
                                                  $"The batch has no commitment_signed for the current funding {current.FundingTxId}");
        if (PendingFundings.IsEmpty)
            return ReceiveCommitCore(currentSignatures, [], verifier);

        var pendingSignatures = new List<FundingSignatures>(PendingFundings.Count);
        foreach (var funding in PendingFundings)
        {
            var member = byFunding.FirstOrDefault(g => g.Key == funding.FundingTxId)?.Single()
                      ?? throw FailChannel("SP-OP-05",
                                           $"The batch has no commitment_signed for pending funding {funding.FundingTxId}");
            pendingSignatures.Add(new FundingSignatures(funding.FundingTxId, member.Signatures));
        }

        return ReceiveCommitCore(currentSignatures, pendingSignatures, verifier);
    }

    /// <summary>Whether <paramref name="fundingTxId"/> is the current funding or a pending splice.</summary>
    private bool IsActiveFunding(TxId fundingTxId) =>
        Params.Funding?.FundingTxId == fundingTxId || PendingFundings.Any(f => f.FundingTxId == fundingTxId);

    /// <summary>The <c>commitment_signed</c> receiver rules on every active funding, then one revocation.</summary>
    private CommitmentsResult ReceiveCommitCore(CommitmentSignatures signatures,
                                                IReadOnlyList<FundingSignatures> pendingSignatures,
                                                ICommitmentVerifier verifier)
    {
        var committed = Advance(HtlcEvent.RecvCommit, out var settledOnCommit);
        var spec = committed.BuildSpec(CommitmentSide.Local);
        UpdateValidator.ValidateReceivedCommitFee(this, spec);
        var number = checked(LocalCommit.Number + 1);

        // Every count first, then every signature: nothing is accepted unless the whole batch is valid (SP-I3)
        var members = new List<(ChannelFunding? Funding, CommitmentSpec Spec, CommitmentSignatures Signatures)>
        {
            (Params.Funding, spec, signatures)
        };
        members.AddRange(PendingFundings.Zip(pendingSignatures, (f, s) => ((ChannelFunding?)f, SpecFor(spec, f),
                                                                          s.Signatures)));
        var expected =
            CommitmentFeeCalculator.UntrimmedHtlcCount(spec, Params.Local.DustLimitSatoshis, Params.Format);
        foreach (var (funding, _, memberSignatures) in members)
            if (IsSimpleTaproot && memberSignatures.PartialSignature is null)
                throw FailChannel("TAPROOT-CS-R01",
                                  $"commitment_signed without partial_signature_with_nonce{Label(funding)}");
        foreach (var (funding, _, memberSignatures) in members)
            if (memberSignatures.HtlcSignatures.Count != expected)
                throw Violation("B2-CS-R02",
                                $"num_htlcs {memberSignatures.HtlcSignatures.Count}, expected {expected}{Label(funding)}");
        foreach (var (funding, memberSpec, memberSignatures) in members)
            if (!verifier.VerifyLocalCommitment(ChannelId, funding, number, memberSpec, memberSignatures))
                throw Violation("B2-CS-R01", $"Invalid signature for local commitment {number}{Label(funding)}");

        var localCommit = new LocalCommit(number, spec, signatures) { PendingFundingSignatures = pendingSignatures };
        var revoked = (committed with { LocalCommit = localCommit })
           .Advance(HtlcEvent.SendRevoke, out var settledOnRevoke);
        return Result(revoked, [new OutboundRevokeAndAck(LocalCommit.Number, checked(number + 1))],
                      settledOnCommit.Concat(settledOnRevoke).ToList());
    }

    /// <summary>A rule-message suffix naming a pending funding; empty for the current one.</summary>
    private string Label(ChannelFunding? funding) =>
        funding is null || ReferenceEquals(funding, Params.Funding) ? string.Empty
                                                                     : $" on splice funding {funding.FundingTxId}";

    /// <summary>
    /// Applies the peer's <c>revoke_and_ack</c>: checks the secret of its current commitment, then rotates to the
    /// commitment we signed last.
    /// </summary>
    /// <remarks>The caller stores the secret in the remote shachain (B2-RAA-R04) in the same save. Raises
    /// <see cref="IncomingHtlcLockedIn"/> for every incoming HTLC this locks in, and <see cref="OutgoingHtlcFailed"/>
    /// (failures only) then <see cref="OutgoingHtlcSettled"/> for every outgoing HTLC whose removal becomes irrevocable.
    /// </remarks>
    /// <param name="perCommitmentSecret">The revealed secret.</param>
    /// <param name="nextPerCommitmentPoint">The peer's point for the commitment after the one it now holds.</param>
    /// <param name="verifier">The secret verifier.</param>
    /// <param name="nextLocalNonces">Simple taproot channels: the message's <c>next_local_nonces</c> (funding txid to
    /// the peer's verification nonce for its next commitment), which replace <see cref="RemoteNextNonces"/>; every
    /// active funding needs an entry (entries for other fundings are dropped). Ignored for the other channel
    /// types.</param>
    /// <exception cref="CommitmentViolationException">No <c>commitment_signed</c> outstanding (B2-RAA-R03), a wrong
    /// secret (B2-RAA-R01, must fail the channel), or, for a simple taproot channel, no nonce for an active funding
    /// (must fail the channel).</exception>
    public CommitmentsResult ReceiveRevoke(Secret perCommitmentSecret, CompactPubKey nextPerCommitmentPoint,
                                           IRevocationVerifier verifier,
                                           IReadOnlyDictionary<TxId, MusigPublicNonce>? nextLocalNonces = null)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (RemoteNextCommit is not { } pending)
            throw Violation("B2-RAA-R03", "revoke_and_ack without an outstanding commitment_signed");
        if (!verifier.IsValidSecret(perCommitmentSecret, RemoteCommit.PerCommitmentPoint))
            throw new CommitmentViolationException("B2-RAA-R01",
                                                   $"per_commitment_secret does not match remote commitment {RemoteCommit.Number}",
                                                   ChannelId)
            { MustFailChannel = true };
        var nonces = IsSimpleTaproot ? CheckRemoteNonces(nextLocalNonces, "revoke_and_ack") : RemoteNextNonces;

        var next = Advance(HtlcEvent.RecvRevoke, out var settled) with
        {
            RemoteCommit = pending.Commit,
            RemoteNextCommit = null,
            RemoteNextPerCommitmentPoint = nextPerCommitmentPoint,
            RemoteNextNonces = nonces
        };
        var result = Result(next, [], settled);

        // The commitment the peer just revoked goes to the revocation log (BOLT 5 plan O1-T1), on every funding it was
        // signed on: one secret revokes the number on all of them (SP-OP-07, SP-I3, SP-I5)
        return result with
        {
            Transition = result.Transition with
            {
                RevokedRemoteCommit = RemoteCommit,
                RevokedRemoteCommitFundings = FundingsToRevokeOn(RemoteCommit)
            }
        };
    }

    /// <summary>
    /// The fundings other than the current one that <paramref name="revoked"/> was signed on: the ones recorded when it
    /// was signed (a sibling discarded or a funding replaced by a lock since then included, SP-I5), else, for a
    /// commitment restored without that record, the pending fundings. Null means none.
    /// </summary>
    private IReadOnlyList<ChannelFunding>? FundingsToRevokeOn(RemoteCommit revoked)
    {
        if (revoked.SignedOnFundings is not { } signedOn)
            return PendingFundings.IsEmpty ? null : PendingFundings;

        var current = Params.Funding?.FundingTxId;
        var others = signedOn.Where(f => f.FundingTxId != current).ToList();
        return others.Count == 0 ? null : others;
    }

    #region Simple taproot nonces

    /// <summary>
    /// Simple taproot channels: the peer's <c>channel_ready</c> <c>next_local_nonce</c>, its verification nonce for its
    /// next commitment on the current funding. Replaces <see cref="RemoteNextNonces"/> (a re-sent
    /// <c>channel_ready</c> after a reconnection carries the same nonce as its <c>channel_reestablish</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Not a simple taproot channel, or no funding data.</exception>
    public CommitmentsResult ReceiveChannelReadyNonce(MusigPublicNonce nonce)
    {
        if (!IsSimpleTaproot)
            throw new InvalidOperationException("Only a simple taproot channel takes the peer's verification nonce");
        var current = Params.Funding
                   ?? throw new InvalidOperationException("A simple taproot engine needs its funding data");

        return Result(this with
        {
            RemoteNextNonces = ImmutableDictionary<TxId, MusigPublicNonce>.Empty.Add(current.FundingTxId, nonce)
        }, []);
    }

    /// <summary>
    /// Simple taproot channels: the peer's <c>next_local_nonces</c> outside a <c>revoke_and_ack</c> (its
    /// <c>channel_reestablish</c>): replaces <see cref="RemoteNextNonces"/> with the entries of the active fundings.
    /// A retransmitted <c>commitment_signed</c> is then signed again with these (<see cref="ResignRemoteNextCommit"/>).
    /// </summary>
    /// <exception cref="CommitmentViolationException">An active funding has no entry (must fail the channel).</exception>
    /// <exception cref="InvalidOperationException">Not a simple taproot channel, or no funding data.</exception>
    public CommitmentsResult ReceiveRemoteNonces(IReadOnlyDictionary<TxId, MusigPublicNonce> nextLocalNonces)
    {
        ArgumentNullException.ThrowIfNull(nextLocalNonces);
        if (!IsSimpleTaproot)
            throw new InvalidOperationException("Only a simple taproot channel takes the peer's verification nonces");

        return Result(this with { RemoteNextNonces = CheckRemoteNonces(nextLocalNonces, "channel_reestablish") }, []);
    }

    /// <summary>
    /// Simple taproot channels: signs the unacked <see cref="RemoteNextCommit"/> again (same number and content) for
    /// its retransmission after a reconnection, with the peer's verification nonces from its
    /// <c>channel_reestablish</c> (<see cref="ReceiveRemoteNonces"/>), which this consumes. A MuSig2 signature is never
    /// replayed byte for byte (bolt-simple-taproot.md: fresh signing nonce, the peer's new verification nonce); the new
    /// signatures replace the stored sent ones.
    /// </summary>
    /// <returns>A result whose outbound is the <c>commitment_signed</c> (a <c>start_batch</c> group with pending
    /// splices), as <see cref="SendCommit"/>'s.</returns>
    /// <exception cref="CommitmentRefusedException">No unacked commitment, or a nonce is missing.</exception>
    /// <exception cref="InvalidOperationException">Not a simple taproot channel.</exception>
    public CommitmentsResult ResignRemoteNextCommit(ICommitmentSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        if (!IsSimpleTaproot)
            throw new InvalidOperationException("Only a simple taproot channel signs its retransmission again");
        if (RemoteNextCommit is not { } unacked)
            throw new CommitmentRefusedException("B2-RE-CS", "No unacked commitment_signed to sign again");
        if (!HasRemoteNoncesForActiveFundings)
            throw new CommitmentRefusedException("TAPROOT-NONCE",
                                                 "The peer's verification nonce for its next commitment is unknown");

        var commit = unacked.Commit;
        var signatures = SignRemote(signer, Params.Funding, commit.Number, commit.Spec, commit.PerCommitmentPoint);
        var pendingSignatures = PendingFundings
                               .Select(f => new FundingSignatures(
                                           f.FundingTxId,
                                           SignRemote(signer, f, commit.Number, SpecFor(commit.Spec, f),
                                                      commit.PerCommitmentPoint)))
                               .ToList();
        var next = this with
        {
            RemoteNextCommit = new RemoteNextCommit(commit, signatures) { PendingFundingSignatures = pendingSignatures },
            RemoteNextNonces = ImmutableDictionary<TxId, MusigPublicNonce>.Empty
        };

        if (pendingSignatures.Count == 0)
            return Result(next, [new OutboundCommitmentSigned(commit.Number, signatures, Params.Funding?.FundingTxId)]);

        var outbound = new List<CommitmentOutbound>(pendingSignatures.Count + 2)
        {
            new OutboundStartBatch(pendingSignatures.Count + 1),
            new OutboundCommitmentSigned(commit.Number, signatures, Params.Funding!.FundingTxId)
        };
        outbound.AddRange(pendingSignatures.Select(s => new OutboundCommitmentSigned(commit.Number, s.Signatures,
                                                                                     s.FundingTxId)));
        return Result(next, outbound);
    }

    /// <summary>
    /// The peer's nonce map restricted to the active fundings (BOLT simple taproot: a missing entry for an active
    /// funding fails the channel).
    /// </summary>
    private ImmutableDictionary<TxId, MusigPublicNonce> CheckRemoteNonces(
        IReadOnlyDictionary<TxId, MusigPublicNonce>? nonces, string message)
    {
        var current = Params.Funding
                   ?? throw new InvalidOperationException("A simple taproot engine needs its funding data");
        if (nonces is null || nonces.Count == 0)
            throw FailChannel("TAPROOT-NONCE-R01", $"{message} without next_local_nonces on a simple taproot channel");

        var builder = ImmutableDictionary.CreateBuilder<TxId, MusigPublicNonce>();
        foreach (var fundingTxId in PendingFundings.Select(f => f.FundingTxId).Prepend(current.FundingTxId))
        {
            if (!nonces.TryGetValue(fundingTxId, out var nonce))
                throw FailChannel("TAPROOT-NONCE-R01",
                                  $"{message} has no next_local_nonces entry for active funding {fundingTxId}");

            builder[fundingTxId] = nonce;
        }

        return builder.ToImmutable();
    }

    /// <summary>The nonces of <see cref="RemoteNextNonces"/> that are still for active fundings.</summary>
    private ImmutableDictionary<TxId, MusigPublicNonce> NoncesOfActiveFundings(ChannelCommitments next) =>
        next.RemoteNextNonces.IsEmpty
            ? next.RemoteNextNonces
            : next.RemoteNextNonces.RemoveRange(next.RemoteNextNonces.Keys
                                                    .Where(t => next.Params.Funding?.FundingTxId != t
                                                             && next.PendingFundings.All(f => f.FundingTxId != t))
                                                    .ToList());

    private static bool SameNonces(ImmutableDictionary<TxId, MusigPublicNonce> left,
                                   ImmutableDictionary<TxId, MusigPublicNonce> right) =>
        left.Count == right.Count
     && left.All(e => right.TryGetValue(e.Key, out var other) && other.Equals(e.Value));

    #endregion

    /// <summary>
    /// On disconnect, reverses every update the peer sent that no <c>commitment_signed</c> of the peer covered (BOLT 2
    /// §Message Retransmission): its unsigned adds are dropped (their ids will be re-sent: B2-ADD-R06), its unsigned
    /// removals return to <see cref="HtlcState.SentAddAckRevocation"/> and its unsigned fee update is dropped. Our own
    /// unsigned updates stay (they are re-sent on reestablish).
    /// </summary>
    /// <remarks>
    /// A reverted fulfill keeps its preimage in <see cref="HtlcRecord.KnownPreimage"/> (B2-RE-04: "the effects of
    /// update_fulfill_htlc are not completely reversed"). The caller must run this on every disconnect and after every
    /// <see cref="Restore"/> before <c>channel_reestablish</c>: <see cref="ReceiveAdd"/> expects the rewound id and treats
    /// a repeated one as a violation.
    /// </remarks>
    public CommitmentsResult RevertUncommitted()
    {
        var dropped = Htlcs.Values.Where(h => h.State == HtlcState.RcvdAddHtlc).ToList();
        var htlcs = Htlcs.RemoveRange(dropped.Select(h => h.Key));
        foreach (var htlc in Htlcs.Values.Where(h => h.State == HtlcState.RcvdRemoveHtlc))
            htlcs = htlcs.SetItem(htlc.Key, htlc with
            {
                State = HtlcState.SentAddAckRevocation,
                Removal = null,
                KnownPreimage = htlc.Removal?.PaymentPreimage ?? htlc.KnownPreimage
            });

        var next = this with
        {
            Htlcs = htlcs,
            FeeUpdates = FeeUpdates.RemoveAll(f => f.State == HtlcState.RcvdAddHtlc),
            RemoteNextHtlcId = dropped.Count == 0 ? RemoteNextHtlcId : dropped.Min(h => h.Id)
        };
        return Result(next, [], dropped: dropped);
    }

    /// <summary>
    /// Applies <paramref name="htlcEvent"/> to every HTLC and fee update it moves, folds final HTLCs into the balances
    /// and drops fee updates superseded by a final one.
    /// </summary>
    private ChannelCommitments Advance(HtlcEvent htlcEvent, out IReadOnlyList<HtlcRecord> settled)
    {
        var htlcs = Htlcs.ToBuilder();
        var local = LocalBalanceMsat;
        var remote = RemoteBalanceMsat;
        var settledList = new List<HtlcRecord>();

        foreach (var htlc in Htlcs.Values)
        {
            if (!HtlcStateTable.TryNext(htlc.State, htlcEvent, out var state))
                continue;

            var moved = htlc with { State = state };
            if (!HtlcStateTable.IsFinal(state))
            {
                htlcs[htlc.Key] = moved;
                continue;
            }

            htlcs.Remove(htlc.Key);
            settledList.Add(moved);
            if (moved.Removal is not { IsFulfill: true })
                continue;

            if (moved.Direction == HtlcDirection.Outgoing)
            {
                local = checked(local - moved.AmountMsat);
                remote = checked(remote + moved.AmountMsat);
            }
            else
            {
                remote = checked(remote - moved.AmountMsat);
                local = checked(local + moved.AmountMsat);
            }
        }

        var fees = FeeUpdates
                  .Select(f => HtlcStateTable.TryNext(f.State, htlcEvent, out var s) ? f with { State = s } : f)
                  .ToList();
        var lastFinal = fees.FindLastIndex(f => f.IsFinal);
        if (lastFinal > 0)
            fees.RemoveRange(0, lastFinal);

        settled = settledList;
        return this with
        {
            Htlcs = htlcs.ToImmutable(),
            FeeUpdates = fees.ToImmutableList(),
            LocalBalanceMsat = local,
            RemoteBalanceMsat = remote
        };
    }

    #endregion

    #region Splicing

    /// <summary>The most active fundings a channel may have: a <c>start_batch</c> carries at most 20 messages.</summary>
    public const int MaxActiveFundings = 20;

    /// <summary>
    /// No update is pending in either direction and no <c>revoke_and_ack</c> is awaited: the state quiescence
    /// guarantees (BOLT 2 <c>stfu</c>), in which both sides agree on the HTLC set and the commitment numbers a splice
    /// commitment uses (SP-I8).
    /// </summary>
    public bool IsSettledForSplice => RemoteNextCommit is null && !HasPendingChangesForRemote
                                                               && !HasPendingChangesForLocal;

    /// <summary>
    /// Signs the peer's commitment on a negotiated splice funding (SP-CS-01): the <b>current</b> remote commitment
    /// number and content, moved to <paramref name="funding"/>'s balances. Nothing advances and no
    /// <c>revoke_and_ack</c> is involved (SP-CS-02); the snapshot is unchanged (the interactive-tx session keeps what was
    /// sent, SP-I7).
    /// </summary>
    /// <param name="funding">The splice funding (not yet pending, or already pending for a retransmission).</param>
    /// <param name="signer">The signer.</param>
    /// <param name="remoteNonce">Simple taproot channels (NL-965, BOLTs PR #1324): the peer's verification nonce of its
    /// current commitment on the new funding, the <c>commit_nonces</c> of its <c>tx_complete</c> (or the
    /// <c>current_commit_nonce</c> of its <c>channel_reestablish</c> for a retransmission); required for such a
    /// channel (<see cref="RemoteNextNonces"/> holds the next commitment's nonces, not this one), ignored otherwise.</param>
    /// <returns>A result whose only outbound is the <see cref="OutboundCommitmentSigned"/> with the funding's txid.</returns>
    /// <exception cref="CommitmentRefusedException">Updates are pending (SP-I8), or a simple taproot channel without
    /// <paramref name="remoteNonce"/>.</exception>
    /// <exception cref="ArgumentException">The funding breaks the <see cref="FundingSet.AddPending"/> rules.</exception>
    /// <exception cref="InvalidOperationException">No funding data, or a balance would be negative on the funding.</exception>
    public CommitmentsResult SignSpliceCommitment(ChannelFunding funding, ICommitmentSigner signer,
                                                  MusigPublicNonce? remoteNonce = null)
    {
        ArgumentNullException.ThrowIfNull(funding);
        ArgumentNullException.ThrowIfNull(signer);
        CheckSpliceFunding(funding);
        if (!IsSettledForSplice)
            throw new CommitmentRefusedException("SP-CS-01", "Updates are pending: a splice commitment needs quiescence");
        if (IsSimpleTaproot && remoteNonce is null)
            throw new CommitmentRefusedException("TAPROOT-NONCE",
                                                 $"No commit nonce of the peer for splice funding {funding.FundingTxId}");

        var spec = SpecFor(RemoteCommit.Spec, funding);
        var signatures = SignRemote(signer, funding, RemoteCommit.Number, spec, RemoteCommit.PerCommitmentPoint,
                                    IsSimpleTaproot ? remoteNonce : null);
        return Result(this, [new OutboundCommitmentSigned(RemoteCommit.Number, signatures, funding.FundingTxId)]);
    }

    /// <summary>
    /// Verifies the peer's <c>commitment_signed</c> for our commitment on a negotiated splice funding (SP-CS-01: the
    /// current local number and content, moved to <paramref name="funding"/>'s balances) and adds the funding as
    /// pending with those signatures (SP-I2). No <c>revoke_and_ack</c> is owed (SP-CS-02).
    /// </summary>
    /// <remarks>Persist the result before <c>tx_signatures</c> releases the shared input (SP-I1). A retransmitted
    /// signature for an already pending funding replaces the stored one after the same checks.</remarks>
    /// <exception cref="CommitmentViolationException">Updates are pending (SP-I8), <c>num_htlcs</c> mismatch (B2-CS-R02)
    /// or an invalid signature (B2-CS-R01); the channel must fail.</exception>
    /// <exception cref="ArgumentException">The funding breaks the <see cref="FundingSet.AddPending"/> rules.</exception>
    /// <exception cref="InvalidOperationException">No funding data, too many fundings, or a negative balance.</exception>
    public CommitmentsResult ReceiveSpliceCommitment(ChannelFunding funding, CommitmentSignatures signatures,
                                                     ICommitmentVerifier verifier,
                                                     MusigPublicNonce? remoteNextNonce = null)
    {
        ArgumentNullException.ThrowIfNull(funding);
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(verifier);
        var alreadyPending = CheckSpliceFunding(funding);
        if (!IsSettledForSplice)
            throw FailChannel("SP-CS-01", "Splice commitment_signed while updates are pending");

        // NL-965: a simple taproot funding is active only with the peer's verification nonce for its next commitment on
        // it (its tx_complete commit_nonces, or its channel_reestablish map), which the next batch signs against
        var nonce = remoteNextNonce ?? (RemoteNextNonces.TryGetValue(funding.FundingTxId, out var known)
                                            ? known
                                            : (MusigPublicNonce?)null);
        if (IsSimpleTaproot && nonce is null)
            throw FailChannel("TAPROOT-NONCE-R01",
                              $"No next verification nonce of the peer for splice funding {funding.FundingTxId}");
        if (IsSimpleTaproot && signatures.PartialSignature is null)
            throw FailChannel("TAPROOT-CS-R01",
                              $"Splice commitment_signed without a partial signature on funding {funding.FundingTxId}");

        var spec = SpecFor(LocalCommit.Spec, funding);
        var expected =
            CommitmentFeeCalculator.UntrimmedHtlcCount(spec, Params.Local.DustLimitSatoshis, Params.Format);
        if (signatures.HtlcSignatures.Count != expected)
            throw FailChannel("B2-CS-R02",
                              $"num_htlcs {signatures.HtlcSignatures.Count}, expected {expected} on splice funding {funding.FundingTxId}");
        if (!verifier.VerifyLocalCommitment(ChannelId, funding, LocalCommit.Number, spec, signatures))
            throw FailChannel("B2-CS-R01",
                              $"Invalid signature for local commitment {LocalCommit.Number} on splice funding {funding.FundingTxId}");

        var entry = new FundingSignatures(funding.FundingTxId, signatures);
        var next = alreadyPending
                       ? this with
                       {
                           LocalCommit = LocalCommit with
                           {
                               PendingFundingSignatures = LocalCommit.PendingFundingSignatures
                                                                     .Select(s => s.FundingTxId == funding.FundingTxId
                                                                                      ? entry
                                                                                      : s)
                                                                     .ToList()
                           }
                       }
                       : this with
                       {
                           PendingFundings = PendingFundings.Add(funding),
                           LocalCommit = LocalCommit with
                           {
                               PendingFundingSignatures = [.. LocalCommit.PendingFundingSignatures, entry]
                           },
                           // Our splice commitment_signed signed the peer's current commitment on it too (SP-CS-01)
                           RemoteCommit = RemoteCommit with
                           {
                               SignedOnFundings = [.. RemoteCommit.SignedOnFundings ?? [Params.Funding!], funding]
                           }
                       };
        if (IsSimpleTaproot)
            next = next with { RemoteNextNonces = next.RemoteNextNonces.SetItem(funding.FundingTxId, nonce!.Value) };
        return Result(next, []);
    }

    /// <summary>
    /// Locks the pending funding <paramref name="fundingTxId"/> (<c>splice_locked</c> sent and received for it,
    /// SP-LK-03): it becomes the current funding with its deltas folded into the settled balances, every commitment
    /// moves to it with the signatures kept for it, and the other pending attempts are discarded. The retired fundings
    /// (the replaced one, the discarded ones) are in <see cref="ChannelTransition.RetiredFundings"/> for the lock's save;
    /// their revocation data must be kept (SP-I5).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="fundingTxId"/> is not pending (SP-LK-02 is the caller's).</exception>
    /// <exception cref="InvalidOperationException">No funding data.</exception>
    public CommitmentsResult LockFunding(TxId fundingTxId)
    {
        var set = Fundings ?? throw new InvalidOperationException("The engine has no funding data");
        var (nextSet, retired) = set.Lock(fundingTxId);
        var locked = PendingFundings.First(f => f.FundingTxId == fundingTxId);

        var localCommit = new LocalCommit(LocalCommit.Number, SpecFor(LocalCommit.Spec, locked),
                                          LocalCommit.SignaturesFor(fundingTxId)
                                       ?? throw new InvalidOperationException(
                                              $"No signatures of our commitment on funding {fundingTxId}"));
        var remoteNext = RemoteNextCommit is { } unacked
                             ? new RemoteNextCommit(RebaseOn(unacked.Commit, locked),
                                                    unacked.SignaturesFor(fundingTxId)
                                                 ?? throw new InvalidOperationException(
                                                        $"No sent signatures on funding {fundingTxId}"))
                             : null;
        var next = this with
        {
            Params = Params with { FundingSatoshis = locked.CapacitySatoshis, Funding = nextSet.Current },
            PendingFundings = ImmutableList<ChannelFunding>.Empty,
            LocalBalanceMsat = checked((ulong)((long)LocalBalanceMsat + locked.LocalBalanceDeltaMsat)),
            RemoteBalanceMsat = checked((ulong)((long)RemoteBalanceMsat + locked.RemoteBalanceDeltaMsat)),
            LocalCommit = localCommit,
            RemoteCommit = RebaseOn(RemoteCommit, locked),
            RemoteNextCommit = remoteNext
        };
        next = next with { RemoteNextNonces = NoncesOfActiveFundings(next) };
        return Result(next, [], retired: retired);
    }

    /// <summary>
    /// A remote commitment moved to the locked funding: its spec, and the deltas of the fundings it was signed on
    /// rebased on the locked one, so <see cref="SpecFor"/> still gives the commitment on each of them (the replaced
    /// funding gets the opposite of the locked delta).
    /// </summary>
    private static RemoteCommit RebaseOn(RemoteCommit commit, ChannelFunding locked) =>
        commit with
        {
            Spec = SpecFor(commit.Spec, locked),
            SignedOnFundings = commit.SignedOnFundings?
                                     .Select(f => f with
                                     {
                                         LocalBalanceDeltaMsat =
                                         checked(f.LocalBalanceDeltaMsat - locked.LocalBalanceDeltaMsat),
                                         RemoteBalanceDeltaMsat =
                                         checked(f.RemoteBalanceDeltaMsat - locked.RemoteBalanceDeltaMsat)
                                     })
                                     .ToList()
        };

    /// <summary>
    /// Discards pending fundings: the one named by <paramref name="fundingTxId"/> (an aborted negotiation or RBF
    /// attempt), or every one when null (a commitment of the current funding confirmed, splicing plan §3.6). Their
    /// signatures leave every commitment; the discarded fundings are in <see cref="ChannelTransition.RetiredFundings"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="fundingTxId"/> is not pending.</exception>
    public CommitmentsResult DiscardPendingFundings(TxId? fundingTxId = null)
    {
        var discarded = PendingFundings.Where(f => fundingTxId is null || f.FundingTxId == fundingTxId.Value).ToList();
        if (fundingTxId is { } txId && discarded.Count == 0)
            throw new ArgumentException($"Funding {txId} is not pending", nameof(fundingTxId));
        if (discarded.Count == 0)
            return Result(this, []);

        var gone = discarded.Select(f => f.FundingTxId).ToHashSet();
        var next = this with
        {
            PendingFundings = PendingFundings.RemoveAll(f => gone.Contains(f.FundingTxId)),
            LocalCommit = LocalCommit with
            {
                PendingFundingSignatures = LocalCommit.PendingFundingSignatures
                                                      .Where(s => !gone.Contains(s.FundingTxId)).ToList()
            },
            RemoteNextCommit = RemoteNextCommit is { } unacked
                                   ? unacked with
                                   {
                                       PendingFundingSignatures = unacked.PendingFundingSignatures
                                                                         .Where(s => !gone.Contains(s.FundingTxId))
                                                                         .ToList()
                                   }
                                   : null
        };
        next = next with { RemoteNextNonces = NoncesOfActiveFundings(next) };
        return Result(next, [], retired: discarded.Select(f => f with { Status = ChannelFundingStatus.Discarded })
                                                  .ToList());
    }

    /// <summary>
    /// Checks a splice funding against the current one: the <see cref="FundingSet.AddPending"/> rules for a new one, the
    /// same data for one already pending, the batch limit, and non-negative balances on both current commitments.
    /// </summary>
    /// <returns>True when the funding is already pending.</returns>
    private bool CheckSpliceFunding(ChannelFunding funding)
    {
        var set = Fundings ?? throw new InvalidOperationException("The engine has no funding data");
        var pending = PendingFundings.FirstOrDefault(f => f.FundingTxId == funding.FundingTxId);
        if (pending is not null)
        {
            if (!pending.Equals(funding))
                throw new ArgumentException($"Funding {funding.FundingTxId} is pending with other data",
                                            nameof(funding));
        }
        else
        {
            if (set.ActiveCount >= MaxActiveFundings)
                throw new InvalidOperationException($"A channel may have at most {MaxActiveFundings} active fundings");
            set.AddPending(funding);
        }

        SpecFor(LocalCommit.Spec, funding);
        SpecFor(RemoteCommit.Spec, funding);
        return pending is not null;
    }

    #endregion

    private CommitmentsResult Result(ChannelCommitments next, IReadOnlyList<CommitmentOutbound> outbound,
                                     IReadOnlyList<HtlcRecord>? settled = null,
                                     IReadOnlyList<HtlcRecord>? dropped = null,
                                     IReadOnlyList<ChannelFunding>? retired = null)
    {
        var upserted = next.Htlcs.Values
                           .Where(h => !Htlcs.TryGetValue(h.Key, out var old) || !old.Equals(h))
                           .ToList();
        var transition = new ChannelTransition(
            upserted, settled ?? [], dropped ?? [],
            FeeUpdatesChanged: !FeeUpdates.SequenceEqual(next.FeeUpdates),
            LocalCommitChanged: !ReferenceEquals(LocalCommit, next.LocalCommit),
            RemoteCommitChanged: !ReferenceEquals(RemoteCommit, next.RemoteCommit)
                              || !ReferenceEquals(RemoteNextCommit, next.RemoteNextCommit),
            ScalarsChanged: LocalBalanceMsat != next.LocalBalanceMsat || RemoteBalanceMsat != next.RemoteBalanceMsat
                         || LocalNextHtlcId != next.LocalNextHtlcId || RemoteNextHtlcId != next.RemoteNextHtlcId
                         || !Nullable.Equals(RemoteNextPerCommitmentPoint, next.RemoteNextPerCommitmentPoint)
                         || !SameNonces(RemoteNextNonces, next.RemoteNextNonces),
            FundingsChanged: !PendingFundings.SequenceEqual(next.PendingFundings)
                          || !Equals(Params.Funding, next.Params.Funding),
            RetiredFundings: retired);
        var events = ChannelDomainEvents.FromChange(ChannelId, Htlcs, next.Htlcs, transition.SettledHtlcs);
        return new CommitmentsResult(next, outbound, transition, events);
    }

    private CommitmentViolationException Violation(string requirementId, string message) =>
        new(requirementId, message, ChannelId);

    /// <summary>A violation for which BOLT 2 allows only "send an error and fail the channel".</summary>
    private CommitmentViolationException FailChannel(string requirementId, string message) =>
        new(requirementId, message, ChannelId) { MustFailChannel = true };
}