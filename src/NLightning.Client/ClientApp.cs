using System.Globalization;

namespace NLightning.Client;

using Daemon.Contracts.Helpers;
using Daemon.Contracts.Utilities;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Handlers;
using Ipc;
using Printers;
using Utils;

/// <summary>
/// Entry point logic of the CLI: parses the arguments, validates them and dispatches the command.
/// </summary>
internal static class ClientApp
{
    internal const int Success = 0;
    internal const int Failure = 1;
    internal const int UsageError = 2;

    /// <summary>
    /// The largest <c>count</c> of listinvoices/listpayments; the daemon refuses a larger page
    /// (<c>ClientRequestGuards.MaxPageSize</c>).
    /// </summary>
    internal const int MaxListCount = 1_000;

    /// <summary>
    /// The longest payinvoice wait; the daemon refuses a longer one (<c>PayInvoiceClientHandler.MaxTimeoutSeconds</c>).
    /// </summary>
    internal const uint MaxPayTimeoutSeconds = 300;

    /// <summary>
    /// The largest payinvoice part limit; the daemon refuses a larger one (<c>PaymentSendOptions.MaxPartsLimit</c>).
    /// </summary>
    internal const uint MaxPayParts = 128;

    /// <summary>
    /// The largest payinvoice fee limit, in msat: the 21M BTC supply.
    /// </summary>
    internal const ulong MaxPayFeeMsat = 2_100_000_000_000_000_000;

    /// <summary>
    /// The longest closechannel wait; the daemon refuses a longer one (<c>CloseChannelClientHandler.MaxWaitSeconds</c>).
    /// </summary>
    internal const uint MaxCloseWaitSeconds = 300;

    /// <summary>
    /// The largest openchannel amount or push, in sats: the 21M BTC supply. Everything the validator accepts must
    /// convert to a <c>long</c> and to msat without overflowing in <c>NamedPipeIpcClient.OpenChannelAsync</c>.
    /// </summary>
    internal const ulong MaxOpenChannelSats = 2_100_000_000_000_000;

    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            if (CommandLineHelper.IsHelpRequested(args))
            {
                ClientUtils.ShowUsage();
                return Success;
            }

            var cmd = CommandLineHelper.GetCommand(args) ?? "node-info";
            var commandArgs = CommandLineHelper.GetCommandArguments(cmd, args);

            var usageError = ValidateArguments(cmd, commandArgs);
            if (usageError is not null)
            {
                Console.Error.WriteLine(usageError);
                ClientUtils.ShowUsage();
                return UsageError;
            }

            // Get network for the NamedPipe file path
            var cookiePath = CommandLineHelper.GetCookiePath(args);
            var namedPipeFilePath = NodeUtils.GetNamedPipeFilePath(cookiePath);
            var cookieFilePath = NodeUtils.GetCookieFilePath(cookiePath);

            await using var client = new NamedPipeIpcClient(namedPipeFilePath, cookieFilePath);

