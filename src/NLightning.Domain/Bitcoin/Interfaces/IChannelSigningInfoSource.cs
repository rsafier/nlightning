namespace NLightning.Domain.Bitcoin.Interfaces;

using Channels.ValueObjects;

/// <summary>
/// Where the signer loads a channel's signing data from when the channel is not registered in its memory (NL-067): the
/// persisted channel (key index, funding outpoint and keys, the peer's HTLC basepoint and node id, the real short
/// channel id, the local commitment number, the data-loss flag and the commitment signed for broadcast). No private key
/// is stored: the signer re-derives every key from the node seed and the channel key index.
/// </summary>
public interface IChannelSigningInfoSource
{
    /// <summary>
    /// Loads the signing data of <paramref name="channelId"/>; false when the channel is unknown or not far enough
    /// (no funding output or no peer keys yet).
    /// </summary>
    bool TryGet(ChannelId channelId, out ChannelSigningInfo signingInfo);
}