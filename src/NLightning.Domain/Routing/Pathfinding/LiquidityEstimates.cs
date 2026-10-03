namespace NLightning.Domain.Routing.Pathfinding;

/// <summary>
/// What we learned about the liquidity of channel directions (plan BOLT7 §3.10, D8): a lower bound (an amount that
/// went through) and an upper bound (an amount that failed with <c>temporary_channel_failure</c>), each fading with a
/// half-life. Mission control (G4-T2) fills it; the pathfinder reads the success probability.
/// </summary>
/// <remarks>
/// <para>Model: <c>P = w × P_bounds + (1 − w) × P_prior</c>, where <c>w = 2^(−age / HalfLife)</c> is the weight of the
/// bounds and <c>P_bounds</c> is the uniform-liquidity probability on <c>[min, max)</c>: 1 below <c>min</c>, 0 at or
/// above <c>max</c>, <c>(max − amount) / (max − min)</c> in between (<c>max</c> defaults to the capacity; without a
/// capacity and an upper bound, amounts from <c>min</c> up get the prior). With no record, <c>P = P_prior</c>.
/// An amount above the known capacity always gets 0.</para>
/// <para>Not thread-safe: the owner mutates it under its own lock and hands the pathfinder a <see cref="Clone"/>.
/// Never persisted (D8).</para>
/// </remarks>
public sealed class LiquidityEstimates
{
    private readonly Dictionary<DirectedChannel, Bounds> _bounds;

    /// <summary>
    /// Creates an empty set of estimates.
    /// </summary>
    /// <param name="halfLife">How fast what we learned fades (default 1 hour).</param>
    public LiquidityEstimates(TimeSpan? halfLife = null)
    {
        HalfLife = halfLife ?? TimeSpan.FromHours(1);
        if (HalfLife <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(halfLife), "The half-life must be positive.");

        _bounds = new Dictionary<DirectedChannel, Bounds>();
    }

    private LiquidityEstimates(TimeSpan halfLife, Dictionary<DirectedChannel, Bounds> bounds)
    {
        HalfLife = halfLife;
        _bounds = new Dictionary<DirectedChannel, Bounds>(bounds);
    }

    /// <summary>The half-life of the bounds.</summary>
    public TimeSpan HalfLife { get; }

    /// <summary>The number of channel directions with a record.</summary>
    public int Count => _bounds.Count;

    /// <summary>
    /// Records that <paramref name="amountMsat"/> went through <paramref name="channel"/> (raises the lower bound;
    /// the upper bound moves above it when needed).
    /// </summary>
    public void RecordSuccess(DirectedChannel channel, ulong amountMsat, ulong atUnixSeconds)
    {
        var current = GetCurrent(channel, atUnixSeconds);
        var min = Math.Max(current.MinMsat, amountMsat);
        var max = current.MaxMsat is { } m && m <= min ? null : current.MaxMsat;
        _bounds[channel] = new Bounds(min, max, atUnixSeconds);
    }

    /// <summary>
    /// Records that <paramref name="amountMsat"/> failed on <paramref name="channel"/> for lack of liquidity (lowers
    /// the upper bound; the lower bound moves below it when needed).
    /// </summary>
    public void RecordFailure(DirectedChannel channel, ulong amountMsat, ulong atUnixSeconds)
    {
        var current = GetCurrent(channel, atUnixSeconds);
        var max = current.MaxMsat is { } m ? Math.Min(m, amountMsat) : amountMsat;
        var min = Math.Min(current.MinMsat, max == 0 ? 0 : max - 1);
        _bounds[channel] = new Bounds(min, max, atUnixSeconds);
    }

    /// <summary>
    /// Forgets <paramref name="channel"/>.
    /// </summary>
    public bool Remove(DirectedChannel channel) => _bounds.Remove(channel);

    /// <summary>
    /// The bounds of <paramref name="channel"/> as recorded (not decayed).
    /// </summary>
    public bool TryGetBounds(DirectedChannel channel, out ulong minMsat, out ulong? maxMsat, out ulong atUnixSeconds)
    {
        if (_bounds.TryGetValue(channel, out var bounds))
        {
            (minMsat, maxMsat, atUnixSeconds) = (bounds.MinMsat, bounds.MaxMsat, bounds.AtUnixSeconds);
            return true;
        }

        (minMsat, maxMsat, atUnixSeconds) = (0, null, 0);
        return false;
    }

    /// <summary>
    /// The probability that <paramref name="amountMsat"/> goes through <paramref name="channel"/> at
    /// <paramref name="nowUnixSeconds"/>.
    /// </summary>
    public double GetSuccessProbability(DirectedChannel channel, ulong amountMsat, ulong? capacityMsat,
                                        ulong nowUnixSeconds, double aprioriProbability)
    {
        if (capacityMsat is { } capacity && amountMsat > capacity)
            return 0;

        if (!_bounds.TryGetValue(channel, out var bounds))
            return aprioriProbability;

        var weight = Weight(bounds.AtUnixSeconds, nowUnixSeconds);
        double boundsProbability;
        var max = bounds.MaxMsat ?? capacityMsat;
        if (amountMsat < bounds.MinMsat)
            boundsProbability = 1;
        else if (max is null)
            boundsProbability = aprioriProbability;
        else if (amountMsat >= max.Value)
            boundsProbability = 0;
        else
            boundsProbability = (double)(max.Value - amountMsat) / (max.Value - bounds.MinMsat);

        return weight * boundsProbability + (1 - weight) * aprioriProbability;
    }

    /// <summary>
    /// An independent copy (for a pathfinding run while the owner keeps learning).
    /// </summary>
    public LiquidityEstimates Clone() => new(HalfLife, _bounds);

    private double Weight(ulong atUnixSeconds, ulong nowUnixSeconds)
    {
        var age = nowUnixSeconds > atUnixSeconds ? nowUnixSeconds - atUnixSeconds : 0;
        return Math.Pow(2, -(age / HalfLife.TotalSeconds));
    }

    private Bounds GetCurrent(DirectedChannel channel, ulong nowUnixSeconds)
    {
        if (!_bounds.TryGetValue(channel, out var bounds))
            return new Bounds(0, null, nowUnixSeconds);

        // Fold the decay into the stored bounds before moving them, so an old bound does not come back at full weight
        var weight = Weight(bounds.AtUnixSeconds, nowUnixSeconds);
        var min = (ulong)(bounds.MinMsat * weight);
        var max = weight < 0.01 ? null : bounds.MaxMsat;
        return new Bounds(min, max, nowUnixSeconds);
    }

    private readonly record struct Bounds(ulong MinMsat, ulong? MaxMsat, ulong AtUnixSeconds);
}