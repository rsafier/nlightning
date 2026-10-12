namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

/// <summary>One durable imported-history checkpoint; never part of the spendable wallet.</summary>
public sealed class ImportedWatchIndexEntity
{
    public int Id { get; set; }
    public uint Height { get; set; }
    public byte[] BlockHash { get; set; } = [];
    public string ScriptSet { get; set; } = string.Empty;
    public byte[] History { get; set; } = [];
}