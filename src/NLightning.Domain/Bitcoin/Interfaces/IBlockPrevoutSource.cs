namespace NLightning.Domain.Bitcoin.Interfaces;

using Enums;
using ValueObjects;

/// <summary>Loads every input of non-coinbase transactions containing a taproot output. Missing data fails the block.</summary>
public interface IBlockPrevoutSource
{
    SilentPaymentPrevoutSource Source { get; }
    Task ProbeAsync(CancellationToken cancellationToken = default);
    Task ValidateHeightAsync(uint height, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>>> GetPrevoutsAsync(
        BitcoinBlock block, uint height, CancellationToken cancellationToken = default);
    /// <summary>Every non-coinbase input for bounded wallet history recovery, including transactions without taproot outputs.</summary>
    Task<IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>>> GetAllPrevoutsAsync(
        BitcoinBlock block, uint height, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This source cannot prove complete historical wallet input ownership.");
}