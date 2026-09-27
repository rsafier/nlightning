namespace NLightning.Domain.Channels.Commitments;

using Splicing;

/// <summary>
/// What one engine operation changed, for the persistence layer to write in a single save (decision D3).
/// </summary>
/// <param name="UpsertedHtlcs">HTLC records that are new or whose state/removal changed.</param>
/// <param name="SettledHtlcs">HTLCs that reached a final state and were folded into the balances (their last record).</param>
/// <param name="DroppedHtlcs">Uncommitted peer adds removed by <see cref="ChannelCommitments.RevertUncommitted"/>.</param>
/// <param name="FeeUpdatesChanged">The fee update list changed (write <see cref="ChannelCommitments.FeeUpdates"/>).</param>
/// <param name="LocalCommitChanged">A new local commitment (write <see cref="ChannelCommitments.LocalCommit"/>).</param>
/// <param name="RemoteCommitChanged">The remote commitment rotated, or the unacked one was set or cleared.</param>
/// <param name="ScalarsChanged">Balances, next HTLC ids or the remote next point changed.</param>
/// <param name="RevokedRemoteCommit">The peer commitment this transition revoked (set only by
/// <see cref="ChannelCommitments.ReceiveRevoke"/>): the persistence layer keeps its spec in the revocation log, in the
/// same save as the <c>revoke_and_ack</c>, so a breach of it can be penalized output by output (BOLT 5 plan O1-T1,
/// D2).</param>
/// <param name="FundingsChanged">The fundings changed (a splice added, locked or discarded; splicing plan §3.3): write
/// <see cref="ChannelCommitments.PendingFundings"/>, the current funding of <see cref="ChannelCommitments.Params"/>
/// and the per-funding signatures of every commitment.</param>
/// <param name="RetiredFundings">Fundings that left the active set in this transition, with their final status (the
/// replaced current funding and the discarded siblings of a lock, or the discarded pending fundings); their revocation
/// data is kept (SP-I5). Null means none.</param>
/// <param name="RevokedRemoteCommitFundings">The fundings besides the current one that <see cref="RevokedRemoteCommit"/>
/// was also signed on (<see cref="RemoteCommit.SignedOnFundings"/>: pending ones, and ones discarded or replaced by a
/// lock since it was signed; the revoked commitment on each is <see cref="ChannelCommitments.SpecFor"/> of its spec;
/// SP-I3, SP-I5). Null means none (no splice was pending when it was signed).</param>
public sealed record ChannelTransition(
    IReadOnlyList<HtlcRecord> UpsertedHtlcs,
    IReadOnlyList<HtlcRecord> SettledHtlcs,
    IReadOnlyList<HtlcRecord> DroppedHtlcs,
    bool FeeUpdatesChanged,
    bool LocalCommitChanged,
    bool RemoteCommitChanged,
    bool ScalarsChanged,
    RemoteCommit? RevokedRemoteCommit = null,
    bool FundingsChanged = false,
    IReadOnlyList<ChannelFunding>? RetiredFundings = null,
    IReadOnlyList<ChannelFunding>? RevokedRemoteCommitFundings = null)
{
    public bool IsEmpty => UpsertedHtlcs.Count == 0 && SettledHtlcs.Count == 0 && DroppedHtlcs.Count == 0
                        && !FeeUpdatesChanged && !LocalCommitChanged && !RemoteCommitChanged && !ScalarsChanged
                        && RevokedRemoteCommit is null && !FundingsChanged && (RetiredFundings?.Count ?? 0) == 0;
}