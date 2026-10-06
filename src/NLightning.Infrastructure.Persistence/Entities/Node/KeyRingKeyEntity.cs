namespace NLightning.Infrastructure.Persistence.Entities.Node;

public sealed class KeyRingKeyEntity
{
    internal KeyRingKeyEntity() { }
    public int Family { get; set; }
    public int Index { get; set; }
    public byte[] PublicKey { get; set; } = [];
    public long CreatedAt { get; set; }
}