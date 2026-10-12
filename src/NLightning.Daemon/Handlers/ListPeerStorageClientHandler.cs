namespace NLightning.Daemon.Handlers;

using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.PeerStorage;
using Interfaces;

/// <summary>
/// Lists the latest <c>peer_storage_retrieval</c> of each peer (kept across restarts) and the blobs we keep for our
/// peers (ClientCommand 32, <c>listpeerstorage</c>, NL-432).
/// </summary>
/// <remarks>
/// Each channel a retrieved backup names says whether the node had a record of it when the retrieval arrived and
/// whether it has one now: a channel unknown now is one to restore from the static channel backup
/// (<c>restorechanbackup</c>). A node without peer storage answers <see cref="ErrorCodes.InvalidOperation"/>.
/// </remarks>
public sealed class ListPeerStorageClientHandler
    : IClientCommandHandler<ListPeerStorageClientRequest, ListPeerStorageClientResponse>
{
    private readonly IPeerStorageService? _peerStorageService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ListPeerStorage;

    public ListPeerStorageClientHandler(IPeerStorageService? peerStorageService)
    {
        _peerStorageService = peerStorageService;
    }

    /// <inheritdoc/>
    public async Task<ListPeerStorageClientResponse> HandleAsync(ListPeerStorageClientRequest request,
                                                                 CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_peerStorageService is null)
            throw new ClientException(ErrorCodes.InvalidOperation, "Peer storage is not available on this node.");

        IEnumerable<PeerStorageRetrievalReport> retrievals = await _peerStorageService.ListRetrievalsAsync(ct);
        IEnumerable<StoredPeerBlob> stored = await _peerStorageService.ListStoredBlobsAsync(ct);
        IEnumerable<PeerStorageRefusalReport> refusals = _peerStorageService.GetRefusals();
        if (request.PeerNodeId is { } peer)
        {
            retrievals = retrievals.Where(r => r.PeerNodeId.Equals(peer));
            stored = stored.Where(b => b.PeerNodeId.Equals(peer));
            refusals = refusals.Where(r => r.PeerNodeId.Equals(peer));
        }

        return new ListPeerStorageClientResponse(retrievals.ToList(), stored.ToList(),
                                                 _peerStorageService.BackupsHeldForDataLoss, request.IncludeBlob,
                                                 refusals.ToList());
    }
}