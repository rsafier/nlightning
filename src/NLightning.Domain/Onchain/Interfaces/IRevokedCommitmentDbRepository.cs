namespace NLightning.Domain.Onchain.Interfaces;

using Channels.ValueObjects;
using Models;

/// <summary>
/// Reads the revocation log (BOLT 5 plan O1-T1, table <c>RevokedCommitments</c>). Entries are written only by
/// <c>IChannelStateDbRepository.ApplyAsync</c>, in the save of the <c>revoke_and_ack</c> transition that revoked the
/// commitment (<see cref="Channels.Commitments.ChannelTransition.RevokedRemoteCommit"/>), and only for commitments with
/// at least one HTLC.
/// </summary>
public interface IRevokedCommitmentDbRepository
{
    /// <summary>The revoked commitment <paramref name="number"/> of the channel, or null when the log has no entry for
    /// it (it had no HTLC, it is not revoked, or it was revoked before the log existed: see
    /// <see cref="GetLogStartAsync"/>).</summary>
    Task<RevokedCommitmentModel?> GetAsync(ChannelId channelId, ulong number);

    /// <summary>Every entry of the channel, by number.</summary>
    Task<IReadOnlyList<RevokedCommitmentModel>> GetByChannelIdAsync(ChannelId channelId);

    /// <summary>
    /// The first revoked commitment number the log covers for the channel: 0 for channels created after migration
    /// <c>AddOnchainResolution</c>; for older channels the peer's commitment number at migration time. An older revoked
    /// commitment on chain has HTLC outputs that cannot be rebuilt (plan §8 risk 5).
    /// </summary>
    Task<ulong> GetLogStartAsync(ChannelId channelId);

    /// <summary>Stages removing every entry of the channel (once it is closed, plan §3.2 step 5).</summary>
    Task DeleteByChannelIdAsync(ChannelId channelId);
}