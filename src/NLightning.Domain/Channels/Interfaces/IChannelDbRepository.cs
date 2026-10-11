using NLightning.Domain.Crypto.ValueObjects;

namespace NLightning.Domain.Channels.Interfaces;

using Models;
using ValueObjects;

public interface IChannelDbRepository
{
    Task AddAsync(ChannelModel channelModel);
    Task UpdateAsync(ChannelModel channelModel);
    Task<ChannelModel?> GetByIdAsync(ChannelId channelId);

    /// <summary>
    /// Whether the channel is stored, without loading it (no HTLC, commitment or key rows are read; NL-243). Prefer it
    /// to <see cref="GetByIdAsync"/> as an existence check: it also never throws for a channel with legacy HTLC rows.
    /// </summary>
    Task<bool> ExistsAsync(ChannelId channelId);
    Task<IEnumerable<ChannelModel>> GetAllAsync();
    Task<IEnumerable<ChannelModel>> GetReadyChannelsAsync();
    Task<IEnumerable<ChannelModel?>> GetByPeerIdAsync(CompactPubKey peerNodeId);

    /// <summary>
    /// Gets every persisted local scid alias with the channel it points to, including the aliases of channels that
    /// are not loaded in memory, so new aliases can be kept unique across restarts.
    /// </summary>
    Task<IReadOnlyCollection<(ChannelId ChannelId, ShortChannelId Alias)>> GetLocalAliasesAsync();

    /// <summary>
    /// Gets the highest channel key index of our key sets over every stored channel row, whatever its state (0 when
    /// there is none). The daemon raises the key file's last used index to it at startup (SECURITY_REVIEW SR-19).
    /// </summary>
    Task<uint> GetHighestLocalKeyIndexAsync();
}