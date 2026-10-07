namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

public sealed class ImportedTapscriptEntity
{
    internal ImportedTapscriptEntity() { }
    public byte[] Script { get; set; } = [];
    public byte[] InternalKey { get; set; } = [];
    public byte[] Definition { get; set; } = [];
    public uint CreatedHeight { get; set; }
}