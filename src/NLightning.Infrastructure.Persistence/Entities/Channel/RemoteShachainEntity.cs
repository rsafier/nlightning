// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Channel;

using Domain.Channels.ValueObjects;

/// <summary>
/// One bucket of the peer's shachain: a per-commitment secret the peer revealed in <c>revoke_and_ack</c>, stored in
/// the BOLT 3 compact form (at most 49 rows per channel). Needed to punish a revoked commitment and to answer
/// <c>channel_reestablish</c> after a restart (NL-136).
/// </summary>
public class RemoteShachainEntity
{
    /// <summary>
    /// The channel the secret belongs to.
    /// </summary>
    public required ChannelId ChannelId { get; set; }

    /// <summary>
    /// The bucket (0..48): the number of trailing zero bits of <see cref="Index"/>.
    /// </summary>
    public required byte Bucket { get; set; }

    /// <summary>
    /// The BOLT 3 secret index (48 bits, counts down from 2^48 - 1).
    /// </summary>
    public required long Index { get; set; }

    /// <summary>
    /// The 32-byte per-commitment secret.
    /// </summary>
    public required byte[] Secret { get; set; }

    /// <summary>
    /// Default constructor for EF Core.
    /// </summary>
    internal RemoteShachainEntity()
    {
    }
}