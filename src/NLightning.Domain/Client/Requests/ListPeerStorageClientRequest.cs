namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;

/// <summary>
/// Lists what our peers handed back to us with <c>peer_storage_retrieval</c> and the blobs we keep for them
/// (<c>ClientCommand.ListPeerStorage</c>, 32; NL-432).
/// </summary>
public sealed class ListPeerStorageClientRequest
{
    public ListPeerStorageClientRequest(CompactPubKey? peerNodeId = null, bool includeBlob = false)
    {
        PeerNodeId = peerNodeId;
        IncludeBlob = includeBlob;
    }

    /// <summary>Only this peer; null for every peer.</summary>
    public CompactPubKey? PeerNodeId { get; }

    /// <summary>Also return each retrieved blob's bytes (up to 65531 each).</summary>
    public bool IncludeBlob { get; }
}