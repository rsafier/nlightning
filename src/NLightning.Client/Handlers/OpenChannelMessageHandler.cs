namespace NLightning.Client.Handlers;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Ipc;
using Printers;
using Transport.Ipc.Responses;

internal class OpenChannelMessageHandler
{
    /// <summary>
    /// The option that opens a public channel (<c>announce_channel</c>, BOLT 7 plan G1-T1).
    /// </summary>
    internal const string PublicOption = "--public";

    /// <summary>
    /// The option that opens a dual-funded (v2) channel (<c>open_channel2</c>, BOLT 2 "Channel Establishment v2"): the
    /// amount is our contribution, the peer may add its own; no push. Refused when the peer lacks
    /// <c>option_dual_fund</c>. Without it the daemon opens v2 anyway when the peer supports dual funding and no push is
    /// given (NL-551).
    /// </summary>
    internal const string DualFundOption = "--dual-fund";

    /// <summary>
    /// The option that opens a v1 channel (<c>open_channel</c>) even when the peer supports dual funding (NL-551).
    /// </summary>
    internal const string V1Option = "--v1";

    /// <summary>
    /// The option that returns as soon as the funding transaction is printed instead of waiting for
    /// <c>channel_ready</c> (NL-535); the open continues either way.
    /// </summary>
    internal const string NoWaitOption = "--no-wait";

    internal const string Usage =
        "<node> <amount_sats> [push_sats] [--public] [--dual-fund|--v1] [--no-wait] " + LiquidityOptions.Usage;

    internal static async Task HandleAsync(string[] commandArgs, NamedPipeIpcClient client,
                                           CancellationToken cancellationToken, LabelArguments? labels = null)
    {
        var positional = ParseArguments(commandArgs, out var isPublic, out var isDualFunded, out var forceV1,
                                        out var noWait, out var liquidity, out var error);
        if (error is not null)
            throw new ArgumentException(error, nameof(commandArgs));

        if (positional.Length < 2)
            throw new ArgumentException($"Missing arguments. Usage: openchannel {Usage}", nameof(commandArgs));

        await RunAsync(ct => client.OpenChannelAsync(positional[0], positional[1],
                                                     positional.Length > 2 ? positional[2] : null, ct, isPublic,
                                                     isDualFunded, forceV1, labels, liquidity.RequestInboundSat,
                                                     liquidity.MaxLiquidityFeeSat),
                       client.OpenChannelSubscriptionAsync, noWait, Console.Out, cancellationToken);
    }

    /// <summary>
    /// Opens the channel and follows it: prints the funding transaction as soon as it is published (at once for a
    /// dual-funded open, whose response carries it), then every new funding transaction (an RBF attempt of either side,
    /// NL-535), until the channel is ready. With <paramref name="noWait"/> it returns once the first funding is printed.
    /// Cancelling (Ctrl-C) after a funding was printed stops the wait only: the open continues on the node.
    /// </summary>
    /// <param name="open">Sends <c>openchannel</c>.</param>
    /// <param name="subscribe">One long-poll of the open subscription, given the funding printed last.</param>
    /// <param name="noWait">Return once the first funding transaction is printed.</param>
    /// <param name="output">Where to print.</param>
    /// <param name="cancellationToken">Ctrl-C.</param>
    /// <summary>
    /// <see cref="ParseArguments(string[], out bool, out bool, out bool, out bool, out LiquidityArguments, out string?)"/>
    /// for callers that do not read the liquidity options.
    /// </summary>
    internal static string[] ParseArguments(string[] commandArgs, out bool isPublic, out bool isDualFunded,
                                            out bool forceV1, out bool noWait, out string? error) =>
        ParseArguments(commandArgs, out isPublic, out isDualFunded, out forceV1, out noWait, out _, out error);

