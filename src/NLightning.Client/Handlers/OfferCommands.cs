using System.Globalization;

namespace NLightning.Client.Handlers;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Ipc;
using Printers;
using Transport.Ipc.Requests;

/// <summary>
/// The parsed arguments of <c>createoffer</c>.
/// </summary>
internal sealed record CreateOfferArguments(LightningMoney? Amount, string? Description, string? Issuer,
                                            ulong? QuantityMax, ulong? AbsoluteExpiry, bool ForcePaths);

/// <summary>
/// The BOLT 12 offer commands of the CLI: <c>createoffer|create-offer</c>, <c>listoffers|list-offers</c> and
/// <c>disableoffer|disable-offer</c> (wave B12 lane D; provisional ClientCommand 26-28).
/// </summary>
internal static class OfferCommands
{
    /// <summary>The usage of createoffer.</summary>
    internal const string CreateOfferUsage =
        "<amount_msat|any> [description] [--issuer <text>] [--quantity-max <n>] [--absolute-expiry <unix_seconds>] "
      + "[--paths]";

    /// <summary>The usage of listoffers.</summary>
    internal const string ListOffersUsage = "[--active] [count] [skip]";

    /// <summary>The usage of disableoffer.</summary>
    internal const string DisableOfferUsage = "<offer_id>";

    /// <summary>
    /// Whether <paramref name="cmd"/> is one of the offer commands.
    /// </summary>
    internal static bool IsOfferCommand(string cmd) => cmd is "createoffer" or "create-offer" or "listoffers"
                                                                or "list-offers" or "disableoffer" or "disable-offer";

    /// <summary>
    /// Checks the arguments of an offer command.
    /// </summary>
    /// <returns>An error message with the usage, or null when they are valid.</returns>
    internal static string? Validate(string cmd, string[] commandArgs, int maxListCount)
    {
        switch (cmd)
        {
            case "createoffer":
            case "create-offer":
                return ParseCreateOfferOptions(commandArgs, out var error) is null
                           ? $"{error} Usage: {cmd} {CreateOfferUsage}"
                           : null;
            case "listoffers":
            case "list-offers":
                return ParseListOffersOptions(commandArgs, maxListCount, out var listError) is null
                           ? $"{listError} Usage: {cmd} {ListOffersUsage}"
                           : null;
            default:
                if (commandArgs.Length != 1)
                    return $"Missing or extra arguments. Usage: {cmd} {DisableOfferUsage}";
                return TryParseOfferId(commandArgs[0], out _)
                           ? null
                           : $"Invalid offer id '{commandArgs[0]}': expected 64 hex characters.";
        }
    }

    /// <summary>
    /// Runs a validated offer command and prints its result.
    /// </summary>
    internal static async Task RunAsync(string cmd, string[] commandArgs, int maxListCount, NamedPipeIpcClient client,
                                        CancellationToken cancellationToken)
    {
        switch (cmd)
        {
            case "createoffer":
            case "create-offer":
                var create = ParseCreateOfferOptions(commandArgs, out _)!;
                var created = await client.CreateOfferAsync(new CreateOfferIpcRequest
                {
                    Amount = create.Amount,
                    Description = create.Description,
                    Issuer = create.Issuer,
                    QuantityMax = create.QuantityMax,
                    AbsoluteExpiry = create.AbsoluteExpiry,
                    ForcePaths = create.ForcePaths
                }, cancellationToken);
                new CreateOfferPrinter().Print(created);
                break;
            case "listoffers":
            case "list-offers":
                var (activeOnly, take, skip) = ParseListOffersOptions(commandArgs, maxListCount, out _)!.Value;
                new ListOffersPrinter().Print(await client.ListOffersAsync(activeOnly, skip, take, cancellationToken));
                break;
            default:
                TryParseOfferId(commandArgs[0], out var offerId);
                new DisableOfferPrinter().Print(await client.DisableOfferAsync(offerId, cancellationToken));
                break;
        }
    }

