// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Channel;

using Domain.Channels.ValueObjects;

/// <summary>
/// One commitment of the state machine: our current commitment, the peer's current commitment, or the peer's
/// commitment we signed and whose <c>revoke_and_ack</c> we are waiting for (<see cref="Slot"/>).
/// </summary>
/// <remarks>
/// The content is the engine's <c>CommitmentSpec</c> (before BOLT 3 fees and trimming); the transaction itself is
/// rebuilt from it. Balances are from our perspective in both slots.
/// </remarks>
public class CommitmentEntity
{
    /// <summary>
    /// <see cref="Slot"/> of our current commitment (<c>LocalCommit</c>).
    /// </summary>
    public const byte LocalCurrentSlot = 0;

    /// <summary>
    /// <see cref="Slot"/> of the peer's current commitment (<c>RemoteCommit</c>).
    /// </summary>
    public const byte RemoteCurrentSlot = 1;

    /// <summary>
    /// <see cref="Slot"/> of the peer's commitment we signed but that is not revoked yet (<c>RemoteNextCommit</c>).
    /// </summary>
    public const byte RemoteNextSlot = 2;

    /// <summary>
    /// The channel the commitment belongs to.
    /// </summary>
    public required ChannelId ChannelId { get; set; }

    /// <summary>
    /// Which commitment this row holds (<see cref="LocalCurrentSlot"/>, <see cref="RemoteCurrentSlot"/> or
    /// <see cref="RemoteNextSlot"/>).
    /// </summary>
    public required byte Slot { get; set; }

    /// <summary>
    /// The commitment number.
    /// </summary>
    public required ulong Number { get; set; }

    /// <summary>
    /// The feerate of the commitment.
    /// </summary>
    public required uint FeeratePerKw { get; set; }

    /// <summary>
    /// Our balance in the commitment, before fees, in millisatoshi.
    /// </summary>
    public required ulong LocalMsat { get; set; }

    /// <summary>
    /// The peer's balance in the commitment, before fees, in millisatoshi.
    /// </summary>
    public required ulong RemoteMsat { get; set; }

    /// <summary>
    /// The HTLCs of the commitment: 53 bytes each (direction, id, amount msat, payment hash, cltv expiry; big-endian),
    /// ordered by (direction, id).
    /// </summary>
    public required byte[] Htlcs { get; set; }

    /// <summary>
    /// The peer's per-commitment point of a remote commitment (null for our own commitment).
    /// </summary>
    public byte[]? PerCommitmentPoint { get; set; }

    /// <summary>
    /// The commitment signature: the peer's for our commitment, ours for an unacked remote commitment (null when not
    /// stored).
    /// </summary>
    public byte[]? Signature { get; set; }

    /// <summary>
    /// The HTLC signatures in commitment output order, each prefixed by its length byte (null when
    /// <see cref="Signature"/> is null).
    /// </summary>
    public byte[]? HtlcSignatures { get; set; }

    /// <summary>
    /// Default constructor for EF Core.
    /// </summary>
    internal CommitmentEntity()
    {
    }
}