using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Onion.Factories;

using Channels.ValueObjects;
using Protocol.Constants;
using Protocol.Messages;
using Protocol.Payloads;

/// <summary>
/// Encodes and reads the <c>channel_update</c> carried in the <c>u16 len || channel_update</c> field of BOLT 4 UPDATE
/// failures.
/// </summary>
/// <remarks>
/// <para>
/// BOLT 4 only says <c>len*byte channel_update</c>. LND, CLN, Eclair and LDK all write the full message, i.e. the
/// <c>u16 type</c> (258) followed by the <c>channel_update</c> payload, and accept it with or without that type prefix
/// when reading. <see cref="Encode"/> therefore writes the type prefix, and <see cref="TryGetPayload"/> strips it when
/// present.
/// </para>
/// <para>
/// The field is no longer mandatory: nodes that do not wish to send an update set <c>len</c> to zero, and the origin
/// MUST accept that. The origin MAY use an update to retry the same payment, but MUST NOT apply it to its graph or
/// relay it as gossip.
/// </para>
/// </remarks>
public static class FailureChannelUpdateFactory
{
    /// <summary>
    /// The length of the <c>u16 type</c> prefix.
    /// </summary>
    public const int TypePrefixLength = sizeof(ushort);

    /// <summary>
    /// The minimum length of a <c>channel_update</c> payload (without the type): signature(64), chain_hash(32),
    /// short_channel_id(8), timestamp(4), message_flags(1), channel_flags(1), cltv_expiry_delta(2),
    /// htlc_minimum_msat(8), fee_base_msat(4), fee_proportional_millionths(4), htlc_maximum_msat(8).
    /// </summary>
    public const int MinPayloadLength = ChannelUpdatePayload.MinLength;

    private const int ShortChannelIdOffset = 64 + 32;
    private const int TimestampOffset = ShortChannelIdOffset + ShortChannelId.Length;

    /// <summary>
    /// Builds the bytes of the <c>channel_update</c> field from a serialized <c>channel_update</c> payload (without the
    /// message type): <c>u16 258 || payload</c>. An empty payload returns an empty array (<c>len = 0</c>).
    /// </summary>
    /// <remarks>
    /// BOLT 4: the update's <c>short_channel_id</c> MUST be the one used by the incoming onion (so an alias when the
    /// sender used one); building the update is the caller's job.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// If a non-empty payload is shorter than <see cref="MinPayloadLength"/>, or the field would not fit a u16 length.
    /// </exception>
    public static byte[] Encode(ReadOnlySpan<byte> channelUpdatePayload)
    {
        if (channelUpdatePayload.IsEmpty)
            return [];

        if (channelUpdatePayload.Length < MinPayloadLength)
            throw new ArgumentException($"A channel_update payload is at least {MinPayloadLength} bytes.",
                                        nameof(channelUpdatePayload));

        if (channelUpdatePayload.Length > ushort.MaxValue - TypePrefixLength)
            throw new ArgumentException("The channel_update does not fit a u16 length.",
                                        nameof(channelUpdatePayload));

        var field = new byte[TypePrefixLength + channelUpdatePayload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(field, (ushort)MessageTypes.ChannelUpdate);
        channelUpdatePayload.CopyTo(field.AsSpan(TypePrefixLength));
        return field;
    }

    /// <summary>
    /// Builds the bytes of the <c>channel_update</c> field from a typed (normally signed) <c>channel_update</c>:
    /// <c>u16 258 || payload</c>, byte for byte what the wire serializer writes for the message.
    /// </summary>
    /// <remarks>
    /// BOLT 4: the update's <c>short_channel_id</c> MUST be the one used by the incoming onion (so an alias when the
    /// sender used one). The signature is not checked here; sign the update with
    /// <c>ILightningSigner.SignNodeMessage(</c><see cref="ChannelUpdatePayload.GetSignatureHash"/><c>)</c> first.
    /// </remarks>
    /// <exception cref="ArgumentException">If the update would not fit a u16 length.</exception>
    public static byte[] Encode(ChannelUpdateMessage channelUpdate)
    {
        ArgumentNullException.ThrowIfNull(channelUpdate);
        return Encode(channelUpdate.Payload.GetBytes());
    }

    /// <summary>
    /// Parses the <c>channel_update</c> field of an UPDATE failure into a typed message (with or without the type
    /// prefix). The signature is not checked; verify it with the origin's node id before using the update.
    /// </summary>
    /// <remarks>
    /// Known limit (inherited from <see cref="TryGetPayload"/>): a field that starts with <c>0x0102</c> and is long
    /// enough is always read as <c>u16 258 || payload</c>. A bare payload (no type prefix) whose signature <c>r</c>
    /// happens to start with <c>0x0102</c> and that carries at least two bytes of unknown trailing fields is therefore
    /// misread (shifted by two bytes); roughly 1 in 65536 bare-format updates. LND, CLN, Eclair and LDK all send the
    /// prefix, so this does not occur in practice, and the shifted reading fails signature verification, which callers
    /// must do anyway.
    /// </remarks>
    /// <returns><c>false</c> when the field is empty (<c>len = 0</c>) or too short to be a <c>channel_update</c>.</returns>
    public static bool TryGetChannelUpdate(ReadOnlyMemory<byte> field,
                                           [NotNullWhen(true)] out ChannelUpdateMessage? channelUpdate)
    {
        channelUpdate = null;
        if (!TryGetPayload(field, out var payload)
         || !ChannelUpdatePayload.TryParse(payload.Span, out var channelUpdatePayload))
            return false;

        channelUpdate = new ChannelUpdateMessage(channelUpdatePayload);
        return true;
    }

    /// <summary>
    /// Gets the <c>channel_update</c> payload (without the message type) from the <c>channel_update</c> field of an
    /// UPDATE failure. A leading type 258 is stripped; a field without it is taken as the bare payload.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the field is empty (<c>len = 0</c>) or too short to be a <c>channel_update</c>.
    /// </returns>
    public static bool TryGetPayload(ReadOnlyMemory<byte> field, out ReadOnlyMemory<byte> payload)
    {
        payload = ReadOnlyMemory<byte>.Empty;

        if (field.Length >= TypePrefixLength + MinPayloadLength
         && BinaryPrimitives.ReadUInt16BigEndian(field.Span) == (ushort)MessageTypes.ChannelUpdate)
        {
            payload = field[TypePrefixLength..];
            return true;
        }

        if (field.Length < MinPayloadLength)
            return false;

        payload = field;
        return true;
    }

    /// <summary>
    /// Reads the <c>short_channel_id</c> and <c>timestamp</c> of a <c>channel_update</c> payload (without the type),
    /// e.g. to check which channel an update in a failure refers to and whether it is newer than the one used to route.
    /// The signature is not checked.
    /// </summary>
    /// <returns><c>false</c> if the payload is shorter than <see cref="MinPayloadLength"/>.</returns>
    public static bool TryReadHeader(ReadOnlySpan<byte> payload, out ShortChannelId shortChannelId,
                                     out uint timestamp)
    {
        shortChannelId = default;
        timestamp = 0;

        if (payload.Length < MinPayloadLength)
            return false;

        shortChannelId = new ShortChannelId(payload.Slice(ShortChannelIdOffset, ShortChannelId.Length).ToArray());
        timestamp = BinaryPrimitives.ReadUInt32BigEndian(payload[TimestampOffset..]);
        return true;
    }
}