    /// <summary>
    /// <c>&lt;amount_msat|any&gt; [description] [--issuer &lt;text&gt;] [--quantity-max &lt;n&gt;]
    /// [--absolute-expiry &lt;unix_seconds&gt;] [--paths]</c>; options also as <c>--option=value</c>, anywhere. An amount
    /// needs a description (BOLT 12 offer writer).
    /// </summary>
    /// <returns>The arguments, or null with <paramref name="error"/> set.</returns>
    internal static CreateOfferArguments? ParseCreateOfferOptions(string[] commandArgs, out string? error)
    {
        error = null;
        var positional = new List<string>();
        string? issuer = null;
        ulong? quantityMax = null;
        ulong? absoluteExpiry = null;
        var forcePaths = false;
        for (var i = 0; i < commandArgs.Length; i++)
        {
            var argument = commandArgs[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(argument);
                continue;
            }

            var (name, inlineValue) = SplitOption(argument);
            if (name == "--paths")
            {
                if (inlineValue is not null)
                {
                    error = "--paths takes no value.";
                    return null;
                }

                forcePaths = true;
                continue;
            }

            if (name is not ("--issuer" or "--quantity-max" or "--absolute-expiry"))
            {
                error = $"Unknown option '{name}'.";
                return null;
            }

            var value = inlineValue ?? (i + 1 < commandArgs.Length ? commandArgs[++i] : null);
            if (value is null)
            {
                error = $"{name} needs a value.";
                return null;
            }

            switch (name)
            {
                case "--issuer":
                    issuer = value;
                    break;
                case "--quantity-max":
                    if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var max))
                    {
                        error = $"Invalid quantity maximum '{value}': expected a number (0 for unlimited).";
                        return null;
                    }

                    quantityMax = max;
                    break;
                default:
                    if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var expiry)
                     || expiry == 0)
                    {
                        error = $"Invalid absolute expiry '{value}': expected seconds since 1970.";
                        return null;
                    }

                    absoluteExpiry = expiry;
                    break;
            }
        }

        if (positional.Count is 0 or > 2)
        {
            error = positional.Count == 0 ? "Missing argument." : "Too many arguments.";
            return null;
        }

        if (!ClientApp.TryParseInvoiceAmount(positional[0], out var amount))
        {
            error = $"Invalid amount '{positional[0]}': expected a positive number of msat or 'any'.";
            return null;
        }

        var description = positional.Count > 1 ? positional[1] : null;
        if (amount is not null && string.IsNullOrEmpty(description))
        {
            error = "An offer with an amount needs a description.";
            return null;
        }

        return new CreateOfferArguments(amount, description, issuer, quantityMax, absoluteExpiry, forcePaths);
    }

    /// <summary>
    /// <c>[--active] [count] [skip]</c> of listoffers.
    /// </summary>
    internal static (bool ActiveOnly, int Take, int Skip)? ParseListOffersOptions(string[] commandArgs,
                                                                                  int maxListCount,
                                                                                  out string? error)
    {
        error = null;
        var activeOnly = false;
        var numbers = new List<int>();
        foreach (var argument in commandArgs)
        {
            if (argument == "--active")
            {
                activeOnly = true;
                continue;
            }

            if (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                error = $"Invalid argument '{argument}': expected --active or a number.";
                return null;
            }

            numbers.Add(number);
        }

        if (numbers.Count > 2)
        {
            error = "Too many arguments.";
            return null;
        }

        var take = numbers.Count > 0 ? numbers[0] : 100;
        if (take < 1 || take > maxListCount)
        {
            error = $"Invalid count '{take}': expected a number from 1 to {maxListCount}.";
            return null;
        }

        return (activeOnly, take, numbers.Count > 1 ? numbers[1] : 0);
    }

    /// <summary>An offer id: 64 hex characters, as listoffers prints it.</summary>
    internal static bool TryParseOfferId(string value, out Hash offerId)
    {
        offerId = default;
        if (value.Length != 64)
            return false;

        try
        {
            offerId = new Hash(Convert.FromHexString(value));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static (string Name, string? Value) SplitOption(string argument)
    {
        var equals = argument.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? (argument, null) : (argument[..equals], argument[(equals + 1)..]);
    }
}