// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Channel;

using Domain.Channels.ValueObjects;

/// <summary>
/// A scid alias we generated for one of our channels and sent to the peer in channel_ready (BOLT 2 option_scid_alias).
/// </summary>
/// <remarks>
/// The alias is the primary key, so an alias can never point to two channels, not even across restarts (NL-103).
/// </remarks>
public class ChannelLocalAliasEntity
{
    /// <summary>
    /// The scid alias.
    /// </summary>
    public required ShortChannelId Alias { get; set; }

    /// <summary>
    /// The channel the alias points to.
    /// </summary>
    public required ChannelId ChannelId { get; set; }

    /// <summary>
    /// Default constructor for EF Core.
    /// </summary>
    internal ChannelLocalAliasEntity()
    {
    }
}