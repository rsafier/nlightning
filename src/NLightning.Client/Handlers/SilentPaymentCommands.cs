using System.Globalization;

namespace NLightning.Client.Handlers;

using Domain.Client.Enums;
using Ipc;
using Printers;
using Transport.Ipc.Requests;

internal static class SilentPaymentCommands
{
    internal static string? Validate(string command, string[] args) =>
        Parse(command, args, out var error) is null ? error : null;

    internal static async Task RunAsync(string command, string[] args, NamedPipeIpcClient client, CancellationToken ct)
    {
        var request = Parse(command, args, out _)!;
        var response = await client.SilentPaymentAsync(Command(command), request, ct);
        new SilentPaymentPrinter().Print(response);
    }

    internal static SilentPaymentIpcRequest? Parse(string command, string[] args, out string? error)
    {
        error = null;
        string? label = null;
        uint? height = null;
        uint? labels = null;
        var cancel = false;
        var kind = Command(command);
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (kind == ClientCommand.SilentPaymentRescan && option == "--cancel" && !cancel)
            {
                cancel = true;
                continue;
            }
            if (kind == ClientCommand.GetSilentPaymentAddress && option == "--label" && label is null
             && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                label = args[++i];
                continue;
            }
            if (kind == ClientCommand.SilentPaymentRescan && option is "--from-height" or "--labels"
             && i + 1 < args.Length && uint.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                if (option == "--from-height" && height is null)
                    height = value;
                else if (option == "--labels" && labels is null && value <= 100_000)
                    labels = value;
                else
                    return Fail(command, out error);
                i++;
                continue;
            }
            return Fail(command, out error);
        }
        if (kind == ClientCommand.SilentPaymentRescan && (cancel ? height is not null || labels is not null : height is null))
            return Fail(command, out error);
        return new SilentPaymentIpcRequest { Label = label, FromHeight = height, RecoveryLabels = labels, Cancel = cancel };
    }

    private static SilentPaymentIpcRequest? Fail(string command, out string? error)
    {
        error = command switch
        {
            "getspaddress" => "Usage: getspaddress [--label <name>]",
            "sprescan" => "Usage: sprescan --from-height <height> [--labels <count>] | --cancel",
            _ => $"Usage: {command} (no arguments)"
        };
        return null;
    }

    private static ClientCommand Command(string command) => command switch
    {
        "getspaddress" => ClientCommand.GetSilentPaymentAddress,
        "splabels" => ClientCommand.SilentPaymentLabels,
        "sprescan" => ClientCommand.SilentPaymentRescan,
        "spstatus" => ClientCommand.SilentPaymentStatus,
        _ => throw new ArgumentException("Unknown silent payment command.", nameof(command))
    };
}