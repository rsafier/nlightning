using System.Globalization;

namespace NLightning.Client.Handlers;

/// <summary>
/// The liquidity ads options of <c>openchannel</c>, <c>splicein</c> and <c>bumpopen</c> (NL-771):
/// <c>--request-inbound &lt;sat&gt;</c> buys that much inbound liquidity from the peer with the funding attempt and
/// <c>--max-liquidity-fee &lt;sat&gt;</c> caps what we pay for it (mining + service fee; the node's
/// <c>Node:LiquidityAds:MaxFeeSat</c> when left out). Both also as <c>--option=value</c>, anywhere after the command.
/// </summary>
internal static class LiquidityOptions
{
    /// <summary>The option that buys inbound liquidity.</summary>
    internal const string RequestInboundOption = "--request-inbound";

    /// <summary>The option that caps the liquidity fee.</summary>
    internal const string MaxLiquidityFeeOption = "--max-liquidity-fee";

    /// <summary>The usage fragment of the options.</summary>
    internal const string Usage = "[--request-inbound <sat> [--max-liquidity-fee <sat>]]";

    /// <summary>The most satoshis that exist (21 million BTC); the daemon refuses more.</summary>
    internal const ulong MaxAmountSat = 2_100_000_000_000_000;

    /// <summary>
    /// Takes the liquidity options out of <paramref name="commandArgs"/>.
    /// </summary>
    /// <param name="commandArgs">The arguments after the command name.</param>
    /// <param name="liquidity">The options found.</param>
    /// <param name="error">Why the options are invalid (a missing or bad value, a repeated option,
    /// <c>--max-liquidity-fee</c> without <c>--request-inbound</c>), else null.</param>
    /// <returns>The other arguments, in order, or null with <paramref name="error"/> set.</returns>
    internal static string[]? Extract(string[] commandArgs, out LiquidityArguments liquidity, out string? error)
    {
        liquidity = LiquidityArguments.None;
        error = null;
        ulong? requestInbound = null;
        ulong? maxFee = null;
        var rest = new List<string>(commandArgs.Length);
        for (var i = 0; i < commandArgs.Length; i++)
        {
            var argument = commandArgs[i];
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? argument : argument[..separator];
            if (name is not (RequestInboundOption or MaxLiquidityFeeOption))
            {
                rest.Add(argument);
                continue;
            }

            string value;
            if (separator >= 0)
            {
                value = argument[(separator + 1)..];
            }
            else if (i + 1 < commandArgs.Length)
            {
                value = commandArgs[++i];
            }
            else
            {
                error = $"Missing value for {name}.";
                return null;
            }

            if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var sat)
             || sat > MaxAmountSat || (name == RequestInboundOption && sat == 0))
            {
                error = name == RequestInboundOption
                            ? $"Invalid {name} '{value}': expected a positive number of sats up to {MaxAmountSat}."
                            : $"Invalid {name} '{value}': expected a number of sats up to {MaxAmountSat}.";
                return null;
            }

            if ((name == RequestInboundOption ? requestInbound : maxFee) is not null)
            {
                error = $"{name} given twice.";
                return null;
            }

            if (name == RequestInboundOption)
                requestInbound = sat;
            else
                maxFee = sat;
        }

        if (maxFee is not null && requestInbound is null)
        {
            error = $"{MaxLiquidityFeeOption} needs {RequestInboundOption}.";
            return null;
        }

        liquidity = new LiquidityArguments(requestInbound, maxFee);
        return rest.ToArray();
    }
}

/// <summary>The parsed liquidity ads options.</summary>
/// <param name="RequestInboundSat">The inbound liquidity to buy, or null for none.</param>
/// <param name="MaxLiquidityFeeSat">The most we pay for it, or null for the node's limit.</param>
internal sealed record LiquidityArguments(ulong? RequestInboundSat, ulong? MaxLiquidityFeeSat)
{
    /// <summary>No purchase.</summary>
    public static readonly LiquidityArguments None = new(null, null);

    /// <summary>Whether a purchase was asked for.</summary>
    public bool IsRequested => RequestInboundSat is not null;
}