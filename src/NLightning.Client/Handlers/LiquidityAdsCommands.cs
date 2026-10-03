using System.Globalization;

namespace NLightning.Client.Handlers;

using Domain.Client.Enums;
using Domain.LiquidityAds.Enums;
using Ipc;
using Printers;
using Transport.Ipc.Requests;

/// <summary>
/// <c>liquidityads|liquidity-ads &lt;rates|sellers|purchases&gt;</c> (ClientCommand 46, liquidity ads NL-850):
/// our rates, the sellers we know of (their <c>init</c> and <c>node_announcement</c>) and the liquidity we bought and
/// sold with its lease (<c>purchases [--role buyer|seller] [--status pending|active|replaced|closed] [--skip &lt;n&gt;]
/// [--limit &lt;n&gt;]</c>, options also as <c>--option=value</c>).
/// </summary>
internal static class LiquidityAdsCommands
{
    /// <summary>The usage of liquidityads.</summary>
    internal const string Usage =
        "<rates|sellers|purchases> [--role buyer|seller] [--status pending|active|replaced|closed] [--skip <n>] "
      + "[--limit <n>]";

    /// <summary>The page size when <c>--limit</c> is left out.</summary>
    internal const int DefaultLimit = 25;

    /// <summary>Whether <paramref name="cmd"/> is liquidityads.</summary>
    internal static bool IsLiquidityAds(string cmd) => cmd is "liquidityads" or "liquidity-ads";

    /// <summary>Checks the arguments of liquidityads.</summary>
    /// <returns>An error message with the usage, or null when they are valid.</returns>
    internal static string? Validate(string cmd, string[] commandArgs) =>
        Parse(commandArgs, out var error) is null ? $"{error} Usage: {cmd} {Usage}" : null;

    /// <summary>Runs a validated liquidityads and prints its result.</summary>
    internal static async Task RunAsync(string[] commandArgs, NamedPipeIpcClient client, TextWriter output,
                                        CancellationToken cancellationToken)
    {
        var request = Parse(commandArgs, out _)!;
        new LiquidityAdsPrinter(output).Print(await client.LiquidityAdsAsync(request, cancellationToken));
    }

    /// <summary>
    /// The action and, for <c>purchases</c>, its filters and page.
    /// </summary>
    /// <returns>The request, or null with <paramref name="error"/> set.</returns>
    internal static LiquidityAdsIpcRequest? Parse(string[] commandArgs, out string? error)
    {
        error = null;
        if (commandArgs.Length == 0)
        {
            error = "Missing the action.";
            return null;
        }

        LiquidityAdsAction action;
        switch (commandArgs[0].ToLowerInvariant())
        {
            case "rates":
                action = LiquidityAdsAction.Rates;
                break;
            case "sellers":
                action = LiquidityAdsAction.Sellers;
                break;
            case "purchases":
                action = LiquidityAdsAction.Purchases;
                break;
            default:
                error = $"Unknown action '{commandArgs[0]}'.";
                return null;
        }

        LiquidityPurchaseRole? role = null;
        LiquidityPurchaseStatus? status = null;
        var skip = 0;
        var limit = DefaultLimit;
        for (var i = 1; i < commandArgs.Length; i++)
        {
            var argument = commandArgs[i];
            if (action != LiquidityAdsAction.Purchases || !argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unexpected argument '{argument}'.";
                return null;
            }

            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? argument : argument[..separator];
            if (name is not ("--role" or "--status" or "--skip" or "--limit"))
            {
                error = $"Unknown option '{argument}'.";
                return null;
            }

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

            switch (name)
            {
                case "--role":
                    role = value.ToLowerInvariant() switch
                    {
                        "buyer" or "bought" => LiquidityPurchaseRole.Buyer,
                        "seller" or "sold" => LiquidityPurchaseRole.Seller,
                        _ => null
                    };
                    if (role is null)
                    {
                        error = $"Invalid role '{value}': expected buyer or seller.";
                        return null;
                    }

                    break;
                case "--status":
                    status = value.ToLowerInvariant() switch
                    {
                        "pending" => LiquidityPurchaseStatus.Pending,
                        "active" => LiquidityPurchaseStatus.Active,
                        "replaced" => LiquidityPurchaseStatus.Replaced,
                        "closed" => LiquidityPurchaseStatus.Closed,
                        _ => null
                    };
                    if (status is null)
                    {
                        error = $"Invalid status '{value}': expected pending, active, replaced or closed.";
                        return null;
                    }

                    break;
                case "--skip":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out skip))
                    {
                        error = $"Invalid skip '{value}': expected a number.";
                        return null;
                    }

                    break;
                default:
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                     || limit < 1 || limit > ClientApp.MaxListCount)
                    {
                        error = $"Invalid limit '{value}': expected a number from 1 to {ClientApp.MaxListCount}.";
                        return null;
                    }

                    break;
            }
        }

        return new LiquidityAdsIpcRequest
        {
            Action = action,
            Role = role,
            Status = status,
            Skip = skip,
            Take = limit
        };
    }
}