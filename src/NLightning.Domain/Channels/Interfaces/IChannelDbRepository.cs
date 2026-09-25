using NLightning.Domain.Crypto.ValueObjects;

namespace NLightning.Domain.Channels.Interfaces;

using Models;
using ValueObjects;

public interface IChannelDbRepository
{
    Task AddAsync(ChannelModel channelModel);
    Task UpdateAsync(ChannelModel channelModel);
    Task<ChannelModel?> GetByIdAsync(ChannelId channelId);
    Task<IEnumerable<ChannelModel>> GetAllAsync();
    Task<IEnumerable<ChannelModel>> GetReadyChannelsAsync();
    Task<IEnumerable<ChannelModel?>> GetByPeerIdAsync(CompactPubKey peerNodeId);

    /// <summary>
    /// Gets every persisted local scid alias with the channel it points to, including the aliases of channels that
    /// are not loaded in memory, so new aliases can be kept unique across restarts.
    /// </summary>
    Task<IReadOnlyCollection<(ChannelId ChannelId, ShortChannelId Alias)>> GetLocalAliasesAsync();
}