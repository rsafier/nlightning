namespace NLightning.Infrastructure.Bitcoin.Builders.Interfaces;

using Domain.Onchain.Models;

/// <summary>
/// Builds penalty (justice) transactions against a revoked commitment and its second-level transactions (BOLT 5 plan
/// O5-T1): batched (B5-REV-08 MAY, decision D10) or one per output (the <c>security_delay</c> split, O5-T3). Sign them
/// with <see cref="ISweepTransactionBuilder.Sign"/>.
/// </summary>
public interface IPenaltyTransactionBuilder
{
    /// <summary>
    /// One penalty spending every revoked output given, plus our <c>to_remote</c> of the same commitment if it is
    /// among the inputs (BOLT 5 allows it: +272 weight).
    /// </summary>
    /// <exception cref="ArgumentException">An input is neither a penalty nor a <c>to_remote</c> sweep, or the value
    /// does not cover the fee.</exception>
    UnsignedSweepTransaction BuildBatched(IReadOnlyList<SweepInput> inputs, byte[] destinationScript,
                                          uint feeratePerKw);

    /// <summary>A penalty spending one revoked output.</summary>
    UnsignedSweepTransaction BuildSingle(SweepInput input, byte[] destinationScript, uint feeratePerKw);

    /// <summary>One penalty per input (the split of B5-REV-08), in input order.</summary>
    IReadOnlyList<UnsignedSweepTransaction> BuildSplit(IReadOnlyList<SweepInput> inputs, byte[] destinationScript,
                                                       uint feeratePerKw);
}