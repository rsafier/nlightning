using System.Globalization;
using System.Text;

namespace NLightning.Client.Handlers;

using Domain.Accounting.Prices;
using Domain.Client.Enums;
using Printers;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>accounting prices import|list|fetch|replace</c> (ClientCommand 45, NL-602 A3-T2, NL-693). <c>import &lt;file&gt;</c> reads the
/// price file on the client's machine (<c>unixSeconds,price</c> per line, the format of the daemon's price file), refuses
/// it with every bad line listed by number, and sends it in requests of
/// <see cref="AccountingPricesIpcRequest.MaxRowsPerRequest"/> rows; <c>list</c> pages the stored prices with
/// <c>--since</c>; <c>fetch --since</c> asks the node's price sources for every hour of the range; <c>replace &lt;time&gt;
/// &lt;price&gt;</c> corrects the stored price of that time (to the second) and re-values what it priced, with
/// <c>--source</c> and <c>--note</c> for the audit trail.
/// </summary>
internal static class AccountingPricesCommands
{
    /// <summary>The usage of the <c>prices</c> subcommands.</summary>
    internal const string Usage =
        "accounting prices import <file> [--currency <code>] | accounting prices list [--since <time>] [--until "
      + "<time>] [--limit <n>] [--currency <code>] | accounting prices fetch --since <time> [--until <time>] | "
      + "accounting prices replace <time> <price> [--currency <code>] [--source <text>] [--note <text>]";

    /// <summary>The largest list page.</summary>
    internal const int MaxLimit = 1_000;

    /// <summary>The most bad lines an import error lists.</summary>
    internal const int MaxListedErrors = 20;

    /// <summary>Parses <c>prices &lt;import|list|fetch&gt; [...]</c> (the arguments after <c>prices</c>).</summary>
    internal static AccountingBooksCommands.AccountingArguments? Parse(string[] args, out string? error)
    {
        error = null;
        if (args.Length == 0)
        {
            error = "Missing prices subcommand: import, list, fetch or replace.";
            return null;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "import":
                return ParseImport(args[1..], out error);
            case "list":
                return ParseList(args[1..], out error);
            case "fetch":
                return ParseFetch(args[1..], out error);
            case "replace":
                return ParseReplace(args[1..], out error);
            default:
                error = $"Unknown prices subcommand '{args[0]}': expected import, list, fetch or replace.";
                return null;
        }
    }

    /// <summary>Runs a parsed <c>prices</c> command and prints its result.</summary>
    /// <exception cref="InvalidOperationException">The import file has bad lines (listed by number).</exception>
    internal static async Task RunAsync(
        AccountingBooksCommands.AccountingArguments arguments,
        Func<AccountingAdminIpcRequest, CancellationToken, Task<AccountingAdminIpcResponse>> send, TextWriter output,
        CancellationToken cancellationToken)
    {
        var request = arguments.Admin!;
        var printer = new AccountingPricesPrinter(output);
        if (arguments.PricesFile is not { } path)
        {
            printer.Print((await send(request, cancellationToken)).Prices!);
            return;
        }

        var parsed = await ReadFileAsync(path, cancellationToken);
        var rows = parsed.Points.Select(p => new AccountingPriceRowIpc
        {
            TimeUnixSeconds = p.Time.ToUnixTimeSeconds(),
            Price = p.Price.ToString(CultureInfo.InvariantCulture)
        }).ToList();

        // One request per chunk; the totals add up, the last valuation round is the one that saw every price
        int added = 0, kept = 0;
        AccountingPricesIpcResponse? last = null;
        foreach (var chunk in rows.Chunk(AccountingPricesIpcRequest.MaxRowsPerRequest))
        {
            var response = await send(new AccountingAdminIpcRequest
            {
                Action = request.Action,
                Prices = new AccountingPricesIpcRequest { Currency = request.Prices!.Currency, Rows = [.. chunk] }
            }, cancellationToken);
            last = response.Prices!;
            added += last.Import?.Added ?? 0;
            kept += last.Import?.AlreadyStored ?? 0;
        }

        printer.Print(new AccountingPricesIpcResponse
        {
            Currency = last!.Currency,
            Import = new AccountingPriceImportIpc
            {
                Added = added,
                AlreadyStored = kept,
                Valuation = last.Import?.Valuation
            }
        });
    }

    /// <summary>Reads and checks an import file; throws with the bad lines when there are any.</summary>
    internal static async Task<AccountingPriceCsvResult> ReadFileAsync(string path,
                                                                      CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not read {path}: {e.Message}", e);
        }

        var parsed = AccountingPriceCsv.Parse(text);
        if (!parsed.IsValid)
        {
            var listed = string.Join(Environment.NewLine,
                                     parsed.Errors.Take(MaxListedErrors).Select(e => $"  {path}: {e}"));
            var more = parsed.ErrorCount > MaxListedErrors
                           ? $"{Environment.NewLine}  ... and {parsed.ErrorCount - MaxListedErrors} more"
                           : string.Empty;
            throw new InvalidOperationException(
                $"{path} has {parsed.ErrorCount} bad line(s); nothing was imported:{Environment.NewLine}{listed}{more}");
        }

