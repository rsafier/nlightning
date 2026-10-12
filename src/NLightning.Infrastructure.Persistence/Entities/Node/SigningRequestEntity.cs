namespace NLightning.Infrastructure.Persistence.Entities.Node;

/// <summary>An exact signing envelope and result retained after its workflow is consumed.</summary>
public sealed class SigningRequestEntity
{
    public required Guid RequestId { get; set; }
    public required Guid WorkflowId { get; set; }
    public required int Ordinal { get; set; }
    public required uint Operation { get; set; }
    public required byte[] Envelope { get; set; }
    public required byte[] ArgumentFingerprint { get; set; }
    public required int State { get; set; }
    public byte[]? Response { get; set; }
    public required long CreatedAtTicks { get; set; }
    public required long UpdatedAtTicks { get; set; }

    internal SigningRequestEntity() { }
}