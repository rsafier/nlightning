namespace NLightning.Domain.Protocol.Payloads;

/// <summary>
/// The payload of <c>peer_storage_retrieval</c>: the last blob the sender kept for us.
/// </summary>
public sealed class PeerStorageRetrievalPayload(ReadOnlyMemory<byte> blob) : PeerStorageBlobPayload(blob);