namespace NLightning.Domain.Bitcoin.Interfaces;

/// <summary>
/// The time of a block of the active chain (NL-623): the accounting channel report dates a channel opened before the
/// accounting feed began by its funding block. Never on a hot path.
/// </summary>
public interface IBlockTimeSource
{
    /// <summary>
    /// The timestamp in the header of the active chain's block at <paramref name="height"/>, or null when it is not
    /// known (above the tip, or the chain cannot be asked). May throw when the chain backend fails.
    /// </summary>
    Task<DateTimeOffset?> GetBlockTimeAsync(uint height, CancellationToken cancellationToken = default);
}