        if (parsed.Points.Count == 0)
            throw new InvalidOperationException($"{path} holds no prices.");

        return parsed;
    }

    private static AccountingBooksCommands.AccountingArguments? ParseImport(string[] args, out string? error)
    {
        error = null;
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            error = "Missing the price file: accounting prices import <file>.";
            return null;
        }

        var options = AccountingBooksCommands.ParseOptions(args[1..], ["--currency"], out error);
        if (options is null)
            return null;

        var prices = new AccountingPricesIpcRequest();
        if (!ApplyCurrency(options, prices, out error))
            return null;

        return new AccountingBooksCommands.AccountingArguments(
            "prices", Admin: new AccountingAdminIpcRequest
            {
                Action = (int)AccountingAdminAction.PricesImport,
                Prices = prices
            }, PricesFile: args[0]);
    }

    private static AccountingBooksCommands.AccountingArguments? ParseList(string[] args, out string? error)
    {
        var options = AccountingBooksCommands.ParseOptions(args, ["--since", "--until", "--limit", "--currency"],
                                                           out error);
        if (options is null)
            return null;

        var prices = new AccountingPricesIpcRequest();
        if (!ApplyCurrency(options, prices, out error) || !ApplyRange(options, prices, out error))
            return null;

        foreach (var (name, value) in options.Where(o => o.Name == "--limit"))
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
             || limit is < 1 or > MaxLimit)
            {
                error = $"Invalid {name.TrimStart('-')} '{value}': expected a number from 1 to {MaxLimit}.";
                return null;
            }

            prices.Limit = limit;
        }

        return new AccountingBooksCommands.AccountingArguments(
            "prices", Admin: new AccountingAdminIpcRequest
            {
                Action = (int)AccountingAdminAction.PricesList,
                Prices = prices
            });
    }

    private static AccountingBooksCommands.AccountingArguments? ParseFetch(string[] args, out string? error)
    {
        var options = AccountingBooksCommands.ParseOptions(args, ["--since", "--until"], out error);
        if (options is null)
            return null;

        var prices = new AccountingPricesIpcRequest();
        if (!ApplyRange(options, prices, out error))
            return null;

        if (prices.SinceUnixSeconds is null)
        {
            error = "Missing --since: accounting prices fetch --since <time> [--until <time>].";
            return null;
        }

        return new AccountingBooksCommands.AccountingArguments(
            "prices", Admin: new AccountingAdminIpcRequest
            {
                Action = (int)AccountingAdminAction.PricesFetch,
                Prices = prices
            });
    }

    private static AccountingBooksCommands.AccountingArguments? ParseReplace(string[] args, out string? error)
    {
        error = null;
        if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal)
                            || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            error = "Missing the time or the price: accounting prices replace <time> <price> [--currency <code>] "
                  + "[--source <text>] [--note <text>].";
            return null;
        }

        if (!AccountingBooksCommands.TryParseTime("time", args[0], out var seconds, out error))
            return null;

        if (!decimal.TryParse(args[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var price)
         || price <= 0)
        {
            error = $"Invalid price '{args[1]}': expected a number above zero such as 86048.5.";
            return null;
        }

        var options = AccountingBooksCommands.ParseOptions(args[2..], ["--currency", "--source", "--note"], out error);
        if (options is null)
            return null;

        var prices = new AccountingPricesIpcRequest
        {
            ReplaceTimeUnixSeconds = seconds,
            ReplacePrice = price.ToString(CultureInfo.InvariantCulture)
        };
        if (!ApplyCurrency(options, prices, out error))
            return null;

        foreach (var (name, value) in options)
        {
            if (name == "--source")
                prices.Source = value;
            else if (name == "--note")
                prices.Note = value;
        }

        return new AccountingBooksCommands.AccountingArguments(
            "prices", Admin: new AccountingAdminIpcRequest
            {
                Action = (int)AccountingAdminAction.PricesReplace,
                Prices = prices
            });
    }

    private static bool ApplyCurrency(List<(string Name, string Value)> options, AccountingPricesIpcRequest prices,
                                      out string? error)
    {
        error = null;
        foreach (var (_, value) in options.Where(o => o.Name == "--currency"))
        {
            var code = value.Trim().ToUpperInvariant();
            if (!AccountingPriceOptions.IsCurrencyCode(code))
            {
                error = $"Invalid currency '{value}': expected a three-letter ISO 4217 code.";
                return false;
            }

            prices.Currency = code;
        }

        return true;
    }

    private static bool ApplyRange(List<(string Name, string Value)> options, AccountingPricesIpcRequest prices,
                                   out string? error)
    {
        error = null;
        foreach (var (name, value) in options)
        {
            if (name is not ("--since" or "--until"))
                continue;

            if (!AccountingBooksCommands.TryParseTime(name, value, out var seconds, out error))
                return false;

            if (name == "--since")
                prices.SinceUnixSeconds = seconds;
            else
                prices.UntilUnixSeconds = seconds;
        }

        if (prices.SinceUnixSeconds is { } since && prices.UntilUnixSeconds is { } until && until <= since)
        {
            error = "--until must be after --since.";
            return false;
        }

        return true;
    }
}