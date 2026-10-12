namespace NLightning.Domain.Node.PeerStorage;

/// <summary>
/// Limits of BOLT 1 peer storage (<c>option_provide_storage</c>, <c>peer_storage</c>/<c>peer_storage_retrieval</c>).
/// </summary>
public static class PeerStorageConstants
{
    /// <summary>
    /// The largest <c>blob</c> a <c>peer_storage</c> or <c>peer_storage_retrieval</c> may carry (BOLT 1): it fits one
    /// lightning message with its type and length prefix.
    /// </summary>
    public const int MaxBlobLength = 65531;
}