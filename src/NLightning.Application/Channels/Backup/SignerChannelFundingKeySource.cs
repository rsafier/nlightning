namespace NLightning.Application.Channels.Backup;

using Domain.Bitcoin.Interfaces;
using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Signers;
using Interfaces;

/// <summary>
/// <see cref="IChannelFundingKeySource"/> over the node's <see cref="ILightningSigner"/>: index 0 is the channel's
/// basepoint funding key (<see cref="ILightningSigner.GetChannelBasepoints(uint)"/>); a rotated key comes from
/// <see cref="LocalLightningSigner.GetFundingPubKey(uint, uint)"/> (<c>m/0'/i'</c> of the channel key), which the
/// Domain signer port does not expose for an unregistered channel yet. Another signer derives only index 0.
/// </summary>
public sealed class SignerChannelFundingKeySource : IChannelFundingKeySource
{
    private readonly ILightningSigner _signer;

    public SignerChannelFundingKeySource(ILightningSigner signer)
    {
        _signer = signer;
    }

    /// <inheritdoc />
    public CompactPubKey? GetFundingPubKey(uint channelKeyIndex, uint fundingKeyIndex)
    {
        if (fundingKeyIndex == 0)
            return _signer.GetChannelBasepoints(channelKeyIndex).FundingPubKey;

        return _signer is LocalLightningSigner localSigner
                   ? localSigner.GetFundingPubKey(channelKeyIndex, fundingKeyIndex)
                   : null;
    }
}