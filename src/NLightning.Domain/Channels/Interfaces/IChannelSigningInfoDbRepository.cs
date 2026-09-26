namespace NLightning.Domain.Channels.Interfaces;

using ValueObjects;

/// <summary>
/// Reads a channel's signing data straight from its persisted rows (NL-067), without loading the whole channel: works
/// for every state, including Closed channels and channels a full load refuses (legacy HTLC rows). Reached through
/// <c>IUnitOfWork.ChannelSigningInfoDbRepository</c>; read-only.
/// </summary>
public interface IChannelSigningInfoDbRepository
{
    /// <summary>
    /// The signing data of a stored channel with both key sets, or null. <see cref="ChannelSigningInfo.LocalCommitmentNumber"/>
    /// is the persisted local commitment number, <see cref="ChannelSigningInfo.BroadcastSignedCommitmentNumber"/> the
    /// lowest commitment number of the channel's <c>LocalCommitment</c> broadcast rows.
    /// </summary>
    Task<ChannelSigningInfo?> GetAsync(ChannelId channelId);

    /// <summary>The signing data of every stored channel with both key sets.</summary>
    Task<IReadOnlyDictionary<ChannelId, ChannelSigningInfo>> GetAllAsync();
}