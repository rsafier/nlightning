namespace NLightning.Infrastructure.Persistence.Entities.Node;

using Domain.Crypto.ValueObjects;

/// <summary>
/// The latest <c>peer_storage_retrieval</c> a peer sent us (BOLT 1 <c>option_provide_storage</c>, migration
/// <c>AddPeerStorageRetrievals</c>, NL-432). One row per peer; no FK to <c>Peers</c> (an inbound peer may have no row
/// there).
/// </summary>
public class PeerStorageRetrievalEntity
{
    public required CompactPubKey NodeId { get; set; }

    /// <summary>When it arrived; UTC ticks (<c>UtcTicksConverter</c>).</summary>
    public required DateTimeOffset ReceivedAt { get; set; }

    /// <summary>The blob as the peer sent it, at most 65531 bytes.</summary>
    public required byte[] Blob { get; set; }

    /// <summary>Whether it was the last blob we sent the peer (null when we had sent none in that process).</summary>
    public bool? MatchesLastSent { get; set; }

    /// <summary>The 32-byte ids of the channels it named that we had no record of, concatenated (empty for none).</summary>
    public required byte[] UnknownChannelIds { get; set; }

    internal PeerStorageRetrievalEntity()
    {
    }
}