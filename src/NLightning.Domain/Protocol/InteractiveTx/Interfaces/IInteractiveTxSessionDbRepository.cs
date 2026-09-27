namespace NLightning.Domain.Protocol.InteractiveTx.Interfaces;

using Channels.ValueObjects;

/// <summary>
/// The persisted interactive-tx negotiations (splicing plan §3.8, table <c>InteractiveTxSessions</c> from migration
/// <c>AddInteractiveTxSessions</c>, lane IT-C). Reached through <c>IUnitOfWork.InteractiveTxSessionDbRepository</c>.
/// </summary>
/// <remarks>
/// A negotiation is stored from the moment our <c>commitment_signed</c> for it is sent (BOLT 2: the node must remember
/// the negotiation from then on, so a reconnection can resume it with <c>next_funding</c>); an unsigned negotiation is
/// never stored and is lost on disconnection by design. Writes are staged and committed by
/// <c>IUnitOfWork.SaveChangesAsync</c>, in the same save as the channel transition they belong to.
/// </remarks>
public interface IInteractiveTxSessionDbRepository
{
    /// <summary>Stages a new row; a duplicate (<c>ChannelId</c>, <c>SessionId</c>) fails the save.</summary>
    void Add(InteractiveTxSessionModel session);

    /// <summary>Stages the replacement of the stored row with the same key.</summary>
    /// <exception cref="KeyNotFoundException">No such row.</exception>
    Task UpdateAsync(InteractiveTxSessionModel session);

    /// <summary>The row, or null.</summary>
    Task<InteractiveTxSessionModel?> GetByIdAsync(ChannelId channelId, Guid sessionId);

    /// <summary>Every row of the channel, oldest first (an RBF adds one row per attempt).</summary>
    Task<IReadOnlyList<InteractiveTxSessionModel>> GetByChannelIdAsync(ChannelId channelId);

    /// <summary>
    /// Every row not yet settled: not <c>Aborted</c>, and not both signed and marked settled by the driver (startup and
    /// <c>channel_reestablish</c> with <c>next_funding</c>). Oldest first.
    /// </summary>
    Task<IReadOnlyList<InteractiveTxSessionModel>> GetUnresolvedAsync();

    /// <summary>Stages the deletion of the row; false when there is none.</summary>
    Task<bool> DeleteAsync(ChannelId channelId, Guid sessionId);
}