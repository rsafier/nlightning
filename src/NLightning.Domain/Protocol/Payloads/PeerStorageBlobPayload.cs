namespace NLightning.Domain.Protocol.Payloads;

using Interfaces;
using Node.PeerStorage;

/// <summary>
/// The payload shared by <c>peer_storage</c> (type 7) and <c>peer_storage_retrieval</c> (type 9): a <c>u16</c>
/// length and at most <see cref="PeerStorageConstants.MaxBlobLength"/> opaque bytes (BOLT 1 Peer Storage).
/// </summary>
public abstract class PeerStorageBlobPayload : IMessagePayload
{
    /// <summary>
    /// The opaque blob (encrypted by its owner).
    /// </summary>
    public ReadOnlyMemory<byte> Blob { get; }

    /// <exception cref="ArgumentException">
    /// The blob is longer than <see cref="PeerStorageConstants.MaxBlobLength"/>.
    /// </exception>
    protected PeerStorageBlobPayload(ReadOnlyMemory<byte> blob)
    {
        if (blob.Length > PeerStorageConstants.MaxBlobLength)
            throw new ArgumentException(
                $"A peer storage blob is at most {PeerStorageConstants.MaxBlobLength} bytes, got {blob.Length}",
                nameof(blob));

        Blob = blob;
    }
}