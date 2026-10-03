namespace NLightning.Domain.Client.Responses;

using Crypto.ValueObjects;

/// <summary>
/// The outcome of <c>disconnect</c> (<c>ClientCommand.DisconnectPeer</c>): the peer, its channels that are not closed
/// and the HTLCs they had in flight when it was disconnected (only non-zero with <c>--force</c>).
/// </summary>
public sealed class DisconnectPeerClientResponse
{
    public DisconnectPeerClientResponse(CompactPubKey nodeId, int channelCount, int htlcsInFlight)
    {
        NodeId = nodeId;
        ChannelCount = channelCount;
        HtlcsInFlight = htlcsInFlight;
    }

    public CompactPubKey NodeId { get; }
    public int ChannelCount { get; }
    public int HtlcsInFlight { get; }
}