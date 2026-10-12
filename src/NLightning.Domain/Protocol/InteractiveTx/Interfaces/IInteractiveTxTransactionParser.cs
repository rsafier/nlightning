namespace NLightning.Domain.Protocol.InteractiveTx.Interfaces;

using Models;

/// <summary>
/// Reads a serialized transaction of an interactive-tx negotiation into Domain values (splicing plan IT2-T3), so
/// Application checks the bytes it signs and reads their witnesses without NBitcoin. Implemented by
/// <c>InteractiveTxTransactionParser</c> in Infrastructure.Bitcoin over the strict <c>InteractiveTxTransactionReader</c>.
/// </summary>
public interface IInteractiveTxTransactionParser
{
    /// <summary>
    /// Parses <paramref name="transaction"/> (with or without witnesses). Null, never an exception, when the bytes are
    /// not exactly one transaction with at least one input.
    /// </summary>
    ParsedInteractiveTx? TryParse(ReadOnlyMemory<byte> transaction);
}