namespace NLightning.Application.Channels.Close.Simple;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Protocol.Payloads;

/// <summary>What the closer signs, or why it sends no <c>closing_complete</c>.</summary>
/// <param name="Kinds">The <c>closing_tlvs</c> fields to set, in TLV order (empty when refused).</param>
/// <param name="RequirementId">The BOLT 2 row behind a refusal, or null.</param>
/// <param name="Reason">Why nothing can be proposed, or null.</param>
public sealed record CloserSelection(IReadOnlyList<ClosingSigKind> Kinds, string? RequirementId = null,
                                     string? Reason = null)
{
    /// <summary>True when a <c>closing_complete</c> can be sent with <see cref="Kinds"/>.</summary>
    public bool CanPropose => Kinds.Count > 0;
}

/// <summary>
/// The BOLT 2 <c>option_simple_close</c> requirements that only depend on the proposal's numbers (BOLT2 plan N11-T2):
/// the closer's fee and <c>closing_tlvs</c> selection (B2-SC-C01, C02, C06, C07) and the closee's choice of the
/// signature to check (B2-SC-E06). Pure; we never consider an output "uneconomical" (the MAY rows) and never use an
/// <c>OP_RETURN</c> script of our own.
/// </summary>
public static class SimpleCloseRules
{
    /// <summary>BOLT 3's feerate floor: our proposals never pay less.</summary>
    public const ulong MinFeeratePerKw = 253;

    /// <summary>
    /// The closer's <c>closing_tlvs</c> (BOLT 2 "The sender of closing_complete"): with less than the closee it never
    /// sets <c>closer_output_only</c> and sets only <c>closee_output_only</c> when its own output is dust; otherwise it
    /// never sets <c>closee_output_only</c>, sets only <c>closer_output_only</c> when the closee's output is dust and
    /// both <c>closer_output_only</c> and <c>closer_and_closee_outputs</c> otherwise. Refused: a fee above the closer's
    /// balance (C01), both outputs dust (C02), and the cases where every allowed variant would carry a dust output.
    /// </summary>
    public static CloserSelection SelectCloserKinds(SimpleClosingTerms terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (!terms.CloserCanPay)
            return Refuse("B2-SC-C01",
                          $"the fee of {terms.FeeSat} sat is above the closer's balance of {terms.CloserBalanceSat} sat");

        var closerDust = terms.CloserIsDust;
        var closeeDust = terms.CloseeIsDust;
        if (closerDust && closeeDust)
            return Refuse("B2-SC-C02", "both outputs would be dust");

        if (terms.CloserIsLesser)
        {
            if (closerDust)
                return new CloserSelection([ClosingSigKind.CloseeOutputOnly]);

            // closer_output_only is not allowed to the lesser side, so a dust closee output can't be dropped
            return closeeDust
                       ? Refuse("B2-SC-C06", "the closee's output is dust and the lesser closer can't drop it")
                       : new CloserSelection([ClosingSigKind.CloserAndCloseeOutputs]);
        }

        // closee_output_only is not allowed to the side that isn't the lesser, so its own dust output stays
        if (closerDust)
            return Refuse("B2-SC-C07", "the closer's output is dust and the closer is not the lesser side");

        return closeeDust
                   ? new CloserSelection([ClosingSigKind.CloserOutputOnly])
                   : new CloserSelection([ClosingSigKind.CloserOutputOnly, ClosingSigKind.CloserAndCloseeOutputs]);
    }

    /// <summary>
    /// The signature the closee checks (BOLT 2 "Select a signature for validation"): <c>closer_output_only</c> when its
    /// own output is dust, else <c>closer_and_closee_outputs</c> when present, else <c>closee_output_only</c>.
    /// </summary>
    /// <param name="terms">The proposal (the closee's output is <see cref="SimpleClosingTerms.CloseeAmountSat"/>).
    /// </param>
    /// <param name="received">The fields the closer set.</param>
    public static ClosingSigKind SelectCloseeKind(SimpleClosingTerms terms, ClosingSignatures received)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(received);
        if (terms.CloseeIsDust)
            return ClosingSigKind.CloserOutputOnly;

        return received.CloserAndCloseeOutputs is not null
                   ? ClosingSigKind.CloserAndCloseeOutputs
                   : ClosingSigKind.CloseeOutputOnly;
    }

    /// <summary>
    /// The fee we propose as closer: <paramref name="feeratePerKw"/> (at least <see cref="MinFeeratePerKw"/>) times
    /// the weight with both outputs, capped at our balance and, when we are not the lesser side, so that our own output
    /// stays at or above its dust threshold. Null when we can't pay the fee at the floor feerate (the peer's own
    /// <c>closing_complete</c> closes the channel then).
    /// </summary>
    public static ulong? ChooseFee(ulong closerBalanceMsat, ulong closeeBalanceMsat, BitcoinScript closerScript,
                                   BitcoinScript closeeScript, ulong feeratePerKw)
    {
        var weight = ClosingFeeCalculator.EstimateWeight(closerScript.Length, closeeScript.Length);
        var floorFee = ClosingFeeCalculator.FeeSat(MinFeeratePerKw, weight);
        var fee = ClosingFeeCalculator.FeeSat(Math.Max(feeratePerKw, MinFeeratePerKw), weight);

        var balanceSat = closerBalanceMsat / 1000;
        fee = Math.Min(fee, balanceSat);
        if (closerBalanceMsat >= closeeBalanceMsat && !SimpleClosingTerms.IsOpReturn(closerScript))
        {
            var dust = ShutdownScriptValidator.GetDustThresholdSat((byte[])closerScript);
            if (balanceSat >= dust)
                fee = Math.Min(fee, balanceSat - dust);
        }

        return fee >= floorFee ? fee : null;
    }

    private static CloserSelection Refuse(string requirementId, string reason) => new([], requirementId, reason);
}