using System.Globalization;

namespace NLightning.Client.Handlers;

using Domain.Channels.ValueObjects;
using Ipc;
using Printers;
using Transport.Ipc.Responses;

/// <summary>
/// <c>bumpopen|bump-open &lt;channel_id&gt; &lt;feerate_per_kw&gt; [--contribution-sat &lt;sats&gt;]</c> (ClientCommand 38,
/// lane dfrbf): RBF of an unconfirmed dual-funded open, as its opener or its accepter (NL-530).
/// </summary>
internal static class BumpOpenCommands
{
    /// <summary>The usage of bumpopen.</summary>
    internal const string Usage =
        "<channel_id> <feerate_per_kw> [--contribution-sat <sats>] " + LiquidityOptions.Usage;

    /// <summary>The most satoshis that exist (21 million BTC); the daemon refuses a larger contribution.</summary>
    internal const ulong MaxAmountSat = 2_100_000_000_000_000;

    /// <summary>BOLT 3's feerate floor; the daemon refuses less.</summary>
    internal const uint MinFeeRatePerKw = 253;

    /// <summary>The largest feerate, 1,000 sat/vB; the daemon refuses more.</summary>
    internal const uint MaxFeeRatePerKw = 250_000;

    /// <summary>Whether <paramref name="cmd"/> is bumpopen.</summary>
    internal static bool IsBumpOpen(string cmd) => cmd is "bumpopen" or "bump-open";

    /// <summary>Checks the arguments of bumpopen.</summary>
    /// <returns>An error message with the usage, or null when they are valid.</returns>
    internal static string? Validate(string cmd, string[] commandArgs) =>
        Parse(commandArgs, out var error) is null ? $"{error} Usage: {cmd} {Usage}" : null;

    /// <summary>Runs a validated bumpopen and prints the new attempt.</summary>
    internal static async Task RunAsync(string[] commandArgs, NamedPipeIpcClient client, TextWriter output,
                                        CancellationToken cancellationToken)
    {
        var arguments = Parse(commandArgs, out _)!;
        var response = await client.BumpOpenAsync(arguments.ChannelId, arguments.FeeRatePerKw,
                                                  arguments.ContributionSat, cancellationToken,
                                                  arguments.Liquidity.RequestInboundSat,
                                                  arguments.Liquidity.MaxLiquidityFeeSat);
        Print(response, output);
    }

    /// <summary>Prints the new attempt of the open.</summary>
    internal static void Print(BumpOpenIpcResponse response, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(response);
        output.WriteLine("Dual-funded open RBF signed");
        output.WriteLine($"  Channel ID:      {response.ChannelId}");
        output.WriteLine($"  Funding TxId:    {response.FundingTxId}");
        if (response.Purchase is { } purchase)
            LiquidityAdsPrinter.WritePurchase(output, purchase);
        output.WriteLine("  The new attempt is broadcast and replaces the previous one; any signed attempt may still");
        output.WriteLine("  confirm, and the channel follows the one that does.");
    }

    /// <summary>
    /// <c>&lt;channel_id&gt; &lt;feerate_per_kw&gt;</c> and <c>--contribution-sat</c> (also as
    /// <c>--contribution-sat=value</c>, anywhere) and the liquidity ads options (<see cref="LiquidityOptions"/>, NL-771:
    /// without <c>--request-inbound</c> the daemon repeats the previous attempt's purchase, if any).
    /// </summary>
    /// <returns>The arguments, or null with <paramref name="error"/> set.</returns>
    internal static BumpOpenArguments? Parse(string[] commandArgs, out string? error)
    {
        error = null;
        var rest = LiquidityOptions.Extract(commandArgs, out var liquidity, out error);
        if (rest is null)
            return null;

        commandArgs = rest;

        ulong? contributionSat = null;
        var positional = new List<string>();
        for (var i = 0; i < commandArgs.Length; i++)
        {
            var argument = commandArgs[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(argument);
                continue;
            }

            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? argument : argument[..separator];
            if (name is not "--contribution-sat")
            {
                error = $"Unknown option '{argument}'.";
                return null;
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

            if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var sats)
             || sats > MaxAmountSat)
            {
                error = $"Invalid contribution '{value}': expected a number of sats up to {MaxAmountSat}.";
                return null;
            }

            contributionSat = sats;
        }

        if (positional.Count < 2)
        {
            error = "Missing arguments.";
            return null;
        }

        if (positional.Count > 2)
        {
            error = $"Unexpected argument '{positional[2]}'.";
            return null;
        }

        if (!ClientApp.TryParseChannelId(positional[0], out var channelId))
        {
            error = $"Invalid channel id '{positional[0]}': expected 64 hex characters.";
            return null;
        }

        if (!uint.TryParse(positional[1], NumberStyles.None, CultureInfo.InvariantCulture, out var feeRate)
         || feeRate < MinFeeRatePerKw || feeRate > MaxFeeRatePerKw)
        {
            error = $"Invalid feerate '{positional[1]}': expected {MinFeeRatePerKw} to {MaxFeeRatePerKw} sat/kw.";
            return null;
        }

        return new BumpOpenArguments(channelId, feeRate, contributionSat) { Liquidity = liquidity };
    }
}

/// <summary>The parsed arguments of bumpopen.</summary>
internal sealed record BumpOpenArguments(ChannelId ChannelId, uint FeeRatePerKw, ulong? ContributionSat)
{
    /// <summary>The liquidity ads options (NL-771).</summary>
    public LiquidityArguments Liquidity { get; init; } = LiquidityArguments.None;
}