namespace NLightning.Domain.Onchain.Interfaces;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Models;

/// <summary>
/// Reads the revocation log (BOLT 5 plan O1-T1, table <c>RevokedCommitments</c>). Entries are written only by
/// <c>IChannelStateDbRepository.ApplyAsync</c>, in the save of the <c>revoke_and_ack</c> transition that revoked the
/// commitment (<see cref="Channels.Commitments.ChannelTransition.RevokedRemoteCommit"/>), once per funding it was signed
/// on (<see cref="Channels.Commitments.ChannelTransition.RevokedRemoteCommitFundings"/>, NL-479), and only for
/// commitments with at least one HTLC.
/// </summary>
public interface IRevokedCommitmentDbRepository
{
    /// <summary>The revoked commitment <paramref name="number"/> of the channel, or null when the log has no entry for
    /// it (it had no HTLC, it is not revoked, or it was revoked before the log existed: see
    /// <see cref="GetLogStartAsync"/>).</summary>
    /// <remarks>Since splicing a number can be logged once per funding (SP-I5): this returns the current funding's
    /// entry, else the one of the funding created first. A caller that knows which funding the revoked commitment spends
    /// uses <see cref="GetAsync(ChannelId, TxId, ulong)"/>.</remarks>
    Task<RevokedCommitmentModel?> GetAsync(ChannelId channelId, ulong number);

    /// <summary>The revoked commitment <paramref name="number"/> logged for the funding <paramref name="fundingTxId"/>
    /// (the outpoint the breach spends), or null when that funding has no entry for it.</summary>
    Task<RevokedCommitmentModel?> GetAsync(ChannelId channelId, TxId fundingTxId, ulong number);

    /// <summary>Every entry of the channel, by number (then funding txid): a number logged on several fundings appears
    /// once per funding.</summary>
    Task<IReadOnlyList<RevokedCommitmentModel>> GetByChannelIdAsync(ChannelId channelId);

    /// <summary>
    /// Every entry logged for the funding <paramref name="fundingTxId"/> of the channel, by number (SP-I5: the
    /// classifier and the revoked resolver judge a breach of a pending, replaced or discarded funding against that
    /// funding's log; NL-479). Rows staged by the same unit of work are included. The default member throws for test
    /// doubles that keep no per-funding log.
    /// </summary>
    Task<IReadOnlyList<RevokedCommitmentModel>> GetByFundingAsync(ChannelId channelId, TxId fundingTxId) =>
        throw new NotSupportedException("This revocation log keeps no per-funding rows");

    /// <summary>
    /// The first revoked commitment number the log covers for the channel: 0 for channels created after migration
    /// <c>AddOnchainResolution</c>; for older channels the peer's commitment number at migration time. An older revoked
    /// commitment on chain has HTLC outputs that cannot be rebuilt (plan §8 risk 5).
    /// </summary>
    Task<ulong> GetLogStartAsync(ChannelId channelId);

    /// <summary>Stages removing every entry of the channel (once it is closed, plan §3.2 step 5).</summary>
    Task DeleteByChannelIdAsync(ChannelId channelId);
}