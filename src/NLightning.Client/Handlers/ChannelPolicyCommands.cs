using System.Globalization;

namespace NLightning.Client.Handlers;

using Domain.Channels.ValueObjects;
using Ipc;
using Printers;
using Transport.Ipc.Requests;

/// <summary>
/// The per-channel routing policy commands of the CLI (wave sp1 lane SP1-G):
/// <c>setchannelpolicy|set-channel-policy &lt;channel&gt; [--fee-base-msat N] [--fee-ppm N] [--cltv-delta N]
/// [--htlc-min-msat N] [--htlc-max-msat N] [--reset]</c> (ClientCommand 35) and
/// <c>getchannelpolicy|get-channel-policy &lt;channel&gt;</c> (36). A channel is its channel id (64 hex characters),
/// its short channel id as <c>BLOCKxTXxOUTPUT</c> or as the BOLT 7 uint64 (LND's <c>chan_id</c>).
/// </summary>
internal static class ChannelPolicyCommands
{
    /// <summary>The usage of setchannelpolicy.</summary>
    internal const string SetUsage = "<channel_id|short_channel_id> [--fee-base-msat <n>] [--fee-ppm <n>] "
                                   + "[--cltv-delta <n>] [--htlc-min-msat <n>] [--htlc-max-msat <n>] [--reset]";

    /// <summary>The usage of getchannelpolicy.</summary>
    internal const string GetUsage = "<channel_id|short_channel_id>";

    /// <summary>Whether <paramref name="cmd"/> is one of the channel policy commands.</summary>
    internal static bool IsChannelPolicyCommand(string cmd) =>
        IsSetCommand(cmd) || cmd is "getchannelpolicy" or "get-channel-policy";

    /// <summary>
    /// Checks the arguments of a channel policy command.
    /// </summary>
    /// <returns>An error message with the usage, or null when they are valid.</returns>
    internal static string? Validate(string cmd, string[] commandArgs)
    {
        if (IsSetCommand(cmd))
            return ParseSetOptions(commandArgs, out var setError) is null ? $"{setError} Usage: {cmd} {SetUsage}" : null;

        return ParseGetOptions(commandArgs, out var getError) is null ? $"{getError} Usage: {cmd} {GetUsage}" : null;
    }

    /// <summary>
    /// Runs a validated channel policy command and prints its result.
    /// </summary>
    internal static async Task RunAsync(string cmd, string[] commandArgs, NamedPipeIpcClient client,
                                        CancellationToken cancellationToken)
    {
        var printer = new ChannelPolicyPrinter();
        if (IsSetCommand(cmd))
        {
            printer.Print(await client.SetChannelPolicyAsync(ParseSetOptions(commandArgs, out _)!, cancellationToken));
            return;
        }

        printer.Print(await client.GetChannelPolicyAsync(ParseGetOptions(commandArgs, out _)!, cancellationToken));
    }

    /// <summary>
    /// <c>&lt;channel&gt;</c> and the options of setchannelpolicy (also as <c>--option=value</c>, in any order after
    /// the channel's position is free).
    /// </summary>
    /// <returns>The request, or null with <paramref name="error"/> set.</returns>
    internal static SetChannelPolicyIpcRequest? ParseSetOptions(string[] commandArgs, out string? error)
    {
        error = null;
        string? channel = null;
        uint? feeBase = null, feePpm = null;
        ushort? cltvDelta = null;
        ulong? htlcMin = null, htlcMax = null;
        var reset = false;

        for (var i = 0; i < commandArgs.Length; i++)
        {
            var argument = commandArgs[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (channel is not null)
                {
                    error = $"Unexpected argument '{argument}'.";
                    return null;
                }

                channel = argument;
                continue;
            }

            var name = argument;
            string? value = null;
            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                name = argument[..equals];
                value = argument[(equals + 1)..];
            }

            if (name == "--reset")
            {
                if (value is not null)
                {
                    error = "--reset takes no value.";
                    return null;
                }

                reset = true;
                continue;
            }

            if (name is not ("--fee-base-msat" or "--fee-ppm" or "--cltv-delta" or "--htlc-min-msat"
                          or "--htlc-max-msat"))
            {
                error = $"Unknown option '{name}'.";
                return null;
            }

            if (value is null)
            {
                if (i + 1 >= commandArgs.Length)
                {
                    error = $"{name} needs a value.";
                    return null;
                }

                value = commandArgs[++i];
            }

            switch (name)
            {
                case "--fee-base-msat":
                    if (!TryParseOnce(name, value, feeBase, uint.MaxValue, out var parsedBase, out error))
                        return null;
                    feeBase = (uint)parsedBase;
                    break;
                case "--fee-ppm":
                    if (!TryParseOnce(name, value, feePpm, uint.MaxValue, out var parsedPpm, out error))
                        return null;
                    feePpm = (uint)parsedPpm;
                    break;
                case "--cltv-delta":
                    if (!TryParseOnce(name, value, cltvDelta, ushort.MaxValue, out var parsedCltv, out error))
                        return null;
                    cltvDelta = (ushort)parsedCltv;
                    break;
                case "--htlc-min-msat":
                    if (!TryParseOnce(name, value, htlcMin, ulong.MaxValue, out var parsedMin, out error))
                        return null;
                    htlcMin = parsedMin;
                    break;
                default:
                    if (!TryParseOnce(name, value, htlcMax, ulong.MaxValue, out var parsedMax, out error))
                        return null;
                    htlcMax = parsedMax;
                    break;
            }
        }

