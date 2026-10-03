namespace NLightning.Domain.Channels.Models;

using Commitments;
using Enums;
using Protocol.Models;

/// <summary>
/// The commitment state of one channel as reloaded from the database (plan N5-T2).
/// </summary>
/// <remarks>
/// <see cref="Commitments"/> comes straight from <see cref="ChannelCommitments.Restore"/>: after a restart the caller
/// must run <see cref="ChannelCommitments.RevertUncommitted"/> (and persist its transition) before
/// <c>channel_reestablish</c>.
/// </remarks>
/// <param name="Commitments">The restored engine snapshot.</param>
/// <param name="SettledHtlcs">HTLCs that reached a final state (19 or 39) and are kept as an archive until pruned, so
/// the events of the transition that settled them can be re-derived after a crash (invariant I8).</param>
/// <param name="SentCommitDiff">The stored <c>commitment_signed</c> diff while the peer has not revoked yet (D4).</param>
/// <param name="LastSent">Which of <c>commitment_signed</c>/<c>revoke_and_ack</c> was sent last.</param>
/// <param name="RemoteShachain">The peer's shachain buckets, for <c>ISecretStorageService.Load</c>.</param>
public sealed record PersistedChannelState(
    ChannelCommitments Commitments,
    IReadOnlyList<HtlcRecord> SettledHtlcs,
    ReadOnlyMemory<byte>? SentCommitDiff,
    LastSentCommitmentMessage LastSent,
    IReadOnlyList<ShachainEntry> RemoteShachain);