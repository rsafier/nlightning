namespace NLightning.Domain.Protocol.Payloads;

using Crypto.ValueObjects;
using Interfaces;
using Onion.Constants;

/// <summary>
/// The payload of <c>onion_message</c> (type 513, BOLT 4 "Onion Messages"):
/// <c>point path_key || u16 len || len*byte onion_message_packet</c>.
/// </summary>
/// <remarks>
/// The packet is kept as raw bytes: <c>version || public_key || onionmsg_payloads || hmac</c>, whose payloads length is
/// <c>len - 66</c> (a writer SHOULD use 1366 or 32834, a reader accepts any length). Peel it with
/// <c>ISphinxService</c> and <c>OnionPacketKind.OnionMessage</c>.
/// </remarks>
public sealed class OnionMessagePayload : IMessagePayload
{
    /// <summary>
    /// The route-blinding <c>path_key</c> the receiver tweaks its node key with (BOLT 4 "Route Blinding").
    /// </summary>
    public CompactPubKey PathKey { get; }

    /// <summary>
    /// The raw <c>onion_message_packet</c>, at least <see cref="OnionConstants.PacketOverheadLength"/> bytes.
    /// </summary>
    public ReadOnlyMemory<byte> OnionMessagePacket { get; }

    /// <exception cref="ArgumentException">
    /// The packet is shorter than <see cref="OnionConstants.PacketOverheadLength"/> bytes or longer than a u16 length
    /// can carry.
    /// </exception>
    public OnionMessagePayload(CompactPubKey pathKey, ReadOnlyMemory<byte> onionMessagePacket)
    {
        if (onionMessagePacket.Length < OnionConstants.PacketOverheadLength
         || onionMessagePacket.Length > ushort.MaxValue)
            throw new ArgumentException(
                $"An onion_message_packet is {OnionConstants.PacketOverheadLength} to {ushort.MaxValue} bytes, got "
              + $"{onionMessagePacket.Length}", nameof(onionMessagePacket));

        PathKey = pathKey;
        OnionMessagePacket = onionMessagePacket;
    }
}