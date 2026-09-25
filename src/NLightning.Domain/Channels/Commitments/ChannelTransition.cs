namespace NLightning.Domain.Channels.Commitments;

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
public sealed record ChannelTransition(
    IReadOnlyList<HtlcRecord> UpsertedHtlcs,
    IReadOnlyList<HtlcRecord> SettledHtlcs,
    IReadOnlyList<HtlcRecord> DroppedHtlcs,
    bool FeeUpdatesChanged,
    bool LocalCommitChanged,
    bool RemoteCommitChanged,
    bool ScalarsChanged)
{
    public bool IsEmpty => UpsertedHtlcs.Count == 0 && SettledHtlcs.Count == 0 && DroppedHtlcs.Count == 0
                        && !FeeUpdatesChanged && !LocalCommitChanged && !RemoteCommitChanged && !ScalarsChanged;
}