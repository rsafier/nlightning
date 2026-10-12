namespace NLightning.Infrastructure.Bitcoin.Builders;

using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Interfaces;

/// <summary>
/// Penalty (justice) transactions (BOLT 5 §Revoked Transaction Close Handling, plan O5-T1): the revoked commitment's
/// <c>to_local</c> and the peer's second-level outputs with <c>&lt;revocation_sig&gt; 1</c>, its HTLC outputs with
/// <c>&lt;revocation_sig&gt; &lt;revocationpubkey&gt;</c>, optionally our <c>to_remote</c> in the same transaction.
/// No timelock applies to a revocation spend, so every input is BIP 125 replaceable and <c>nLockTime</c> is 0.
/// </summary>
public class PenaltyTransactionBuilder : IPenaltyTransactionBuilder
{
    private readonly ISweepTransactionBuilder _sweepTransactionBuilder;

    public PenaltyTransactionBuilder(ISweepTransactionBuilder sweepTransactionBuilder)
    {
        _sweepTransactionBuilder = sweepTransactionBuilder;
    }

    /// <inheritdoc />
    public UnsignedSweepTransaction BuildBatched(IReadOnlyList<SweepInput> inputs, byte[] destinationScript,
                                                 uint feeratePerKw)
    {
        ValidatePenaltyInputs(inputs);
        return _sweepTransactionBuilder.Build(inputs, destinationScript, feeratePerKw);
    }

    /// <inheritdoc />
    public UnsignedSweepTransaction BuildSingle(SweepInput input, byte[] destinationScript, uint feeratePerKw)
    {
        ArgumentNullException.ThrowIfNull(input);
        return BuildBatched([input], destinationScript, feeratePerKw);
    }

    /// <inheritdoc />
    public IReadOnlyList<UnsignedSweepTransaction> BuildSplit(IReadOnlyList<SweepInput> inputs,
                                                              byte[] destinationScript, uint feeratePerKw)
    {
        ValidatePenaltyInputs(inputs);
        return inputs.Select(i => _sweepTransactionBuilder.Build([i], destinationScript, feeratePerKw)).ToList();
    }

    private static void ValidatePenaltyInputs(IReadOnlyList<SweepInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
            throw new ArgumentException("A penalty needs at least one input", nameof(inputs));

        if (!inputs.Any(i => i.SpendKind.IsPenalty()))
            throw new ArgumentException("A penalty spends at least one revoked output", nameof(inputs));

        var other = inputs.FirstOrDefault(i => !i.SpendKind.IsPenalty() && i.SpendKind != SweepSpendKind.PaymentToRemote);
        if (other is not null)
            throw new ArgumentException($"A {other.SpendKind} input does not belong in a penalty", nameof(inputs));
    }
}