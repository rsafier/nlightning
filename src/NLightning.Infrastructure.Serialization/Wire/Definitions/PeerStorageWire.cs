namespace NLightning.Infrastructure.Serialization.Wire.Definitions;

using System.Runtime.Serialization;

using Domain.Node.PeerStorage;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// The wire definitions of BOLT 1 Peer Storage: <c>peer_storage</c> (7) and <c>peer_storage_retrieval</c> (9), each a
/// <c>u16</c> length and at most <see cref="PeerStorageConstants.MaxBlobLength"/> opaque bytes. Neither message
/// defines TLVs, so a trailing extension is validated with an empty known set (BOLT 1: unknown even fails, odd
/// ignored).
/// </summary>
internal static class PeerStorageWire
{
    public static readonly MessageWire<PeerStorageMessage> Def =
        new(MessageTypes.PeerStorage, Encode, Decode, strictEmptyExtension: true, keepRawExtension: false);

    private static void Encode(ref WireWriter writer, PeerStorageMessage message)
    {
        WriteBlob(ref writer, message.Payload.Blob);
    }

    private static WireConstruct<PeerStorageMessage> Decode(ref WireReader reader)
    {
        var blob = ReadBlob(ref reader);

        return tlvs => new PeerStorageMessage(new PeerStoragePayload(blob));
    }

    internal static void WriteBlob(ref WireWriter writer, ReadOnlyMemory<byte> blob)
    {
        writer.U16((ushort)blob.Length);
        writer.Bytes(blob.Span);
    }

    /// <exception cref="SerializationException">The declared blob is longer than BOLT 1 allows.</exception>
    internal static byte[] ReadBlob(ref WireReader reader)
    {
        var length = reader.U16();
        if (length > PeerStorageConstants.MaxBlobLength)
            throw new SerializationException(
                $"Peer storage blob of {length} bytes is longer than {PeerStorageConstants.MaxBlobLength}");

        return reader.BytesArray(length);
    }
}

internal static class PeerStorageRetrievalWire
{
    public static readonly MessageWire<PeerStorageRetrievalMessage> Def =
        new(MessageTypes.PeerStorageRetrieval, Encode, Decode, strictEmptyExtension: true, keepRawExtension: false);

    private static void Encode(ref WireWriter writer, PeerStorageRetrievalMessage message)
    {
        PeerStorageWire.WriteBlob(ref writer, message.Payload.Blob);
    }

    private static WireConstruct<PeerStorageRetrievalMessage> Decode(ref WireReader reader)
    {
        var blob = PeerStorageWire.ReadBlob(ref reader);

        return tlvs => new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(blob));
    }
}