using System.Collections.Immutable;

namespace NLightning.Domain.Channels.Commitments;

using Crypto.Constants;
using Crypto.Hashes;
using Crypto.ValueObjects;
using Enums;
using Exceptions;
using Interfaces;
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
    /// <exception cref="ArgumentException">The balances don't add up to the funding amount.</exception>
    public static ChannelCommitments Create(ChannelId channelId, CommitmentParams @params, ulong localBalanceMsat,
                                            ulong remoteBalanceMsat, uint feeratePerKw,
                                            CompactPubKey remoteCurrentPerCommitmentPoint,
                                            CompactPubKey? remoteNextPerCommitmentPoint,
                                            CommitmentSignatures? localCommitRemoteSignatures = null,
                                            ulong localCommitmentNumber = 0, ulong remoteCommitmentNumber = 0)
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

        return new ChannelCommitments(channelId, @params, localBalanceMsat, remoteBalanceMsat, noHtlcs, fees, 0, 0,
                                      new LocalCommit(localCommitmentNumber, localSpec, localCommitRemoteSignatures),
                                      new RemoteCommit(remoteCommitmentNumber, remoteSpec,
                                                       remoteCurrentPerCommitmentPoint),
                                      null, remoteNextPerCommitmentPoint);
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
                                             CompactPubKey? remoteNextPerCommitmentPoint)
    {
        ArgumentNullException.ThrowIfNull(@params);
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
                                              remoteNextPerCommitmentPoint);
        var total = checked(localBalanceMsat + remoteBalanceMsat);
        if (total != @params.FundingMsat)
            throw new ArgumentException($"Balances add up to {total} msat, not {@params.FundingMsat}");

        return restored;
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
    /// B2-CS-S06) and we know the peer's next per-commitment point.
    /// </summary>
    public bool CanSendCommit => RemoteNextCommit is null && RemoteNextPerCommitmentPoint.HasValue
                              && HasPendingChangesForRemote;

    private bool HasMovable(HtlcEvent htlcEvent) =>
        Htlcs.Values.Any(h => HtlcStateTable.TryNext(h.State, htlcEvent, out _))
     || FeeUpdates.Any(f => HtlcStateTable.TryNext(f.State, htlcEvent, out _));

    /// <summary>Finds an HTLC.</summary>
    public HtlcRecord? GetHtlc(HtlcDirection direction, ulong id) =>
        Htlcs.TryGetValue(new HtlcKey(direction, id), out var htlc) ? htlc : null;

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
    /// <exception cref="CommitmentRefusedException">A sender rule would be broken.</exception>
    public CommitmentsResult SendAdd(ulong amountMsat, Hash paymentHash, uint cltvExpiry,
                                     ReadOnlyMemory<byte> onionRoutingPacket, CompactPubKey? pathKey = null,
                                     uint? currentBlockHeight = null)
    {
        var htlc = new HtlcRecord(HtlcDirection.Outgoing, LocalNextHtlcId, amountMsat, paymentHash, cltvExpiry,
                                  HtlcStateTable.Initial(HtlcDirection.Outgoing), null, onionRoutingPacket, pathKey);
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
                                        ReadOnlyMemory<byte> onionRoutingPacket, CompactPubKey? pathKey = null)
    {
        if (id != RemoteNextHtlcId)
            throw Violation("B2-ADD-R07", $"update_add_htlc id {id}, expected {RemoteNextHtlcId}");

        var htlc = new HtlcRecord(HtlcDirection.Incoming, id, amountMsat, paymentHash, cltvExpiry,
                                  HtlcStateTable.Initial(HtlcDirection.Incoming), null, onionRoutingPacket, pathKey);
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
    public CommitmentsResult SendFulfill(ulong id, Secret paymentPreimage, ISha256 sha256)
    {
        var htlc = GetRemovableIncoming(id);
        if (!PreimageMatches(paymentPreimage, htlc.PaymentHash, sha256))
            throw new CommitmentRefusedException("B2-DEL-R02", $"Preimage does not match the hash of HTLC {id}");

        return SendRemove(htlc, HtlcRemoval.Fulfill(paymentPreimage), new OutboundFulfillHtlc(id, paymentPreimage));
    }

    /// <summary>Fails an HTLC the peer offered (<c>update_fail_htlc</c>) with an opaque, already encrypted reason.</summary>
    /// <exception cref="CommitmentRefusedException">See <see cref="SendFulfill"/>.</exception>
    public CommitmentsResult SendFail(ulong id, ReadOnlyMemory<byte> reason)
    {
        var htlc = GetRemovableIncoming(id);
        return SendRemove(htlc, HtlcRemoval.Fail(reason), new OutboundFailHtlc(id, reason));
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
    /// <exception cref="CommitmentViolationException">Unknown id or HTLC not in our current commitment (B2-DEL-R01),
    /// already removed (B2-DEL-R07) or wrong preimage (B2-DEL-R02).</exception>
    public CommitmentsResult ReceiveFulfill(ulong id, Secret paymentPreimage, ISha256 sha256)
    {
        var htlc = GetRemovableOutgoing(id);
        if (!PreimageMatches(paymentPreimage, htlc.PaymentHash, sha256))
            throw Violation("B2-DEL-R02", $"Preimage does not hash to the payment_hash of HTLC {id}");

        return ReceiveRemove(htlc, HtlcRemoval.Fulfill(paymentPreimage));
    }

    /// <summary>Applies the peer's <c>update_fail_htlc</c> for an HTLC we offered.</summary>
    /// <exception cref="CommitmentViolationException">See <see cref="ReceiveFulfill"/>.</exception>
    public CommitmentsResult ReceiveFail(ulong id, ReadOnlyMemory<byte> reason)
    {
        var htlc = GetRemovableOutgoing(id);
        return ReceiveRemove(htlc, HtlcRemoval.Fail(reason));
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
        sha256.AppendData(preimage);
        sha256.GetHashAndReset(hash);
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

        var advanced = Advance(HtlcEvent.SendCommit, out var settled);
        var spec = advanced.BuildSpec(CommitmentSide.Remote);
        var number = checked(RemoteCommit.Number + 1);
        var signatures = signer.SignRemoteCommitment(ChannelId, number, spec, point);
        var expected = CommitmentFees.UntrimmedHtlcCount(spec, Params.Remote.DustLimitSatoshis, Params.OptionAnchors);
        if (signatures.HtlcSignatures.Count != expected)
            throw new InvalidOperationException(
                $"Signer returned {signatures.HtlcSignatures.Count} HTLC signatures, expected {expected}");

        var next = advanced with
        {
            RemoteNextCommit = new RemoteNextCommit(new RemoteCommit(number, spec, point), signatures)
        };
        return Result(next, [new OutboundCommitmentSigned(number, signatures)], settled);
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
    public CommitmentsResult ReceiveCommit(CommitmentSignatures signatures, ICommitmentVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(verifier);

        var committed = Advance(HtlcEvent.RecvCommit, out var settledOnCommit);
        var spec = committed.BuildSpec(CommitmentSide.Local);
        UpdateValidator.ValidateReceivedCommitFee(this, spec);
        var number = checked(LocalCommit.Number + 1);
        var expected = CommitmentFees.UntrimmedHtlcCount(spec, Params.Local.DustLimitSatoshis, Params.OptionAnchors);
        if (signatures.HtlcSignatures.Count != expected)
            throw Violation("B2-CS-R02", $"num_htlcs {signatures.HtlcSignatures.Count}, expected {expected}");
        if (!verifier.VerifyLocalCommitment(ChannelId, number, spec, signatures))
            throw Violation("B2-CS-R01", $"Invalid signature for local commitment {number}");

        var revoked = (committed with { LocalCommit = new LocalCommit(number, spec, signatures) })
           .Advance(HtlcEvent.SendRevoke, out var settledOnRevoke);
        return Result(revoked, [new OutboundRevokeAndAck(LocalCommit.Number, checked(number + 1))],
                      settledOnCommit.Concat(settledOnRevoke).ToList());
    }

    /// <summary>
    /// Applies the peer's <c>revoke_and_ack</c>: checks the secret of its current commitment, then rotates to the
    /// commitment we signed last.
    /// </summary>
    /// <remarks>The caller stores the secret in the remote shachain (B2-RAA-R04) in the same save.</remarks>
    /// <exception cref="CommitmentViolationException">No <c>commitment_signed</c> outstanding (B2-RAA-R03) or a wrong
    /// secret (B2-RAA-R01, must fail the channel).</exception>
    public CommitmentsResult ReceiveRevoke(Secret perCommitmentSecret, CompactPubKey nextPerCommitmentPoint,
                                           IRevocationVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (RemoteNextCommit is not { } pending)
            throw Violation("B2-RAA-R03", "revoke_and_ack without an outstanding commitment_signed");
        if (!verifier.IsValidSecret(perCommitmentSecret, RemoteCommit.PerCommitmentPoint))
            throw new CommitmentViolationException("B2-RAA-R01",
                                                   $"per_commitment_secret does not match remote commitment {RemoteCommit.Number}",
                                                   ChannelId)
            { MustFailChannel = true };

        var next = Advance(HtlcEvent.RecvRevoke, out var settled) with
        {
            RemoteCommit = pending.Commit,
            RemoteNextCommit = null,
            RemoteNextPerCommitmentPoint = nextPerCommitmentPoint
        };
        return Result(next, [], settled);
    }

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

    private CommitmentsResult Result(ChannelCommitments next, IReadOnlyList<CommitmentOutbound> outbound,
                                     IReadOnlyList<HtlcRecord>? settled = null,
                                     IReadOnlyList<HtlcRecord>? dropped = null)
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
                         || !Nullable.Equals(RemoteNextPerCommitmentPoint, next.RemoteNextPerCommitmentPoint));
        return new CommitmentsResult(next, outbound, transition);
    }

    private CommitmentViolationException Violation(string requirementId, string message) =>
        new(requirementId, message, ChannelId);
}