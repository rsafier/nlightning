using System.Globalization;

namespace NLightning.Client.Handlers;

using Domain.Crypto.ValueObjects;
using Ipc;
using Printers;

/// <summary>
/// The <c>waitinvoice|wait-invoice &lt;payment_hash&gt; [--timeout &lt;seconds&gt;]</c> command of the CLI (ClientCommand
/// 47, Cashu plan C0, NL-991).
/// </summary>
internal static class WaitInvoiceCommands
{
    /// <summary>The usage of waitinvoice.</summary>
    internal const string Usage = "<payment_hash> [--timeout <seconds>]";

    /// <summary>The longest wait the daemon accepts.</summary>
    internal const uint MaxTimeoutSeconds = 300;

    /// <summary>
    /// Checks the arguments of waitinvoice.
    /// </summary>
    /// <returns>An error message with the usage, or null when they are valid.</returns>
    internal static string? Validate(string cmd, string[] commandArgs) =>
        Parse(commandArgs, out var error) is null ? $"{error} Usage: {cmd} {Usage}" : null;

    /// <summary>
    /// Runs a validated waitinvoice and prints its result.
    /// </summary>
    /// <returns>False when the wait timed out with the invoice still open (exit code 1).</returns>
    internal static async Task<bool> RunAsync(string[] commandArgs, NamedPipeIpcClient client,
                                              CancellationToken cancellationToken)
    {
        var (paymentHash, timeoutSeconds) = Parse(commandArgs, out _)!.Value;
        var response = await client.WaitInvoiceAsync(paymentHash, timeoutSeconds, cancellationToken);
        new WaitInvoicePrinter().Print(response);
        return !response.TimedOut;
    }

    /// <summary>
    /// <c>&lt;payment_hash&gt; [--timeout &lt;seconds&gt;]</c>, the option anywhere.
    /// </summary>
    /// <returns>The arguments, or null with <paramref name="error"/> set.</returns>
    internal static (Hash PaymentHash, uint? TimeoutSeconds)? Parse(string[] commandArgs, out string? error)
    {
        error = null;
        Hash? paymentHash = null;
        uint? timeoutSeconds = null;
        for (var i = 0; i < commandArgs.Length; i++)
        {
            var argument = commandArgs[i];
            if (argument == "--timeout")
            {
                if (i + 1 >= commandArgs.Length
                 || !uint.TryParse(commandArgs[i + 1], NumberStyles.None, CultureInfo.InvariantCulture,
                                   out var seconds)
                 || seconds is 0 or > MaxTimeoutSeconds)
                {
                    error = $"--timeout needs a number of seconds from 1 to {MaxTimeoutSeconds}.";
                    return null;
                }

                timeoutSeconds = seconds;
                i++;
                continue;
            }

            if (paymentHash is not null)
            {
                error = $"Unexpected argument '{argument}'.";
                return null;
            }

            if (argument.Length != 64 || !TryParseHex(argument, out var bytes))
            {
                error = $"Invalid payment hash '{argument}': expected 64 hex characters.";
                return null;
            }

            paymentHash = new Hash(bytes);
        }

        if (paymentHash is null)
        {
            error = "Missing the payment hash.";
            return null;
        }

        return (paymentHash.Value, timeoutSeconds);
    }

    private static bool TryParseHex(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}