        if (!TryParseChannel(channel, out var channelId, out var shortChannelId, out error))
            return null;

        var hasValues = feeBase is not null || feePpm is not null || cltvDelta is not null || htlcMin is not null
                     || htlcMax is not null;
        if (reset && hasValues)
        {
            error = "--reset takes no other option.";
            return null;
        }

        if (!reset && !hasValues)
        {
            error = "Nothing to set: give at least one option, or --reset.";
            return null;
        }

        return new SetChannelPolicyIpcRequest
        {
            ChannelId = channelId,
            ShortChannelId = shortChannelId,
            FeeBaseMsat = feeBase,
            FeeProportionalMillionths = feePpm,
            CltvExpiryDelta = cltvDelta,
            HtlcMinimumMsat = htlcMin,
            HtlcMaximumMsat = htlcMax,
            Reset = reset
        };
    }

    /// <summary>
    /// <c>&lt;channel&gt;</c> of getchannelpolicy.
    /// </summary>
    /// <returns>The request, or null with <paramref name="error"/> set.</returns>
    internal static GetChannelPolicyIpcRequest? ParseGetOptions(string[] commandArgs, out string? error)
    {
        if (commandArgs.Length != 1)
        {
            error = "Expected exactly one channel.";
            return null;
        }

        return TryParseChannel(commandArgs[0], out var channelId, out var shortChannelId, out error)
                   ? new GetChannelPolicyIpcRequest { ChannelId = channelId, ShortChannelId = shortChannelId }
                   : null;
    }

    private static bool IsSetCommand(string cmd) => cmd is "setchannelpolicy" or "set-channel-policy";

    /// <summary>A channel id (64 hex), a short channel id (<c>BLOCKxTXxOUTPUT</c>) or a BOLT 7 uint64.</summary>
    private static bool TryParseChannel(string? value, out ChannelId? channelId, out ulong? shortChannelId,
                                        out string? error)
    {
        channelId = null;
        shortChannelId = null;
        error = null;
        if (value is null)
        {
            error = "Missing channel.";
            return false;
        }

        if (ClientApp.TryParseChannelId(value, out var id))
        {
            channelId = id;
            return true;
        }

        if (ClientApp.TryParseShortChannelId(value, out var scid)
         || (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out scid) && scid > 0))
        {
            shortChannelId = scid;
            return true;
        }

        error = $"Invalid channel '{value}': expected a channel id (64 hex characters) or a short channel id "
              + "(BLOCKxTXxOUTPUT or its number).";
        return false;
    }

    private static bool TryParseOnce<T>(string name, string value, T? current, ulong maximum, out ulong parsed,
                                        out string? error) where T : struct
    {
        parsed = 0;
        error = null;
        if (current is not null)
        {
            error = $"{name} given twice.";
            return false;
        }

        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) || parsed > maximum)
        {
            error = $"Invalid {name} '{value}': expected a number from 0 to "
                  + $"{maximum.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }

        return true;
    }
}