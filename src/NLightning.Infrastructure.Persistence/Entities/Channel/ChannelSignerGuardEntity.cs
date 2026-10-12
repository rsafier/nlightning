// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Channel;

using Domain.Channels.ValueObjects;

/// <summary>
/// The local signer's durable safety state of one channel (NL-1345, migration <c>AddChannelSignerGuards</c>, Domain
/// <c>ChannelSignerGuard</c>). Written only by <c>ChannelSignerGuardStore</c>, never lowered; no foreign key to the
/// channel row on purpose: the guard outlives whatever happens to the channel rows (a restore, a reset).
/// </summary>
public class ChannelSignerGuardEntity
{
    public required ChannelId ChannelId { get; set; }

    /// <summary>The highest local commitment number the signer advanced to.</summary>
    public required long LocalCommitmentNumber { get; set; }

    /// <summary>The highest local commitment whose per-commitment secret was released, if any.</summary>
    public long? RevokedCommitmentNumber { get; set; }

    /// <summary>The highest commitment number of the peer's commitment the signer signed, if any.</summary>
    public long? RemoteSignedCommitmentNumber { get; set; }

    /// <summary>The lowest local commitment number signed for broadcast (S1), if any.</summary>
    public long? BroadcastSignedCommitmentNumber { get; set; }

    public required bool DataLossDetected { get; set; }

    internal ChannelSignerGuardEntity() { }
}