namespace NLightning.Domain.Protocol.Onion.Tlv;

using Constants;
using Protocol.Tlv;
using ValueObjects;

/// <summary>
/// Onion hop payload TLV 20 (<c>trampoline_onion_packet</c>): <c>byte version || point public_key ||
/// ...*byte hop_payloads || 32*byte hmac</c>, the trampoline onion carried by the last hop's payload of the outer onion
/// (BOLTs PR 836).
/// </summary>
/// <remarks>
/// The packet has a variable size: its <c>hop_payloads</c> are the TLV length minus
/// <see cref="OnionConstants.PacketOverheadLength"/> (66) bytes, and must not be empty. Like <see cref="OnionPacket"/>,
/// only the length is checked here; the version byte and the public key are the peeler's to refuse.
/// </remarks>
public class TrampolineOnionPacketTlv : BaseTlv
{
    /// <summary>
    /// The shortest valid packet: the 66-byte overhead and one byte of <c>hop_payloads</c>.
    /// </summary>
    public const int MinLength = OnionConstants.PacketOverheadLength + 1;

    /// <summary>
    /// The raw packet bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Packet { get; }

    /// <summary>
    /// The length of the packet's <c>hop_payloads</c>.
    /// </summary>
    public int HopPayloadsLength => Packet.Length - OnionConstants.PacketOverheadLength;

    /// <summary>
    /// Creates the TLV from the raw packet bytes (copied).
    /// </summary>
    /// <exception cref="ArgumentException">The packet is shorter than <see cref="MinLength"/> bytes.</exception>
    public TrampolineOnionPacketTlv(ReadOnlySpan<byte> packet) : base(OnionPayloadTlvTypes.TrampolineOnionPacket)
    {
        if (packet.Length < MinLength)
            throw new ArgumentException(
                $"A trampoline_onion_packet must be at least {MinLength} bytes, got {packet.Length}.", nameof(packet));

        var value = packet.ToArray();

        Value = value;
        Length = value.Length;
        Packet = value;
    }

    /// <summary>
    /// Creates the TLV from a constructed trampoline onion.
    /// </summary>
    /// <exception cref="InvalidOperationException">The packet is <c>default</c>.</exception>
    public TrampolineOnionPacketTlv(OnionPacket packet) : this(packet.ToBytes())
    { }

    /// <summary>
    /// The packet as an <see cref="OnionPacket"/> whose <c>hop_payloads</c> are <see cref="HopPayloadsLength"/> bytes
    /// long, ready to peel.
    /// </summary>
    public OnionPacket ToOnionPacket() => new(Packet.Span, HopPayloadsLength);
}