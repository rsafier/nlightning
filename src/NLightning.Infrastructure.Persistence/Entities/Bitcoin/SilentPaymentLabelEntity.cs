namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

public class SilentPaymentLabelEntity
{
    public uint M { get; set; }
    public required string Name { get; set; }
    public uint CreatedAtHeight { get; set; }
    internal SilentPaymentLabelEntity() { }
}