using System.Globalization;

namespace NLightning.Client;

using Daemon.Contracts.Helpers;
using Daemon.Contracts.Utilities;
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
                    var payment = await client.PayInvoiceAsync(commandArgs[0],
                                                               commandArgs.Length > 1
                                                                   ? ParseInvoiceAmount(commandArgs[1])
                                                                   : null,
                                                               commandArgs.Length > 2 ? ParseUInt(commandArgs[2]) : null,
                                                               cancellationToken);
                    new PayInvoicePrinter().Print(payment);
                    if (payment.Payment.Status == PaymentStatus.Failed)
                        return Failure;
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
                return null;
            case "connect":
            case "connect-peer":
                return commandArgs.Length < 1 ? $"Missing argument. Usage: {cmd} <node>" : null;
            case "openchannel":
            case "open-channel":
                return commandArgs.Length < 2 ? $"Missing arguments. Usage: {cmd} <node> <amount_sats>" : null;
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
                    return $"Missing argument. Usage: {cmd} <bolt11> [amount_msat] [timeout_seconds]";
                if (commandArgs.Length > 1 && !TryParseInvoiceAmount(commandArgs[1], out _))
                    return $"Invalid amount '{commandArgs[1]}': expected a positive number of msat or 'any'.";
                if (commandArgs.Length > 2
                 && !(TryParsePositiveUInt(commandArgs[2], out var timeout) && timeout <= MaxPayTimeoutSeconds))
                    return $"Invalid timeout '{commandArgs[2]}': expected 1 to {MaxPayTimeoutSeconds} seconds.";
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