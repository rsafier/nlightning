namespace NLightning.Domain.Signing.Vls;

using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>Reserved before signer allocation; the 41-byte VLS identity is distinct from a BOLT channel ID.</summary>
public sealed record VlsChannelMapping(uint KeyIndex, ulong DbId, CompactPubKey PeerId,
    ChannelId? ChannelId, byte[] SignerIdentity, string Network, Guid AllocationRequestId,
    byte[] AllocationEnvelope, byte[]? AllocationResponse, byte[]? VlsChannelId,
    long CreatedAtTicks, long UpdatedAtTicks);

/// <summary>Mutations are staged and immutable allocation identities are never reused.</summary>
public interface IVlsChannelMappingDbRepository
{
    Task<VlsChannelMapping?> GetByKeyIndexAsync(uint keyIndex);
    Task<VlsChannelMapping?> GetByChannelIdAsync(ChannelId channelId);
    Task<IReadOnlyList<VlsChannelMapping>> GetAllAsync();
    Task AddAsync(VlsChannelMapping mapping);
    Task CompleteAllocationAsync(uint keyIndex, byte[] response, byte[] vlsChannelId);
    Task BindChannelAsync(uint keyIndex, ChannelId channelId);
}