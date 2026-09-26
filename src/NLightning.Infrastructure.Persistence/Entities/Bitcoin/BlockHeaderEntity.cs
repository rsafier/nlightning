namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

using Domain.Crypto.ValueObjects;

/// <summary>
/// A recently processed block of the active chain (the reorg-detection ring, BOLT 5 plan O0-T3). Keyed by height.
/// </summary>
public class BlockHeaderEntity
{
    public required uint Height { get; set; }
    public required Hash BlockHash { get; set; }
    public required Hash PreviousBlockHash { get; set; }

    // Default constructor for EF Core
    internal BlockHeaderEntity() { }
}