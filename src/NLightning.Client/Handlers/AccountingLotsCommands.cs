using System.Globalization;
using System.Text;

namespace NLightning.Client.Handlers;

using Domain.Accounting.Financial.Lots;
using Domain.Accounting.Prices;
using Domain.Client.Enums;
using Transport.Ipc.Requests;

/// <summary>
/// <c>accounting lots import &lt;file&gt; [--currency &lt;code&gt;]</c> (ClientCommand 45 action 13, D-A9; NL-602
/// A3-T4): reads the lot file on the client's machine (<c>time,sats,cost</c> per line, <see cref="AccountingLotCsv"/>),
/// refuses it with every bad line listed by number, and sends it in one request (an import replaces the opening
/// balances' lots and any earlier import as a whole).
/// </summary>
internal static class AccountingLotsCommands
{
    /// <summary>The usage of the <c>lots</c> subcommand.</summary>
    internal const string Usage = "accounting lots import <file> [--currency <code>]";

    /// <summary>The most bad lines an import error lists.</summary>
    internal const int MaxListedErrors = 20;

    /// <summary>Parses <c>lots import &lt;file&gt; [--currency &lt;code&gt;]</c> (the arguments after
    /// <c>lots</c>).</summary>
    internal static AccountingBooksCommands.AccountingArguments? Parse(string[] args, out string? error)
    {
        error = null;
        if (args.Length == 0 || !string.Equals(args[0], "import", StringComparison.OrdinalIgnoreCase))
        {
            error = args.Length == 0
                        ? "Missing lots subcommand: import."
                        : $"Unknown lots subcommand '{args[0]}': expected import.";
            return null;
        }

        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            error = "Missing the lot file: accounting lots import <file>.";
            return null;
        }

        var options = AccountingBooksCommands.ParseOptions(args[2..], ["--currency"], out error);
        if (options is null)
            return null;

        var lots = new AccountingLotsIpcRequest();
        foreach (var (_, value) in options.Where(o => o.Name == "--currency"))
        {
            var code = value.Trim().ToUpperInvariant();
            if (!AccountingPriceOptions.IsCurrencyCode(code))
            {
                error = $"Invalid currency '{value}': expected a three-letter ISO 4217 code.";
                return null;
            }

            lots.Currency = code;
        }

        return new AccountingBooksCommands.AccountingArguments(
            "lots", Admin: new AccountingAdminIpcRequest
            {
                Action = (int)AccountingAdminAction.LotsImport,
                Lots = lots
            }, LotsFile: args[1]);
    }

    /// <summary>Reads the lot file into the request (its rows).</summary>
    /// <exception cref="InvalidOperationException">The file cannot be read, has bad lines (listed by number) or holds
    /// no lot.</exception>
    internal static async Task<AccountingAdminIpcRequest> BuildRequestAsync(
        AccountingBooksCommands.AccountingArguments arguments, CancellationToken cancellationToken)
    {
        var request = arguments.Admin!;
        var path = arguments.LotsFile!;
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not read {path}: {e.Message}", e);
        }

        var parsed = AccountingLotCsv.Parse(text);
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

        if (parsed.Lots.Count == 0)
            throw new InvalidOperationException($"{path} holds no lots.");

        return new AccountingAdminIpcRequest
        {
            Action = request.Action,
            Lots = new AccountingLotsIpcRequest
            {
                Currency = request.Lots?.Currency,
                Rows = parsed.Lots.Select(l => new AccountingLotRowIpc
                {
                    TimeUnixSeconds = l.Time.ToUnixTimeSeconds(),
                    Msat = l.Msat,
                    Cost = l.Cost.ToString(CultureInfo.InvariantCulture)
                }).ToList()
            }
        };
    }
}