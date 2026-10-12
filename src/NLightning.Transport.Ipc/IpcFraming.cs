using System.Buffers;
using System.Buffers.Binary;
using MessagePack;

namespace NLightning.Transport.Ipc;

/// <summary>
/// The one length-prefixed framing shared by the daemon's pipe server and the CLI client (NL-154): a 4-byte
/// big-endian length prefix (architecture-independent) followed by the MessagePack-serialized envelope, at most
/// <see cref="MaxFrameLength"/> bytes.
/// </summary>
/// <remarks>
/// Every frame is parsed with MessagePack's untrusted-data security (object-graph depth, collision-resistant
/// hashing): the server reads the request before the client is authenticated, and the client reads whatever answers
/// its connection.
/// </remarks>
public sealed class IpcFraming : IIpcFraming
{
    /// <summary>
    /// The most bytes one frame may carry.
    /// </summary>
    public const int MaxFrameLength = 10_000_000;

    public async Task<IpcEnvelope> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactAsync(stream, header, ct);
        var len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len is <= 0 or > MaxFrameLength) throw new IOException("Invalid IPC frame length.");

        var buffer = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            await ReadExactAsync(stream, buffer.AsMemory(0, len), ct);
            var options = MessagePackSerializer.DefaultOptions.WithSecurity(MessagePackSecurity.UntrustedData);
            return MessagePackSerializer.Deserialize<IpcEnvelope>(buffer.AsMemory(0, len), options, ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task WriteAsync(Stream stream, IpcEnvelope envelope, CancellationToken ct)
    {
        var payload = MessagePackSerializer.Serialize(envelope, cancellationToken: ct);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
    }
}