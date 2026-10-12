namespace NLightning.Domain.Protocol.Onion.Models;

using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// The result of building an onion: the packet and the per-hop shared secrets the origin keeps to decrypt failures.
/// </summary>
public sealed class ConstructedOnion
{
    /// <summary>
    /// The onion packet to send to the first hop.
    /// </summary>
    public OnionPacket Packet { get; }

    /// <summary>
    /// The shared secret with each hop, first hop first.
    /// </summary>
    public IReadOnlyList<Secret> SharedSecrets { get; }

    public ConstructedOnion(OnionPacket packet, IReadOnlyList<Secret> sharedSecrets)
    {
        ArgumentNullException.ThrowIfNull(sharedSecrets);

        Packet = packet;
        SharedSecrets = sharedSecrets;
    }
}