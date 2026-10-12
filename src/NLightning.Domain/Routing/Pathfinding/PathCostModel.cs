namespace NLightning.Domain.Routing.Pathfinding;

/// <summary>
/// The edge cost of the pathfinder (plan BOLT7 §3.10, D6), in msat-equivalent units:
/// <c>fee + amount × cltv_delta × RiskFactorPpmPerBlock / 1e6 + ProbabilityPenalty</c>, with
/// <c>ProbabilityPenalty = −ln(P) × PenaltyBaseMsat + amount × PenaltyPpm / 1e6 × (1 − P)</c>.
/// </summary>
public sealed record PathCostModel
{
    /// <summary>The default model.</summary>
    public static PathCostModel Default { get; } = new();

    /// <summary>The time value of locked funds, in ppm of the amount per block of CLTV delta. Default 15.</summary>
    public double RiskFactorPpmPerBlock { get; init; } = 15;

    /// <summary>The fixed part of the probability penalty. Default 1,000 msat.</summary>
    public double PenaltyBaseMsat { get; init; } = 1_000;

    /// <summary>The amount-proportional part of the probability penalty, in ppm. Default 500.</summary>
    public double PenaltyPpm { get; init; } = 500;

    /// <summary>The success probability of an edge with no liquidity knowledge. Default 0.6.</summary>
    public double AprioriProbability { get; init; } = 0.6;

    /// <summary>Edges less likely than this are not used. Default 0.0001.</summary>
    public double MinProbability { get; init; } = 0.0001;

    /// <summary>
    /// Multiplies the probability of an edge of a channel whose funding output could not be verified (plan §3.4
    /// <c>SkipUnavailable</c>). Default 0.5.
    /// </summary>
    public double UnverifiedProbabilityFactor { get; init; } = 0.5;

    /// <summary>
    /// The extra cost, per earlier use, of an edge already on a previously found path when looking for diverse
    /// paths: <c>uses × max(DiversityPenaltyBaseMsat, amount × DiversityPenaltyPpm / 1e6)</c>. Defaults 10,000 msat
    /// and 10,000 ppm.
    /// </summary>
    public double DiversityPenaltyBaseMsat { get; init; } = 10_000;

    /// <inheritdoc cref="DiversityPenaltyBaseMsat"/>
    public double DiversityPenaltyPpm { get; init; } = 10_000;

    /// <summary>
    /// The cost of carrying <paramref name="amountMsat"/> over one edge that charges <paramref name="feeMsat"/>,
    /// adds <paramref name="cltvDelta"/> blocks and succeeds with probability <paramref name="probability"/>.
    /// </summary>
    public double EdgeCost(ulong feeMsat, ulong amountMsat, uint cltvDelta, double probability)
    {
        var p = Math.Clamp(probability, double.Epsilon, 1.0);
        var risk = (double)amountMsat * cltvDelta * RiskFactorPpmPerBlock / 1_000_000;
        var penalty = -Math.Log(p) * PenaltyBaseMsat + (double)amountMsat * PenaltyPpm / 1_000_000 * (1 - p);
        return feeMsat + risk + penalty;
    }

    /// <summary>
    /// The diversity penalty of an edge used <paramref name="uses"/> times by earlier paths.
    /// </summary>
    public double DiversityPenalty(int uses, ulong amountMsat) =>
        uses <= 0 ? 0 : uses * Math.Max(DiversityPenaltyBaseMsat, (double)amountMsat * DiversityPenaltyPpm / 1_000_000);
}