            switch (cmd)
            {
                case "info":
                case "node-info":
                    var info = await client.GetNodeInfoAsync(cancellationToken);
                    new NodeInfoPrinter().Print(info);
                    break;
                case "connect":
                case "connect-peer":
                    var connect = await client.ConnectPeerAsync(commandArgs[0], cancellationToken);
                    new ConnectPeerPrinter().Print(connect);
                    break;
                case "listpeers":
                case "list-peers":
                    var listPeers = await client.ListPeersAsync(cancellationToken);
                    new ListPeersPrinter().Print(listPeers);
                    break;
                case "listchannels":
                case "list-channels":
                    var listChannels =
                        await client.ListChannelsAsync(commandArgs.Length > 0 ? commandArgs[0] : null,
                                                       cancellationToken);
                    new ListChannelsPrinter().Print(listChannels);
                    break;
                case "getaddress":
                case "get-address":
                    var addresses =
                        await client.GetAddressAsync(commandArgs.Length > 0 ? commandArgs[0] : null,
                                                     cancellationToken);
                    new GetAddressPrinter().Print(addresses);
                    break;
                case "walletbalance":
                case "wallet-balance":
                    var balance = await client.GetWalletBalance(cancellationToken);
                    new WalletBalancePrinter().Print(balance);
                    break;
                case "openchannel":
                case "open-channel":
                    await OpenChannelMessageHandler.HandleAsync(commandArgs, client, cancellationToken);
                    break;
                case "createinvoice":
                case "create-invoice":
                case "addinvoice":
                    var invoice = await client.CreateInvoiceAsync(ParseInvoiceAmount(commandArgs[0]),
                                                                  commandArgs.Length > 1 ? commandArgs[1] : string.Empty,
                                                                  commandArgs.Length > 2
                                                                      ? ParseUInt(commandArgs[2])
                                                                      : null,
                                                                  cancellationToken);
                    new CreateInvoicePrinter().Print(invoice);
                    break;
                case "payinvoice":
                case "pay-invoice":
                case "pay":
                    var payOptions = ParsePayInvoiceOptions(commandArgs, out _)!;
                    var payment = await client.PayInvoiceAsync(payOptions.Bolt11, payOptions.Amount,
                                                               payOptions.TimeoutSeconds, payOptions.MaxFeeMsat,
                                                               payOptions.MaxParts, cancellationToken);
                    new PayInvoicePrinter().Print(payment);
                    if (payment.Payment.Status == PaymentStatus.Failed)
                        return Failure;
                    break;
                case "closechannel":
                case "close-channel":
                    var (closeFeerate, closeWait, noFeeRange) = ParseCloseOptions(commandArgs);
                    var close = await client.CloseChannelAsync(ParseChannelId(commandArgs[0]), closeFeerate,
                                                               noFeeRange, closeWait, cancellationToken);
                    new CloseChannelPrinter().Print(close);
                    break;
                case "forceclosechannel":
                case "force-close-channel":
                    var forceClose = await client.ForceCloseChannelAsync(ParseChannelId(commandArgs[0]),
                                                                         cancellationToken);
                    new ForceCloseChannelPrinter().Print(forceClose);
                    break;
                case "chainstatus":
                case "chain-status":
                    var chainStatus = await client.ChainStatusAsync(cancellationToken);
                    new ChainStatusPrinter().Print(chainStatus);
                    if (chainStatus.IsChainProcessingHalted)
                        return Failure;
                    break;
                case "listnodes":
                case "list-nodes":
                    var nodes = await client.ListNodesAsync(
                        commandArgs.Length > 0 && TryParseNodeId(commandArgs[0], out var onlyNode) ? onlyNode : null,
                        cancellationToken);
                    new ListNodesPrinter().Print(nodes);
                    break;
                case "listgraphchannels":
                case "list-graph-channels":
                    var (graphScid, graphNode) = ParseGraphChannelFilters(commandArgs);
                    var graphChannels = await client.ListGraphChannelsAsync(graphScid, graphNode, cancellationToken);
                    new ListGraphChannelsPrinter().Print(graphChannels);
                    break;
                case "pendingsweeps":
                case "pending-sweeps":
                    var (sweepChannel, includeClosed) = ParsePendingSweepsOptions(commandArgs);
                    var sweeps = await client.PendingSweepsAsync(sweepChannel, includeClosed, cancellationToken);
                    new PendingSweepsPrinter().Print(sweeps);
                    break;
                case "listinvoices":
                case "list-invoices":
                    var (invoiceTake, invoiceSkip) = ParsePage(commandArgs);
                    var invoices = await client.ListInvoicesAsync(invoiceSkip, invoiceTake, cancellationToken);
                    new ListInvoicesPrinter().Print(invoices);
                    break;
                case "listpayments":
                case "list-payments":
                    var (paymentTake, paymentSkip) = ParsePage(commandArgs);
                    var payments = await client.ListPaymentsAsync(paymentSkip, paymentTake, cancellationToken);
                    new ListPaymentsPrinter().Print(payments);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return Failure;
        }

