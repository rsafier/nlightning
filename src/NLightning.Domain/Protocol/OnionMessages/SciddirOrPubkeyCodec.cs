using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.OnionMessages;

using Channels.ValueObjects;
using Crypto.Constants;
using Crypto.ValueObjects;

/// <summary>
/// The BOLT 1 <c>sciddir_or_pubkey</c> wire codec: 9 bytes (a direction byte 0 or 1, then a
/// <c>short_channel_id</c>) or 33 bytes (a compressed point, first byte 2 or 3).
/// </summary>
/// <remarks>
/// The first byte picks the form; any other first byte is refused. Pure and allocation-light; used by
/// <see cref="BlindedPathCodec"/> (<c>first_node_id</c>) and by BOLT 12 (<c>offer_paths</c>, <c>invreq_paths</c>,
/// <c>invoice_paths</c>).
/// </remarks>
public static class SciddirOrPubkeyCodec
{
    /// <summary>
    /// The length of the SCID form: a direction byte and an 8-byte <c>short_channel_id</c>.
    /// </summary>
    public const int ShortChannelIdFormLength = 1 + ShortChannelId.Length;

    /// <summary>
    /// The length of the node id form: a 33-byte compressed point.
    /// </summary>
    public const int NodeIdFormLength = CryptoConstants.CompactPubkeyLen;

    /// <summary>
    /// The encoded length of <paramref name="value"/> (9 or 33).
    /// </summary>
    public static int GetLength(SciddirOrPubkey value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.IsNodeId ? NodeIdFormLength : ShortChannelIdFormLength;
    }

    /// <summary>
    /// Writes <paramref name="value"/> at the start of <paramref name="destination"/>.
    /// </summary>
    /// <returns>The number of bytes written (9 or 33).</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public static int Write(SciddirOrPubkey value, Span<byte> destination)
    {
        var length = GetLength(value);
        if (destination.Length < length)
            throw new ArgumentException($"Destination needs {length} bytes, got {destination.Length}.",
                                        nameof(destination));

        if (value.NodeId is { } nodeId)
        {
            ((ReadOnlySpan<byte>)nodeId).CopyTo(destination);
        }
        else
        {
            destination[0] = value.Direction;
            ((ReadOnlySpan<byte>)value.ShortChannelId!.Value).CopyTo(destination[1..]);
        }

        return length;
    }

    /// <summary>
    /// Encodes <paramref name="value"/>.
    /// </summary>
    public static byte[] Encode(SciddirOrPubkey value)
    {
        var bytes = new byte[GetLength(value)];
        Write(value, bytes);
        return bytes;
    }

    /// <summary>
    /// Reads one <c>sciddir_or_pubkey</c> from the start of <paramref name="data"/> (trailing bytes are left for the
    /// caller).
    /// </summary>
    /// <param name="data">The bytes to read from.</param>
    /// <param name="value">The decoded value.</param>
    /// <param name="bytesRead">How many bytes it took (9 or 33), 0 on failure.</param>
    /// <param name="reason">Why the bytes are refused: empty or truncated data, or a first byte other than 0-3.</param>
    public static bool TryRead(ReadOnlySpan<byte> data, [NotNullWhen(true)] out SciddirOrPubkey? value,
                               out int bytesRead, [NotNullWhen(false)] out string? reason)
    {
        value = null;
        bytesRead = 0;

        if (data.IsEmpty)
        {
            reason = "sciddir_or_pubkey is empty.";
            return false;
        }

        switch (data[0])
        {
            case 0 or 1:
                if (data.Length < ShortChannelIdFormLength)
                {
                    reason = $"sciddir_or_pubkey SCID form needs {ShortChannelIdFormLength} bytes, got {data.Length}.";
                    return false;
                }

                value = SciddirOrPubkey.FromShortChannelId(new ShortChannelId(data[1..ShortChannelIdFormLength]
                                                                                 .ToArray()), data[0]);
                bytesRead = ShortChannelIdFormLength;
                reason = null;
                return true;
            case 2 or 3:
                if (data.Length < NodeIdFormLength)
                {
                    reason = $"sciddir_or_pubkey point needs {NodeIdFormLength} bytes, got {data.Length}.";
                    return false;
                }

                value = SciddirOrPubkey.FromNodeId(new CompactPubKey(data[..NodeIdFormLength].ToArray()));
                bytesRead = NodeIdFormLength;
                reason = null;
                return true;
            default:
                reason = $"sciddir_or_pubkey starts with 0x{data[0]:x2}; only 0, 1, 2 and 3 are defined.";
                return false;
        }
    }

    /// <summary>
    /// Decodes a value that fills <paramref name="data"/> exactly.
    /// </summary>
    /// <exception cref="FormatException">The bytes are not exactly one <c>sciddir_or_pubkey</c>.</exception>
    public static SciddirOrPubkey Decode(ReadOnlySpan<byte> data)
    {
        if (!TryRead(data, out var value, out var bytesRead, out var reason))
            throw new FormatException(reason);
        if (bytesRead != data.Length)
            throw new FormatException(
                $"sciddir_or_pubkey is {bytesRead} bytes, but {data.Length - bytesRead} bytes follow it.");

        return value;
    }
}