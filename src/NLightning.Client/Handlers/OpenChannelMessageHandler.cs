namespace NLightning.Client.Handlers;

using Domain.Channels.Enums;
using Ipc;
using Printers;

internal class OpenChannelMessageHandler
{
    internal static async Task HandleAsync(string[] commandArgs, NamedPipeIpcClient client,
                                           CancellationToken cancellationToken)
    {
        if (commandArgs.Length < 2)
            throw new ArgumentException("Missing arguments. Usage: openchannel <node> <amount_sats> [push_sats]",
                                        nameof(commandArgs));

        var channelResponse = await client.OpenChannelAsync(commandArgs[0], commandArgs[1],
                                                           commandArgs.Length > 2 ? commandArgs[2] : null,
                                                           cancellationToken);
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
}