    internal static async Task RunAsync(Func<CancellationToken, Task<OpenChannelIpcResponse>> open,
                                        Func<ChannelId, TxId?, CancellationToken,
                                            Task<OpenChannelSubscriptionIpcResponse>> subscribe, bool noWait,
                                        TextWriter output, CancellationToken cancellationToken)
    {
        var channelResponse = await open(cancellationToken);
        new OpenChannelPrinter(output).Print(channelResponse);

        var printed = channelResponse.FundingTxId;
        if (printed is not null && noWait)
        {
            PrintNotWaiting(output, channelResponse.ChannelId);
            return;
        }

        var subscriptionPrinter = new OpenChannelSubscriptionPrinter(output);
        try
        {
            while (true)
            {
                var subscriptionResponse = await subscribe(channelResponse.ChannelId, printed, cancellationToken);
                if (subscriptionResponse.ChannelState is ChannelState.ReadyForUs or ChannelState.ReadyForThem)
                {
                    subscriptionPrinter.Print(subscriptionResponse);
                    return;
                }

                // The daemon answers once per new funding transaction; an answer without one is printed as it is
                if (subscriptionResponse.ChannelState == ChannelState.V1FundingSigned
                 && subscriptionResponse.TxId is not null && subscriptionResponse.TxId == printed)
                    continue;

                subscriptionPrinter.Print(subscriptionResponse, printed);
                if (subscriptionResponse.ChannelState == ChannelState.V1FundingSigned)
                {
                    printed = subscriptionResponse.TxId ?? printed;
                    if (noWait)
                    {
                        PrintNotWaiting(output, channelResponse.ChannelId);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && printed is not null)
        {
            // Ctrl-C after the funding was printed: stop waiting, the node keeps the open
            PrintNotWaiting(output, channelResponse.ChannelId);
        }
    }

    private static void PrintNotWaiting(TextWriter output, ChannelId channelId)
    {
        output.WriteLine("Not waiting for the channel to be ready; the open continues on the node.");
        output.WriteLine("Follow it with: listchannels (channel {0})", channelId);
    }

    /// <summary>
    /// Splits the arguments of <c>openchannel</c> into the positional ones (node, amount, push) and the
    /// <see cref="PublicOption"/>, <see cref="DualFundOption"/>, <see cref="V1Option"/> and <see cref="NoWaitOption"/>
    /// flags, which may appear anywhere after the command.
    /// </summary>
    /// <param name="commandArgs">The arguments after the command name.</param>
    /// <param name="isPublic">True when <see cref="PublicOption"/> was given.</param>
    /// <param name="isDualFunded">True when <see cref="DualFundOption"/> was given.</param>
    /// <param name="forceV1">True when <see cref="V1Option"/> was given.</param>
    /// <param name="noWait">True when <see cref="NoWaitOption"/> was given.</param>
    /// <param name="liquidity">The liquidity ads options (<see cref="LiquidityOptions"/>, NL-771):
    /// <c>--request-inbound</c> buys inbound liquidity with a dual-funded open, so it is refused with
    /// <see cref="V1Option"/>.</param>
    /// <param name="error">The usage error for an unknown option, <see cref="DualFundOption"/> together with
    /// <see cref="V1Option"/>, a bad liquidity option or too many arguments, else null.</param>
    /// <returns>The positional arguments, in order.</returns>
    internal static string[] ParseArguments(string[] commandArgs, out bool isPublic, out bool isDualFunded,
                                            out bool forceV1, out bool noWait, out LiquidityArguments liquidity,
                                            out string? error)
    {
        isPublic = false;
        isDualFunded = false;
        forceV1 = false;
        noWait = false;
        error = null;
        var rest = LiquidityOptions.Extract(commandArgs, out liquidity, out var liquidityError);
        if (rest is null)
        {
            error = $"{liquidityError} Usage: openchannel {Usage}";
            return [];
        }

        var positional = new List<string>(rest.Length);
        foreach (var arg in rest)
        {
            if (string.Equals(arg, PublicOption, StringComparison.OrdinalIgnoreCase))
            {
                isPublic = true;
                continue;
            }

            if (string.Equals(arg, DualFundOption, StringComparison.OrdinalIgnoreCase))
            {
                isDualFunded = true;
                continue;
            }

            if (string.Equals(arg, V1Option, StringComparison.OrdinalIgnoreCase))
            {
                forceV1 = true;
                continue;
            }

            if (string.Equals(arg, NoWaitOption, StringComparison.OrdinalIgnoreCase))
            {
                noWait = true;
                continue;
            }

            // A negative push ("-1") stays positional and is refused by the amount check; anything else starting with
            // "--" is an option we don't know
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unknown option '{arg}': expected {PublicOption}, {DualFundOption}, {V1Option}, "
                      + $"{NoWaitOption}, {LiquidityOptions.RequestInboundOption} or "
                      + $"{LiquidityOptions.MaxLiquidityFeeOption}.";
                return [];
            }

            positional.Add(arg);
        }

        if (isDualFunded && forceV1)
            error = $"{DualFundOption} and {V1Option} can't be used together.";
        else if (liquidity.IsRequested && forceV1)
            error = $"{LiquidityOptions.RequestInboundOption} buys inbound liquidity with a dual-funded open; it "
                  + $"can't be used with {V1Option}.";
        else if (positional.Count > 3)
            error = $"Too many arguments. Usage: openchannel {Usage}";

        return positional.ToArray();
    }
}