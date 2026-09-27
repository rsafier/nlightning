using System.Globalization;

namespace NLightning.Client.Handlers;

using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Ipc;
using Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The splice commands of the CLI (ClientCommand 33/34, splicing plan §3.10):
/// <c>splicein|splice-in &lt;channel_id&gt; &lt;amount_sat&gt; [--feerate &lt;sat_per_kw&gt;]</c> and
/// <c>spliceout|splice-out &lt;channel_id&gt; &lt;amount_sat&gt; [--address &lt;address&gt;] [--feerate
/// &lt;sat_per_kw&gt;]</c>.
/// </summary>
internal static class SpliceCommands
{
    /// <summary>The usage of splicein.</summary>
    internal const string SpliceInUsage = "<channel_id> <amount_sat> [--feerate <sat_per_kw>]";

    /// <summary>The usage of spliceout.</summary>
    internal const string SpliceOutUsage = "<channel_id> <amount_sat> [--address <address>] [--feerate <sat_per_kw>]";

    /// <summary>BOLT 3's feerate floor; the daemon refuses less (<c>SpliceCommand.MinFeeRatePerKw</c>).</summary>
    internal const uint MinFeeRatePerKw = 253;

    /// <summary>The largest feerate, 1,000 sat/vB; the daemon refuses more (<c>SpliceCommand.MaxFeeRatePerKw</c>).</summary>
    internal const uint MaxFeeRatePerKw = 250_000;

    /// <summary>Whether <paramref name="cmd"/> is splicein.</summary>
    internal static bool IsSpliceIn(string cmd) => cmd is "splicein" or "splice-in";

    /// <summary>Whether <paramref name="cmd"/> is spliceout.</summary>
    internal static bool IsSpliceOut(string cmd) => cmd is "spliceout" or "splice-out";

    /// <summary>
    /// Checks the arguments of splicein or spliceout.
    /// </summary>
    /// <returns>An error message with the usage, or null when they are valid.</returns>
    internal static string? Validate(string cmd, string[] commandArgs)
    {
        var spliceOut = IsSpliceOut(cmd);
        return Parse(commandArgs, spliceOut, out var error) is null
                   ? $"{error} Usage: {cmd} {(spliceOut ? SpliceOutUsage : SpliceInUsage)}"
                   : null;
    }

    /// <summary>
    /// Runs a validated splicein or spliceout and prints its result.
    /// </summary>
    /// <returns>True when the splice was not refused or aborted.</returns>
    internal static async Task<bool> RunAsync(string cmd, string[] commandArgs, NamedPipeIpcClient client,
                                              CancellationToken cancellationToken)
    {
        var spliceOut = IsSpliceOut(cmd);
        var arguments = Parse(commandArgs, spliceOut, out _)!;
        var response = spliceOut
                           ? await client.SpliceOutAsync(arguments.ChannelId, arguments.AmountSat, arguments.Address,
                                                         arguments.FeeRatePerKw, cancellationToken)
                           : await client.SpliceInAsync(arguments.ChannelId, arguments.AmountSat,
                                                        arguments.FeeRatePerKw, cancellationToken);
        new SplicePrinter().Print(response);
        return IsSuccess(response);
    }

    /// <summary>A splice that was not aborted and carries no failure reason.</summary>
    internal static bool IsSuccess(SpliceIpcResponse response) =>
        response.State != SpliceNegotiationState.Aborted && response.FailureReason is null;

    /// <summary>
    /// <c>&lt;channel_id&gt; &lt;amount_sat&gt;</c> and the options (also as <c>--option=value</c>, anywhere);
    /// <c>--address</c> only for spliceout.
    /// </summary>
    /// <returns>The arguments, or null with <paramref name="error"/> set.</returns>
    internal static SpliceArguments? Parse(string[] commandArgs, bool spliceOut, out string? error)
    {
        error = null;
        uint? feeRate = null;
        string? address = null;
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
            if (name is not "--feerate" && !(spliceOut && name is "--address"))
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

            if (name is "--address")
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = "Missing value for --address.";
                    return null;
                }

                address = value;
                continue;
            }

            if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var rate)
             || rate < MinFeeRatePerKw || rate > MaxFeeRatePerKw)
            {
                error = $"Invalid feerate '{value}': expected {MinFeeRatePerKw} to {MaxFeeRatePerKw} sat/kw.";
                return null;
            }

            feeRate = rate;
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

        if (!ulong.TryParse(positional[1], NumberStyles.None, CultureInfo.InvariantCulture, out var amountSat)
         || amountSat == 0 || amountSat > ClientApp.MaxOpenChannelSats)
        {
            error = $"Invalid amount '{positional[1]}': expected a positive number of sats up to "
                  + $"{ClientApp.MaxOpenChannelSats}.";
            return null;
        }

        return new SpliceArguments(channelId, amountSat, address, feeRate);
    }
}

/// <summary>The parsed arguments of splicein/spliceout (<see cref="Address"/> is always null for splicein).</summary>
internal sealed record SpliceArguments(ChannelId ChannelId, ulong AmountSat, string? Address, uint? FeeRatePerKw);