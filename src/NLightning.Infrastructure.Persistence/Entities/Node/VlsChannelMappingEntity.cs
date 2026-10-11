namespace NLightning.Infrastructure.Persistence.Entities.Node;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

public sealed class VlsChannelMappingEntity
{
    public required uint KeyIndex { get; set; }
    public required ulong DbId { get; set; }
    public required CompactPubKey PeerId { get; set; }
    public ChannelId? ChannelId { get; set; }
    public required byte[] SignerIdentity { get; set; }
    public required string Network { get; set; }
    public required Guid AllocationRequestId { get; set; }
    public required byte[] AllocationEnvelope { get; set; }
    public byte[]? AllocationResponse { get; set; }
    public byte[]? VlsChannelId { get; set; }
    public required long CreatedAtTicks { get; set; }
    public required long UpdatedAtTicks { get; set; }
    internal VlsChannelMappingEntity() { }
}