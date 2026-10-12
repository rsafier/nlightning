// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Channel;

/// <summary>
/// The database's channel key index high-water mark (NL-1388, migration <c>AddSharedDatabaseHardening</c>): one row
/// (<see cref="Id"/> 1) with the highest channel key index any process over this database handed out. Written only by
/// <c>ChannelKeyIndexDbRepository</c>, never lowered.
/// </summary>
public class ChannelKeyIndexHighWaterEntity
{
    /// <summary>The row's key; always <c>1</c>.</summary>
    public required int Id { get; set; }

    /// <summary>The highest channel key index handed out (a <c>uint</c>).</summary>
    public required long HighWater { get; set; }

    internal ChannelKeyIndexHighWaterEntity() { }
}