namespace NLightning.Client.Handlers;

using Domain.Channels.Enums;
using Ipc;
using Printers;

internal class OpenChannelMessageHandler
{
    /// <summary>
    /// The option that opens a public channel (<c>announce_channel</c>, BOLT 7 plan G1-T1).
    /// </summary>
    internal const string PublicOption = "--public";

    internal const string Usage = "<node> <amount_sats> [push_sats] [--public]";

    internal static async Task HandleAsync(string[] commandArgs, NamedPipeIpcClient client,
                                           CancellationToken cancellationToken)
    {
        var positional = ParseArguments(commandArgs, out var isPublic, out var error);
        if (error is not null)
            throw new ArgumentException(error, nameof(commandArgs));

        if (positional.Length < 2)
            throw new ArgumentException($"Missing arguments. Usage: openchannel {Usage}", nameof(commandArgs));

        var channelResponse = await client.OpenChannelAsync(positional[0], positional[1],
                                                           positional.Length > 2 ? positional[2] : null,
                                                           cancellationToken, isPublic);
        new OpenChannelPrinter().Print(channelResponse);

        while (!cancellationToken.IsCancellationRequested)
        {
            var subscriptionResponse =
                await client.OpenChannelSubscriptionAsync(channelResponse.ChannelId, cancellationToken);

            new OpenChannelSubscriptionPrinter().Print(subscriptionResponse);

            if (subscriptionResponse.ChannelState is ChannelState.ReadyForUs or ChannelState.ReadyForThem)
                break;
        }
    }

    /// <summary>
    /// Splits the arguments of <c>openchannel</c> into the positional ones (node, amount, push) and the
    /// <see cref="PublicOption"/> flag, which may appear anywhere after the command.
    /// </summary>
    /// <param name="commandArgs">The arguments after the command name.</param>
    /// <param name="isPublic">True when <see cref="PublicOption"/> was given.</param>
    /// <param name="error">The usage error for an unknown option or too many arguments, else null.</param>
    /// <returns>The positional arguments, in order.</returns>
    internal static string[] ParseArguments(string[] commandArgs, out bool isPublic, out string? error)
    {
        isPublic = false;
        error = null;
        var positional = new List<string>(commandArgs.Length);
        foreach (var arg in commandArgs)
        {
            if (string.Equals(arg, PublicOption, StringComparison.OrdinalIgnoreCase))
            {
                isPublic = true;
                continue;
            }

            // A negative push ("-1") stays positional and is refused by the amount check; anything else starting with
            // "--" is an option we don't know
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unknown option '{arg}': expected {PublicOption}.";
                return [];
            }

            positional.Add(arg);
        }

        if (positional.Count > 3)
            error = $"Too many arguments. Usage: openchannel {Usage}";

        return positional.ToArray();
    }
}