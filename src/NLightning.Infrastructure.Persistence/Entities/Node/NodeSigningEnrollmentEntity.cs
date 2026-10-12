namespace NLightning.Infrastructure.Persistence.Entities.Node;

/// <summary>The immutable signing authority assigned to this database before any key-bearing state is created.</summary>
public sealed class NodeSigningEnrollmentEntity
{
    public required int Id { get; set; }
    public required int SchemaVersion { get; set; }
    public required string NodeId { get; set; }
    public required string OwnerId { get; set; }
    public required string SignerId { get; set; }
    public required string Network { get; set; }
    public required byte[] NodePublicKey { get; set; }
    public required long CreatedAtTicks { get; set; }

    internal NodeSigningEnrollmentEntity() { }
}