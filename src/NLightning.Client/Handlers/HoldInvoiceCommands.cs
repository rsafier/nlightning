using System.Globalization;

namespace NLightning.Client.Handlers;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Ipc;
using Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The hold invoice verbs of the CLI (NL-995, Cashu plan C4):
/// <c>createholdinvoice|create-hold-invoice &lt;payment_hash&gt; [amount_msat|any] [description]
/// [--expiry &lt;seconds&gt;]</c>, <c>settleholdinvoice|settle-hold-invoice &lt;payment_hash&gt; &lt;preimage&gt;</c>
/// and <c>cancelholdinvoice|cancel-hold-invoice &lt;payment_hash&gt;</c>. The node does not generate the preimage of a
/// hold invoice: the operator must be able to produce a preimage whose SHA-256 is the payment hash (the NUT-14/ASP
/// flow), then settle with it; the amount is in msat like createinvoice.
/// </summary>
internal static class HoldInvoiceCommands
{
    /// <summary>The usage of createholdinvoice.</summary>
    internal const string CreateUsage = "<payment_hash> [amount_msat|any] [description] [--expiry <seconds>]";

    /// <summary>The usage of settleholdinvoice.</summary>
    internal const string SettleUsage = "<payment_hash> <preimage>";

    /// <summary>The usage of cancelholdinvoice.</summary>
    internal const string CancelUsage = "<payment_hash>";

    /// <summary>
    /// Checks the arguments of the three verbs.
    /// </summary>
    /// <returns>An error message with the verb's usage, or null when they are valid.</returns>
    internal static string? Validate(string cmd, string[] commandArgs) =>
        cmd is "settleholdinvoice" or "settle-hold-invoice"
            ? ParseSettle(commandArgs, out var settleError) is null
                ? $"{settleError} Usage: {cmd} {SettleUsage}"
                : null
            : cmd is "cancelholdinvoice" or "cancel-hold-invoice"
                ? ParseCancel(commandArgs, out var cancelError) is null
                    ? $"{cancelError} Usage: {cmd} {CancelUsage}"
                    : null
                : ParseCreate(commandArgs, out var createError) is null
                    ? $"{createError} Usage: {cmd} {CreateUsage}"
                    : null;

    /// <summary>
    /// Runs a validated hold invoice verb and prints the invoice it answers with.
    /// </summary>
    internal static async Task RunAsync(string cmd, string[] commandArgs, NamedPipeIpcClient client,
                                        CancellationToken cancellationToken, LabelArguments? labels = null)
    {
        HoldInvoiceIpcResponse response;
        switch (cmd)
        {
            case "settleholdinvoice":
            case "settle-hold-invoice":
                var (settleHash, preimage) = ParseSettle(commandArgs, out _)!.Value;
                response = await client.SettleHoldInvoiceAsync(settleHash, preimage, cancellationToken);
                break;
            case "cancelholdinvoice":
            case "cancel-hold-invoice":
                response = await client.CancelHoldInvoiceAsync(ParseCancel(commandArgs, out _)!.Value,
                                                               cancellationToken);
                break;
            default:
                var (paymentHash, amount, description, expirySeconds) = ParseCreate(commandArgs, out _)!.Value;
                response = await client.CreateHoldInvoiceAsync(paymentHash, amount, description, expirySeconds,
                                                               cancellationToken, labels);
                break;
        }

        new HoldInvoicePrinter().Print(response);
    }

    /// <summary>
    /// <c>&lt;payment_hash&gt; [amount_msat|any] [description]</c> positionally and the option
    /// <c>--expiry &lt;seconds&gt;</c> (also as <c>--expiry=&lt;seconds&gt;</c>, anywhere after the command) of
    /// createholdinvoice; the amount follows createinvoice's convention: a positive number of msat, or <c>any</c> (or
    /// no second argument) for an invoice that accepts any amount, never <c>0</c>.
    /// </summary>
    /// <returns>The arguments, or null with <paramref name="error"/> set.</returns>
    internal static (Hash PaymentHash, LightningMoney? Amount, string Description, uint? ExpirySeconds)? ParseCreate(
        string[] commandArgs, out string? error)
    {
        error = null;
        uint? expirySeconds = null;
        var positional = new List<string>();
        for (var i = 0; i < commandArgs.Length; i++)
        {
            var argument = commandArgs[i];
            string? value = null;
            if (string.Equals(argument, "--expiry", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= commandArgs.Length)
                {
                    error = "Missing value for --expiry.";
                    return null;
                }

                value = commandArgs[++i];
            }
            else if (argument.StartsWith("--expiry=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument["--expiry=".Length..];
            }
            else if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unknown option '{argument}'.";
                return null;
            }
            else
            {
                positional.Add(argument);
                continue;
            }

            if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds == 0)
            {
                error = $"Invalid expiry '{value}': expected a positive number of seconds.";
                return null;
            }

            expirySeconds = seconds;
        }

