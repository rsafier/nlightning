namespace NLightning.Domain.Protocol.Onion.Models;

using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// A built trampoline onion: the packet to put in the outer onion's <c>trampoline_onion_packet</c> TLV (20) and the
/// shared secret with each trampoline hop, which the origin keeps to decrypt failures from the trampoline layer.
/// </summary>
public sealed class TrampolineOnion
{
    /// <summary>
    /// The packet; its serialized bytes (<see cref="OnionPacket.ToBytes"/>) are the TLV 20 value.
    /// </summary>
    public OnionPacket Packet { get; }

    /// <summary>
    /// The shared secret with each trampoline hop, first trampoline hop first.
    /// </summary>
    public IReadOnlyList<Secret> SharedSecrets { get; }

    /// <summary>
    /// The packet's <c>hop_payloads</c> length.
    /// </summary>
    public int HopPayloadsLength => Packet.HopPayloadsLength;

    public TrampolineOnion(OnionPacket packet, IReadOnlyList<Secret> sharedSecrets)
    {
        ArgumentNullException.ThrowIfNull(sharedSecrets);

        Packet = packet;
        SharedSecrets = sharedSecrets;
    }

    /// <summary>
    /// The value of the outer onion's <c>trampoline_onion_packet</c> TLV:
    /// <c>version || public_key || hop_payloads || hmac</c>.
    /// </summary>
    public byte[] ToTlvValue() => Packet.ToBytes();
}