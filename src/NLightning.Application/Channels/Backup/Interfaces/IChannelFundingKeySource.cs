namespace NLightning.Application.Channels.Backup.Interfaces;

using Domain.Crypto.ValueObjects;

/// <summary>
/// Our funding public keys of a channel by its key index, without a registered channel (splicing plan D5, lane SP2-E):
/// what a backup check and a restore re-derive to prove that a backed-up funding key, rotated by a splice, is ours.
/// </summary>
public interface IChannelFundingKeySource
{
    /// <summary>
    /// Our funding public key number <paramref name="fundingKeyIndex"/> of the channel with key index
    /// <paramref name="channelKeyIndex"/> (index 0: the channel's original funding key), or null when this signer
    /// cannot derive rotated funding keys (the caller then refuses the channel rather than trust the backup).
    /// </summary>
    CompactPubKey? GetFundingPubKey(uint channelKeyIndex, uint fundingKeyIndex);
}