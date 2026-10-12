namespace NLightning.Infrastructure.Persistence.Entities.Node;

using Domain.Crypto.ValueObjects;

/// <summary>
/// The latest <c>peer_storage</c> blob a peer asked us to keep (BOLT 1 <c>option_provide_storage</c>, migration
/// <c>AddPeerStorage</c>). One row per peer; no FK to <c>Peers</c> (an inbound peer may have no row there).
/// </summary>
public class PeerStorageBlobEntity
{
    public required CompactPubKey NodeId { get; set; }

    /// <summary>The blob, at most 65531 bytes.</summary>
    public required byte[] Blob { get; set; }

    /// <summary>When we stored it; UTC ticks (<c>UtcTicksConverter</c>).</summary>
    public required DateTimeOffset UpdatedAt { get; set; }

    internal PeerStorageBlobEntity()
    {
    }
}