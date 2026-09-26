// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Channel;

using Domain.Channels.ValueObjects;

/// <summary>
/// One entry of the revocation log (BOLT 5 plan O1-T1, D2): the content of a peer commitment the peer revoked, in the
/// <see cref="CommitmentEntity"/> format, written in the same save as the <c>revoke_and_ack</c> and only when it had
/// at least one HTLC. Keyed by (channel, commitment number).
/// </summary>
public class RevokedCommitmentEntity
{
    public required ChannelId ChannelId { get; set; }

    /// <summary>The peer's commitment number.</summary>
    public required ulong Number { get; set; }

    public required uint FeeratePerKw { get; set; }

    /// <summary>Our balance in the commitment, before fees, in millisatoshi.</summary>
    public required ulong LocalMsat { get; set; }

    /// <summary>The peer's balance in the commitment, before fees, in millisatoshi.</summary>
    public required ulong RemoteMsat { get; set; }

    /// <summary>The HTLCs, 53 bytes each, as <see cref="CommitmentEntity.Htlcs"/>.</summary>
    public required byte[] Htlcs { get; set; }

    // Default constructor for EF Core
    internal RevokedCommitmentEntity() { }
}