        return Success;
    }

    /// <summary>
    /// Checks the command exists and has the arguments it needs.
    /// </summary>
    /// <returns>An error message, or null when the arguments are valid.</returns>
    internal static string? ValidateArguments(string cmd, string[] commandArgs)
    {
        switch (cmd)
        {
            case "info":
            case "node-info":
            case "listpeers":
            case "list-peers":
            case "listchannels":
            case "list-channels":
            case "getaddress":
            case "get-address":
            case "walletbalance":
            case "wallet-balance":
            case "chainstatus":
            case "chain-status":
                return null;
            case "connect":
            case "connect-peer":
                return commandArgs.Length < 1 ? $"Missing argument. Usage: {cmd} <node>" : null;
            case "openchannel":
            case "open-channel":
                var openArgs = OpenChannelMessageHandler.ParseArguments(commandArgs, out _, out var openError);
                if (openError is not null)
                    return openError;
                if (openArgs.Length < 2)
                    return $"Missing arguments. Usage: {cmd} {OpenChannelMessageHandler.Usage}";
                if (!ulong.TryParse(openArgs[1], NumberStyles.None, CultureInfo.InvariantCulture, out var fundingSats)
                 || fundingSats == 0 || fundingSats > MaxOpenChannelSats)
                    return $"Invalid amount '{openArgs[1]}': expected a positive number of sats up to {MaxOpenChannelSats}.";
                if (openArgs.Length > 2
                 && !(ulong.TryParse(openArgs[2], NumberStyles.None, CultureInfo.InvariantCulture, out var pushSats)
                   && pushSats < fundingSats))
                    return $"Invalid push '{openArgs[2]}': expected a number of sats below the channel amount.";
                return null;
            case "createinvoice":
            case "create-invoice":
            case "addinvoice":
                if (commandArgs.Length < 1)
                    return $"Missing argument. Usage: {cmd} <amount_msat|any> [description] [expiry_seconds]";
                if (!TryParseInvoiceAmount(commandArgs[0], out _))
                    return $"Invalid amount '{commandArgs[0]}': expected a positive number of msat or 'any'.";
                if (commandArgs.Length > 2 && !TryParsePositiveUInt(commandArgs[2], out _))
                    return $"Invalid expiry '{commandArgs[2]}': expected a positive number of seconds.";
                return null;
            case "payinvoice":
            case "pay-invoice":
            case "pay":
                if (commandArgs.Length < 1)
                    return $"Missing argument. Usage: {cmd} <bolt11> [amount_msat] [timeout_seconds] "
                         + "[--max-fee-msat <msat>] [--max-parts <n>] [--timeout <seconds>]";
                return ParsePayInvoiceOptions(commandArgs, out var payError) is null ? payError : null;
            case "closechannel":
            case "close-channel":
                if (commandArgs.Length < 1)
                    return $"Missing argument. Usage: {cmd} <channel_id> [feerate_per_kw|0] [wait_seconds] [nofeerange]";
                if (!TryParseChannelId(commandArgs[0], out _))
                    return $"Invalid channel id '{commandArgs[0]}': expected 64 hex characters.";
                if (commandArgs.Length > 1 && !uint.TryParse(commandArgs[1], NumberStyles.None,
                                                             CultureInfo.InvariantCulture, out _))
                    return $"Invalid feerate '{commandArgs[1]}': expected sat/kw, or 0 for the node's estimate.";
                if (commandArgs.Length > 2
                 && !(uint.TryParse(commandArgs[2], NumberStyles.None, CultureInfo.InvariantCulture, out var wait)
                   && wait <= MaxCloseWaitSeconds))
                    return $"Invalid wait '{commandArgs[2]}': expected 0 to {MaxCloseWaitSeconds} seconds.";
                if (commandArgs.Length > 3 && !string.Equals(commandArgs[3], "nofeerange",
                                                             StringComparison.OrdinalIgnoreCase))
                    return $"Invalid option '{commandArgs[3]}': expected nofeerange.";
                return null;
            case "forceclosechannel":
            case "force-close-channel":
                if (commandArgs.Length < 1)
                    return $"Missing argument. Usage: {cmd} <channel_id>";
                return TryParseChannelId(commandArgs[0], out _)
                           ? null
                           : $"Invalid channel id '{commandArgs[0]}': expected 64 hex characters.";
            case "listnodes":
            case "list-nodes":
                if (commandArgs.Length > 1)
                    return $"Too many arguments. Usage: {cmd} [node_id]";
                return commandArgs.Length == 1 && !TryParseNodeId(commandArgs[0], out _)
                           ? $"Invalid node id '{commandArgs[0]}': expected 66 hex characters."
                           : null;
            case "listgraphchannels":
            case "list-graph-channels":
                foreach (var argument in commandArgs)
                {
                    if (!TryParseShortChannelId(argument, out _) && !TryParseNodeId(argument, out _))
                        return $"Invalid argument '{argument}': expected a short channel id (BLOCKxTXxOUTPUT) or a "
                             + "node id (66 hex characters).";
                }

                return null;
            case "pendingsweeps":
            case "pending-sweeps":
                foreach (var argument in commandArgs)
                {
                    if (!string.Equals(argument, "all", StringComparison.OrdinalIgnoreCase)
                     && !TryParseChannelId(argument, out _))
                        return $"Invalid argument '{argument}': expected a channel id (64 hex characters) or all.";
                }

                return null;
            case "listinvoices":
            case "list-invoices":
            case "listpayments":
            case "list-payments":
                if (commandArgs.Length > 0
                 && !(TryParsePositiveInt(commandArgs[0], out var count) && count <= MaxListCount))
                    return $"Invalid count '{commandArgs[0]}': expected a number from 1 to {MaxListCount}.";
                if (commandArgs.Length > 1 && !int.TryParse(commandArgs[1], NumberStyles.None,
                                                            CultureInfo.InvariantCulture, out _))
                    return $"Invalid skip '{commandArgs[1]}': expected a number.";
                return null;
            default:
                return $"Unknown command: {cmd}";
        }
    }

    /// <summary>
    /// Parses an amount in msat; <c>any</c> means no amount. <c>0</c> is rejected: use <c>any</c> for an
    /// any-amount invoice.
    /// </summary>
    internal static bool TryParseInvoiceAmount(string value, out LightningMoney? amount)
    {
        amount = null;
        if (string.Equals(value, "any", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var msat) || msat == 0)
            return false;

        amount = LightningMoney.MilliSatoshis(msat);
        return true;
    }

    private static LightningMoney? ParseInvoiceAmount(string value) =>
        TryParseInvoiceAmount(value, out var amount)
            ? amount
            : throw new ArgumentException($"Invalid amount '{value}'.", nameof(value));

    private static bool TryParsePositiveUInt(string value, out uint result) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;

    private static bool TryParsePositiveInt(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;

    private static uint ParseUInt(string value) =>
        TryParsePositiveUInt(value, out var result)
            ? result
            : throw new ArgumentException($"Invalid number '{value}'.", nameof(value));

    /// <summary>A channel id: 64 hex characters, as listchannels prints it.</summary>
    internal static bool TryParseChannelId(string value, out ChannelId channelId)
    {
        channelId = default;
        if (value.Length != 64)
            return false;

        try
        {
            channelId = new ChannelId(Convert.FromHexString(value));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A node id: 66 hex characters of a compressed public key (02 or 03 first).</summary>
    internal static bool TryParseNodeId(string value, out CompactPubKey nodeId)
    {
        nodeId = default;
        if (value.Length != 66 || !(value.StartsWith("02", StringComparison.Ordinal)
                                 || value.StartsWith("03", StringComparison.Ordinal)))
            return false;

        try
        {
            nodeId = new CompactPubKey(Convert.FromHexString(value));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// A short channel id as <c>BLOCKxTXxOUTPUT</c> (as listgraphchannels prints it), as a number.
    /// </summary>
    internal static bool TryParseShortChannelId(string value, out ulong shortChannelId)
    {
        shortChannelId = 0;
        var parts = value.Split('x');
        if (parts.Length != 3
         || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var block) || block > 0xFFFFFF
         || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var tx) || tx > 0xFFFFFF
         || !ushort.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var output))
            return false;

        shortChannelId = ((ulong)block << 40) | ((ulong)tx << 16) | output;
        return true;
    }

    /// <summary>
    /// <c>[short_channel_id] [node_id]</c> of listgraphchannels, in any order.
    /// </summary>
    internal static (ulong? ShortChannelId, CompactPubKey? NodeId) ParseGraphChannelFilters(string[] commandArgs)
    {
        ulong? shortChannelId = null;
        CompactPubKey? nodeId = null;
        foreach (var argument in commandArgs)
        {
            if (TryParseShortChannelId(argument, out var scid))
                shortChannelId = scid;
            else if (TryParseNodeId(argument, out var node))
                nodeId = node;
        }

        return (shortChannelId, nodeId);
    }

    private static ChannelId ParseChannelId(string value) =>
        TryParseChannelId(value, out var channelId)
            ? channelId
            : throw new ArgumentException($"Invalid channel id '{value}'.", nameof(value));

    /// <summary>
    /// <c>[feerate_per_kw|0] [wait_seconds] [nofeerange]</c> of closechannel: a feerate of 0 (or none) uses the node's
    /// estimate, no wait uses the daemon's default.
    /// </summary>
    internal static (uint? FeeRatePerKw, uint? WaitSeconds, bool NoFeeRange) ParseCloseOptions(string[] commandArgs)
    {
        uint? feerate = commandArgs.Length > 1 && TryParsePositiveUInt(commandArgs[1], out var f) ? f : null;
        uint? wait = commandArgs.Length > 2
                  && uint.TryParse(commandArgs[2], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
                         ? w
                         : null;
        var noFeeRange = commandArgs.Length > 3
                      && string.Equals(commandArgs[3], "nofeerange", StringComparison.OrdinalIgnoreCase);
        return (feerate, wait, noFeeRange);
    }

    /// <summary>
    /// The arguments of payinvoice: <c>&lt;bolt11&gt; [amount_msat|any] [timeout_seconds]</c> positionally, and the
    /// options <c>--max-fee-msat &lt;msat&gt;</c> (the per-call fee limit, 0 for fee-free routes only, NL-270),
    /// <c>--max-parts &lt;n&gt;</c> (1 to <see cref="MaxPayParts"/>; 1 never splits) and <c>--timeout &lt;seconds&gt;</c>
    /// (1 to <see cref="MaxPayTimeoutSeconds"/>, instead of the positional timeout), each also as
    /// <c>--option=value</c>, anywhere after the command.
    /// </summary>
    /// <returns>The arguments, or null with <paramref name="error"/> set.</returns>
    internal static PayInvoiceArguments? ParsePayInvoiceOptions(string[] commandArgs, out string? error)
    {
        var positional = new List<string>();
        ulong? maxFeeMsat = null;
        uint? maxParts = null;
        uint? timeout = null;
        for (var i = 0; i < commandArgs.Length; i++)
        {
            var argument = commandArgs[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(argument);
                continue;
            }

            var separator = argument.IndexOf('=');
            var name = separator < 0 ? argument : argument[..separator];
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

            switch (name.ToLowerInvariant())
            {
                case "--max-fee-msat":
                    if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var fee)
                     || fee > MaxPayFeeMsat)
                    {
                        error = $"Invalid fee limit '{value}': expected a number of msat from 0 to {MaxPayFeeMsat}.";
                        return null;
                    }

                    maxFeeMsat = fee;
                    break;
                case "--max-parts":
                    if (!TryParsePositiveUInt(value, out var parts) || parts > MaxPayParts)
                    {
                        error = $"Invalid part limit '{value}': expected 1 to {MaxPayParts}.";
                        return null;
                    }

                    maxParts = parts;
                    break;
                case "--timeout":
                    if (!TryParsePositiveUInt(value, out var seconds) || seconds > MaxPayTimeoutSeconds)
                    {
                        error = $"Invalid timeout '{value}': expected 1 to {MaxPayTimeoutSeconds} seconds.";
                        return null;
                    }

                    timeout = seconds;
                    break;
                default:
                    error = $"Unknown option '{name}': expected --max-fee-msat, --max-parts or --timeout.";
                    return null;
            }
        }

        if (positional.Count is < 1 or > 3)
        {
            error = positional.Count == 0
                        ? "Missing argument: the invoice."
                        : $"Unexpected argument '{positional[3]}'.";
            return null;
        }

        LightningMoney? amount = null;
        if (positional.Count > 1 && !TryParseInvoiceAmount(positional[1], out amount))
        {
            error = $"Invalid amount '{positional[1]}': expected a positive number of msat or 'any'.";
            return null;
        }

        if (positional.Count > 2)
        {
            if (timeout is not null)
            {
                error = "Give the timeout either as the third argument or with --timeout, not both.";
                return null;
            }

            if (!(TryParsePositiveUInt(positional[2], out var positionalTimeout)
               && positionalTimeout <= MaxPayTimeoutSeconds))
            {
                error = $"Invalid timeout '{positional[2]}': expected 1 to {MaxPayTimeoutSeconds} seconds.";
                return null;
            }

            timeout = positionalTimeout;
        }

        error = null;
        return new PayInvoiceArguments(positional[0], amount, timeout, maxFeeMsat, maxParts);
    }

    /// <summary>
    /// <c>[channel_id] [all]</c> of pendingsweeps, in any order: one channel only, and the closed channels too.
    /// </summary>
    internal static (ChannelId? ChannelId, bool IncludeClosed) ParsePendingSweepsOptions(string[] commandArgs)
    {
        ChannelId? channelId = null;
        var includeClosed = false;
        foreach (var argument in commandArgs)
        {
            if (string.Equals(argument, "all", StringComparison.OrdinalIgnoreCase))
                includeClosed = true;
            else if (TryParseChannelId(argument, out var parsed))
                channelId = parsed;
        }

        return (channelId, includeClosed);
    }

    /// <summary>
    /// <c>[take] [skip]</c> of the list commands; take defaults to 100 and skip to 0.
    /// </summary>
    internal static (int Take, int Skip) ParsePage(string[] commandArgs)
    {
        var take = commandArgs.Length > 0 && TryParsePositiveInt(commandArgs[0], out var t) ? t : 100;
        var skip = commandArgs.Length > 1
                && int.TryParse(commandArgs[1], NumberStyles.None, CultureInfo.InvariantCulture, out var s)
                       ? s
                       : 0;
        return (take, skip);
    }
}

/// <summary>
/// The parsed arguments of payinvoice.
/// </summary>
internal sealed record PayInvoiceArguments(
    string Bolt11,
    LightningMoney? Amount,
    uint? TimeoutSeconds,
    ulong? MaxFeeMsat,
    uint? MaxParts);