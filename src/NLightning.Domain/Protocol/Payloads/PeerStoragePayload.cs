namespace NLightning.Domain.Protocol.Payloads;

/// <summary>
/// The payload of <c>peer_storage</c>: the blob the sender asks us to keep.
/// </summary>
public sealed class PeerStoragePayload(ReadOnlyMemory<byte> blob) : PeerStorageBlobPayload(blob);