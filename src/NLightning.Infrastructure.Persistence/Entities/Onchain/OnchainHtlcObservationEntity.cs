namespace NLightning.Infrastructure.Persistence.Entities.Onchain;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;

/// <summary>A durable first-observation checkpoint; not a historical event payload.</summary>
public class OnchainHtlcObservationEntity
{
    public required ChannelId ChannelId { get; set; }
    public required HtlcDirection Direction { get; set; }
    public required ulong HtlcId { get; set; }
    public required bool Settled { get; set; }
    public required DateTimeOffset ObservedAt { get; set; }
}