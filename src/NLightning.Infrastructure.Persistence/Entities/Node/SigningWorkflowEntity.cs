namespace NLightning.Infrastructure.Persistence.Entities.Node;

using Domain.Channels.ValueObjects;

/// <summary>Immutable prerequisites and lifecycle of a recoverable remote signing transition.</summary>
public sealed class SigningWorkflowEntity
{
    public required Guid WorkflowId { get; set; }
    public required ChannelId ChannelId { get; set; }
    public ChannelId? ActiveChannelId { get; set; }
    public required int Kind { get; set; }
    public required ulong ExpectedLocalCommitmentNumber { get; set; }
    public required ulong ExpectedRemoteCommitmentNumber { get; set; }
    public required byte[] SnapshotFingerprint { get; set; }
    public required byte[] SignerIdentity { get; set; }
    public required string Network { get; set; }
    public required int SchemaVersion { get; set; }
    public required int State { get; set; }
    public required long CreatedAtTicks { get; set; }
    public required long UpdatedAtTicks { get; set; }

    internal SigningWorkflowEntity() { }
}