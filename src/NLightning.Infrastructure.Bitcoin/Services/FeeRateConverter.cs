namespace NLightning.Infrastructure.Bitcoin.Services;

using Domain.Node.Options;

/// <summary>
/// Converts fee rates to sat/kw (<c>feerate_per_kw</c>), the one unit the node works in (NL-288).
/// </summary>
/// <remarks>
/// 1 vbyte is 4 weight units, so 1 sat/vB = 250 sat/kw and 1 sat/kvB = 1/4 sat/kw. The result is rounded down and
/// raised to the BOLT 3 floor of 253 sat/kw (1 sat/vB rounded up), below which transactions don't relay.
/// </remarks>
public static class FeeRateConverter
{
    public const string SatPerVByte = "sat/vB";
    public const string SatPerKvByte = "sat/kvB";
    public const string SatPerKw = "sat/kw";
    public const string BtcPerKvByte = "BTC/kvB";

    /// <summary>
    /// BOLT 3: the lowest feerate a node should use or accept.
    /// </summary>
    public const long FeeratePerKwFloor = FeeUpdateOptions.FeeratePerKwFloor;

    internal const string KnownUnitsText = $"{SatPerVByte}, {SatPerKvByte}, {SatPerKw} or {BtcPerKvByte}";

    /// <summary>
    /// True when <paramref name="unit"/> is one of the supported units (case-insensitive).
    /// </summary>
    public static bool IsKnownUnit(string? unit) => TryGetSatPerKwFactor(unit, out _);

    /// <summary>
    /// Converts <paramref name="rate"/> in <paramref name="unit"/> to sat/kw, rounded down, at least
    /// <see cref="FeeratePerKwFloor"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The unit is unknown or the rate is negative.</exception>
    public static long ToSatPerKw(decimal rate, string unit)
    {
        if (rate < 0)
            throw new ArgumentException($"A fee rate can't be negative: {rate} {unit}.", nameof(rate));
        if (!TryGetSatPerKwFactor(unit, out var factor))
            throw new ArgumentException($"Unknown fee rate unit '{unit}', expected {KnownUnitsText}.", nameof(unit));

        return Math.Max(FeeratePerKwFloor, (long)decimal.Floor(rate * factor));
    }

    /// <summary>
    /// Converts sat/vB (mempool.space, bitcoind's <c>SatoshiPerByte</c>) to sat/kw: x 250, floored at 253.
    /// </summary>
    public static long SatPerVByteToSatPerKw(decimal satPerVByte) => ToSatPerKw(satPerVByte, SatPerVByte);

    private static bool TryGetSatPerKwFactor(string? unit, out decimal factor)
    {
        factor = unit?.Trim().ToLowerInvariant() switch
        {
            "sat/vb" => 250m,
            "sat/kvb" => 0.25m,
            "sat/kw" => 1m,
            "btc/kvb" => 25_000_000m, // 1 BTC/kvB = 100,000,000 sat/kvB = 25,000,000 sat/kw
            _ => 0m
        };
        return factor != 0m;
    }
}