        if (positional.Count < 1)
        {
            error = "Missing argument: the payment hash.";
            return null;
        }

        if (positional.Count > 3)
        {
            error = $"Unexpected argument '{positional[3]}'.";
            return null;
        }

        if (!TryParsePaymentHash(positional[0], out var paymentHash))
        {
            error = $"Invalid payment hash '{positional[0]}': expected 64 hex characters.";
            return null;
        }

        LightningMoney? amount = null;
        if (positional.Count > 1
         && !string.Equals(positional[1], "any", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(positional[1], NumberStyles.None, CultureInfo.InvariantCulture, out var msat)
             || msat == 0)
            {
                error = $"Invalid amount '{positional[1]}': expected a positive number of msat or 'any'.";
                return null;
            }

            amount = LightningMoney.MilliSatoshis(msat);
        }

        return (paymentHash, amount, positional.Count > 2 ? positional[2] : string.Empty, expirySeconds);
    }

    /// <summary>
    /// <c>&lt;payment_hash&gt; &lt;preimage&gt;</c> of settleholdinvoice, both 64 hex characters; the preimage must
    /// hash to the payment hash (the daemon checks).
    /// </summary>
    /// <returns>The arguments, or null with <paramref name="error"/> set.</returns>
    internal static (Hash PaymentHash, Secret Preimage)? ParseSettle(string[] commandArgs, out string? error)
    {
        if (commandArgs.Length < 2)
        {
            error = "Missing arguments: the payment hash and the preimage.";
            return null;
        }

        if (commandArgs.Length > 2)
        {
            error = $"Unexpected argument '{commandArgs[2]}'.";
            return null;
        }

        if (!TryParsePaymentHash(commandArgs[0], out var paymentHash))
        {
            error = $"Invalid payment hash '{commandArgs[0]}': expected 64 hex characters.";
            return null;
        }

        if (!TryParsePreimage(commandArgs[1], out var preimage))
        {
            error = $"Invalid preimage '{commandArgs[1]}': expected 64 hex characters.";
            return null;
        }

        error = null;
        return (paymentHash, preimage);
    }

    /// <summary>
    /// <c>&lt;payment_hash&gt;</c> of cancelholdinvoice: 64 hex characters.
    /// </summary>
    /// <returns>The arguments, or null with <paramref name="error"/> set.</returns>
    internal static Hash? ParseCancel(string[] commandArgs, out string? error)
    {
        if (commandArgs.Length < 1)
        {
            error = "Missing argument: the payment hash.";
            return null;
        }

        if (commandArgs.Length > 1)
        {
            error = $"Unexpected argument '{commandArgs[1]}'.";
            return null;
        }

        if (!TryParsePaymentHash(commandArgs[0], out var paymentHash))
        {
            error = $"Invalid payment hash '{commandArgs[0]}': expected 64 hex characters.";
            return null;
        }

        error = null;
        return paymentHash;
    }

    /// <summary>A payment hash: 64 hex characters, as the invoice listings print it.</summary>
    internal static bool TryParsePaymentHash(string value, out Hash paymentHash)
    {
        paymentHash = default;
        if (value.Length != 64 || !TryParseHex(value, out var bytes))
            return false;

        paymentHash = new Hash(bytes);
        return true;
    }

    private static bool TryParsePreimage(string value, out Secret preimage)
    {
        preimage = default;
        if (value.Length != 64 || !TryParseHex(value, out var bytes))
            return false;

        preimage = new Secret(bytes);
        return true;
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