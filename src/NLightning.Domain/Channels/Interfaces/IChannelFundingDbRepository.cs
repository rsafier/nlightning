namespace NLightning.Domain.Channels.Interfaces;

using Bitcoin.ValueObjects;
using Commitments;
using Money;
using Splicing;
using ValueObjects;

/// <summary>
/// Stores the fundings of a channel (splicing plan §3.3, §3.8, migration <c>AddSpliceFundings</c>, lane SP1-C): the
/// <c>ChannelFundings</c> rows, the commitments signed for a pending splice funding (the <c>Commitments</c> slots keyed
/// by that funding's txid), per-funding revocation-log rows, and the dual-funding columns of the channel row.
/// </summary>
/// <remarks>
/// <para>Every write is staged on the unit of work and commits with its <c>SaveChangesAsync</c>, so a splice step's
/// rows, the channel's commitment state (<see cref="IChannelStateDbRepository"/>) and anything else of the transition
/// go in one save (invariant SP-I7). Reads by key go through the change tracker and see what this unit of work
/// staged.</para>
/// <para>The commitment state machine's own slots are the rows of the channel's <b>current</b> funding
/// (<c>Channels.FundingTxId</c>), written only by <see cref="IChannelStateDbRepository"/>: the commitment members here
/// refuse the current funding. Once a splice is locked (<see cref="ApplyLockAsync"/> moves <c>Channels.FundingTxId</c>)
/// its rows become the state machine's.</para>
/// </remarks>
public interface IChannelFundingDbRepository
{
    /// <summary>Every funding of the channel, whatever its status, in creation order.</summary>
    Task<IReadOnlyList<ChannelFunding>> GetByChannelIdAsync(ChannelId channelId);

    /// <summary>
    /// The channels with a <see cref="Splicing.Enums.ChannelFundingStatus.Replaced"/> funding that has a short channel
    /// id (a locked splice retired it), read from the stored rows only: the channels whose retired short channel ids
    /// the startup rebuilds (NL-1358), so it never loads every channel.
    /// </summary>
    Task<IReadOnlyList<ChannelId>> GetChannelIdsWithRetiredFundingsAsync();

    /// <summary>
    /// The channel's <see cref="FundingSet"/>: its <see cref="Splicing.Enums.ChannelFundingStatus.Current"/> funding and
    /// its pending ones in creation order, or null when the channel has no current funding row (not funded yet).
    /// </summary>
    Task<FundingSet?> GetFundingSetAsync(ChannelId channelId);

    /// <summary>
    /// Stages a funding: inserted when new (after the channel's other fundings in creation order), else every field is
    /// replaced. A <see cref="Splicing.Enums.ChannelFundingStatus.Current"/> funding must be the channel's current
    /// funding outpoint; use <see cref="ApplyLockAsync"/> to move it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel does not exist, or a current funding other than the
    /// channel's.</exception>
    Task UpsertAsync(ChannelId channelId, ChannelFunding funding);

    /// <summary>
    /// The lock's save (splicing plan §3.5 step 8): stages <paramref name="newCurrent"/> (status
    /// <see cref="Splicing.Enums.ChannelFundingStatus.Current"/>) as the channel's current funding, the channel row's
    /// <c>FundingTxId</c>/<c>FundingOutputIndex</c>/<c>FundingAmountSatoshis</c> and real short channel id with it, and
    /// the <paramref name="retired"/> fundings (replaced and discarded). The commitment slots of the retired fundings
    /// are removed; the revocation log is kept (SP-I5).
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel or the new current funding's row does not
    /// exist.</exception>
    Task ApplyLockAsync(ChannelId channelId, ChannelFunding newCurrent, IReadOnlyList<ChannelFunding> retired);

    /// <summary>
    /// Stages our local commitment spending the pending funding <paramref name="fundingTxId"/> with the peer's
    /// signatures (the splice commitment step, SP-CS-01). Its save is what <c>ILightningSigner.MarkSpliceCommitmentPersisted</c>
    /// records (SP-I1), and the signer reloads it at registration.
    /// </summary>
    /// <exception cref="InvalidOperationException">The funding is the channel's current one or not a funding of the
    /// channel, or the commitment carries no remote signatures.</exception>
    Task StageLocalCommitmentAsync(ChannelId channelId, TxId fundingTxId, LocalCommit commit);

    /// <summary>
    /// Stages the peer's commitment spending the pending funding <paramref name="fundingTxId"/> and, when we signed it,
    /// our signatures (kept for retransmission).
    /// </summary>
    /// <exception cref="InvalidOperationException">The funding is the channel's current one or not a funding of the
    /// channel.</exception>
    Task StageRemoteCommitmentAsync(ChannelId channelId, TxId fundingTxId, RemoteCommit commit,
                                    CommitmentSignatures? sentSignatures);

    /// <summary>
    /// Stages the peer's commitment for the pending funding <paramref name="fundingTxId"/> that we signed and whose
    /// <c>revoke_and_ack</c> we wait for (a batched <c>commitment_signed</c>, SP-OP-03), or removes it when
    /// <paramref name="next"/> is null.
    /// </summary>
    /// <exception cref="InvalidOperationException">The funding is the channel's current one or not a funding of the
    /// channel.</exception>
    Task StageRemoteNextCommitmentAsync(ChannelId channelId, TxId fundingTxId, RemoteNextCommit? next);

    /// <summary>The stored unacked peer commitment for a pending funding, or null.</summary>
    Task<RemoteNextCommit?> GetRemoteNextCommitmentAsync(ChannelId channelId, TxId fundingTxId);

    /// <summary>Our stored local commitment for a pending funding, or null.</summary>
    Task<LocalCommit?> GetLocalCommitmentAsync(ChannelId channelId, TxId fundingTxId);

    /// <summary>The stored peer commitment for a pending funding and the signatures we sent for it, or null.</summary>
    Task<(RemoteCommit Commit, CommitmentSignatures? SentSignatures)?> GetRemoteCommitmentAsync(
        ChannelId channelId, TxId fundingTxId);

    /// <summary>
    /// Stages a revocation-log row for the peer's revoked commitment <paramref name="revoked"/> on the funding
    /// <paramref name="fundingTxId"/> (SP-I5), when it has HTLCs; the current funding's row is written by
    /// <see cref="IChannelStateDbRepository.ApplyAsync"/>.
    /// </summary>
    /// <returns>True when a row was staged.</returns>
    Task<bool> StageRevokedCommitmentAsync(ChannelId channelId, TxId fundingTxId, RemoteCommit revoked);

    /// <summary>
    /// Marks the channel dual-funded (splicing plan wave DF) with both sides' contributions to its funding output.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel does not exist.</exception>
    Task SetDualFundedAsync(ChannelId channelId, LightningMoney localContribution, LightningMoney remoteContribution);

    /// <summary>The contributions of a dual-funded channel, or null for a v1 channel.</summary>
    Task<(LightningMoney Local, LightningMoney Remote)?> GetDualFundedContributionsAsync(ChannelId channelId);

    /// <summary>
    /// Records the amount the opener pushed to the other side at the open (NL-605). The channel may be staged in this
    /// unit of work. The default is for test doubles that store none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel does not exist.</exception>
    Task SetPushAmountAsync(ChannelId channelId, LightningMoney pushAmount) => Task.CompletedTask;

    /// <summary>
    /// The amount the opener pushed at the open, or null when it was not recorded (channels opened before NL-605). The
    /// default is for test doubles that store none.
    /// </summary>
    Task<LightningMoney?> GetPushAmountAsync(ChannelId channelId) => Task.FromResult<LightningMoney?>(null);
}