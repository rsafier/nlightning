namespace NLightning.Domain.Client.Responses;

using Node.PeerStorage;

/// <summary>
/// The outcome of <c>listpeerstorage</c> (<c>ClientCommand.ListPeerStorage</c>, 32; NL-432).
/// </summary>
public sealed class ListPeerStorageClientResponse
{
    public ListPeerStorageClientResponse(IReadOnlyList<PeerStorageRetrievalReport> retrievals,
                                         IReadOnlyList<StoredPeerBlob> storedBlobs, bool backupsHeldForDataLoss,
                                         bool includesBlobs)
    {
        Retrievals = retrievals;
        StoredBlobs = storedBlobs;
        BackupsHeldForDataLoss = backupsHeldForDataLoss;
        IncludesBlobs = includesBlobs;
    }

    /// <summary>The latest retrieval of each peer, ordered by arrival.</summary>
    public IReadOnlyList<PeerStorageRetrievalReport> Retrievals { get; }

    /// <summary>The blobs we keep for our peers (as a provider).</summary>
    public IReadOnlyList<StoredPeerBlob> StoredBlobs { get; }

    /// <summary>True when our backups go to no peer until the restart (a retrieval named channels we do not know).</summary>
    public bool BackupsHeldForDataLoss { get; }

    /// <summary>True when the caller asked for the retrieved blobs' bytes.</summary>
    public bool IncludesBlobs { get; }
}