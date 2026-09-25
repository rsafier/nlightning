namespace NLightning.Client;

using Daemon.Contracts.Helpers;
using Daemon.Contracts.Utilities;
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
            default:
                return $"Unknown command: {cmd}";
        }
    }
}