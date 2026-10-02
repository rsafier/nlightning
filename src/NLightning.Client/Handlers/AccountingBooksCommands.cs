using System.Globalization;
using System.Text;

namespace NLightning.Client.Handlers;

using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Client.Enums;
using Ipc;
using Printers;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// The <c>accounting</c> verb family of the CLI (NL-602 A2): <c>accounting report &lt;kind&gt; [...]</c> (ClientCommand
/// 43), <c>accounting export --format hledger|beancount|csv [--since] [--until] [--output &lt;file&gt;]</c> (44) and
/// <c>accounting reconcile|rebuild|verify</c> (45).
/// </summary>
/// <remarks>
/// An export is fetched page by page and written by the client, to standard output or to <c>--output</c> (a path on
/// the client's machine, replaced when it exists); the daemon never writes a file (plan §11).
/// </remarks>
internal static class AccountingBooksCommands
{
    /// <summary>The verb.</summary>
    internal const string Verb = "accounting";

    /// <summary>The usage of the verb family.</summary>
    internal const string Usage =
        "accounting report <balance|income|channels|peers|fees|register> [options] | accounting export --format "
      + "<hledger|beancount|csv> [--since <time>] [--until <time>] [--output <file>] | accounting "
      + "<reconcile|rebuild|verify>";

    /// <summary>The largest register page.</summary>
    internal const int MaxLimit = 1_000;

    /// <summary>The export's page size.</summary>
    internal const int ExportPageSize = 1_000;

    private static readonly Dictionary<string, AccountingReportKind> s_reportKinds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["balance"] = AccountingReportKind.BalanceSheet,
            ["balance-sheet"] = AccountingReportKind.BalanceSheet,
            ["balancesheet"] = AccountingReportKind.BalanceSheet,
            ["income"] = AccountingReportKind.IncomeStatement,
            ["income-statement"] = AccountingReportKind.IncomeStatement,
            ["incomestatement"] = AccountingReportKind.IncomeStatement,
            ["channels"] = AccountingReportKind.Channels,
            ["peers"] = AccountingReportKind.Peers,
            ["fees"] = AccountingReportKind.Fees,
            ["register"] = AccountingReportKind.Register
        };

    /// <summary>Whether <paramref name="cmd"/> is the <c>accounting</c> verb.</summary>
    internal static bool IsAccounting(string cmd) => cmd == Verb;

    /// <summary>The arguments of an <c>accounting</c> command.</summary>
    internal sealed record AccountingArguments(
        string Subcommand,
        AccountingReportIpcRequest? Report = null,
        AccountingExportIpcRequest? Export = null,
        string? OutputPath = null,
        AccountingAdminIpcRequest? Admin = null);

    /// <summary>Checks the arguments; an error message with the usage, or null when they are valid.</summary>
    internal static string? Validate(string[] commandArgs) =>
        Parse(commandArgs, out var error) is null ? $"{error} Usage: {Usage}" : null;

    /// <summary>
    /// Parses <c>accounting &lt;subcommand&gt; [options]</c>; options are <c>--name value</c> or <c>--name=value</c>, a
    /// time is Unix seconds or an ISO date, a channel a 64-hex channel id or a short_channel_id.
    /// </summary>
    /// <returns>The arguments, or null with <paramref name="error"/> set.</returns>
    internal static AccountingArguments? Parse(string[] commandArgs, out string? error)
    {
        error = null;
        if (commandArgs.Length == 0)
        {
            error = "Missing accounting subcommand.";
            return null;
        }

        var subcommand = commandArgs[0].ToLowerInvariant();
        switch (subcommand)
        {
            case "report":
                return ParseReport(commandArgs[1..], out error);
            case "export":
                return ParseExport(commandArgs[1..], out error);
            case "reconcile":
            case "rebuild":
            case "verify":
                if (commandArgs.Length > 1)
                {
                    error = $"Unexpected argument '{commandArgs[1]}'.";
                    return null;
                }

                var action = subcommand switch
                {
                    "reconcile" => AccountingAdminAction.Reconcile,
                    "rebuild" => AccountingAdminAction.Rebuild,
                    _ => AccountingAdminAction.Verify
                };
                return new AccountingArguments(subcommand,
                                               Admin: new AccountingAdminIpcRequest { Action = (int)action });
            default:
                error = $"Unknown accounting subcommand '{commandArgs[0]}'.";
                return null;
        }
    }

    /// <summary>
    /// Runs a validated <c>accounting</c> command and prints its result (an export goes to <paramref name="output"/>
    /// or its <c>--output</c> file).
    /// </summary>
    internal static async Task RunAsync(string[] commandArgs, NamedPipeIpcClient client, TextWriter output,
                                        CancellationToken cancellationToken)
    {
        var arguments = Parse(commandArgs, out _)!;
        if (arguments.Report is { } report)
        {
            new AccountingReportPrinter(output).Print(await client.AccountingReportAsync(report, cancellationToken));
            return;
        }

        if (arguments.Export is { } export)
        {
            if (arguments.OutputPath is not { } path)
            {
                await ExportAsync(client.AccountingExportAsync, export, output, cancellationToken);
                return;
            }

            // Written next to the target first, so an interrupted export never leaves a truncated file in its place
            var fullPath = Path.GetFullPath(path);
            var temporary = fullPath + ".part";
            int entries;
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                entries = await ExportAsync(client.AccountingExportAsync, export, writer, cancellationToken);
            }

            File.Move(temporary, fullPath, overwrite: true);
            output.WriteLine(string.Format(CultureInfo.InvariantCulture, "Exported {0} entr{1} to {2}", entries,
                                           entries == 1 ? "y" : "ies", fullPath));
            return;
        }

        new AccountingAdminPrinter(output).Print(await client.AccountingAdminAsync(arguments.Admin!, cancellationToken));
    }

    /// <summary>
    /// Fetches every page of an export from cursor 0 and writes their text in order.
    /// </summary>
    /// <returns>How many entries the pages read.</returns>
    internal static async Task<int> ExportAsync(
        Func<AccountingExportIpcRequest, CancellationToken, Task<AccountingExportIpcResponse>> fetch,
        AccountingExportIpcRequest request, TextWriter output, CancellationToken cancellationToken)
    {
        var after = 0L;
        var entries = 0;
        while (true)
        {
            var page = await fetch(new AccountingExportIpcRequest
            {
                Format = request.Format,
                SinceUnixSeconds = request.SinceUnixSeconds,
                UntilUnixSeconds = request.UntilUnixSeconds,
                AfterLedgerSeq = after,
                Limit = request.Limit
            }, cancellationToken);
            await output.WriteAsync(page.Text.AsMemory(), cancellationToken);
            entries += page.EntryCount;
            if (!page.HasMore || page.NextAfter <= after)
                break;

            after = page.NextAfter;
        }

        await output.FlushAsync(cancellationToken);
        return entries;
    }

    private static AccountingArguments? ParseReport(string[] args, out string? error)
    {
        error = null;
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            error = "Missing report kind: balance, income, channels, peers, fees or register.";
            return null;
        }

        if (!s_reportKinds.TryGetValue(args[0], out var kind))
        {
            error = $"Unknown report '{args[0]}': expected balance, income, channels, peers, fees or register.";
            return null;
        }

        string[] allowed = kind switch
        {
            AccountingReportKind.BalanceSheet => ["--at", "--until"],
            AccountingReportKind.IncomeStatement or AccountingReportKind.Fees => ["--since", "--until"],
            AccountingReportKind.Channels or AccountingReportKind.Peers => ["--since", "--until", "--channel"],
            _ =>
            [
                "--since", "--until", "--channel", "--account", "--kind", "--after", "--limit"
            ]
        };
        var options = ParseOptions(args[1..], allowed, out error);
        if (options is null)
            return null;

        var request = new AccountingReportIpcRequest { Kind = (int)kind };
        List<int>? kinds = null;
        foreach (var (name, value) in options)
        {
            switch (name)
            {
                case "--since":
                    if (!TryParseTime(name, value, out var since, out error))
                        return null;
                    request.SinceUnixSeconds = since;
                    break;
                case "--until":
                case "--at":
                    if (!TryParseTime(name, value, out var until, out error))
                        return null;
                    request.UntilUnixSeconds = until;
                    break;
                case "--channel":
                    request.Channel = value;
                    break;
                case "--account":
                    request.Account = value;
                    break;
                case "--kind":
                    kinds ??= [];
                    foreach (var text in value.Split(',', StringSplitOptions.TrimEntries
                                                        | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!AccountingCommands.TryParseKind(text, out var eventKind))
                        {
                            error = $"Unknown kind '{text}'.";
                            return null;
                        }

                        if (!kinds.Contains(eventKind))
                            kinds.Add(eventKind);
                    }

                    break;
                case "--after":
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var after))
                    {
                        error = $"Invalid after '{value}': expected a ledger sequence (0 or more).";
                        return null;
                    }

                    request.AfterLedgerSeq = after;
                    break;
                case "--limit":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
                     || limit is < 1 or > MaxLimit)
                    {
                        error = $"Invalid limit '{value}': expected a number from 1 to {MaxLimit}.";
                        return null;
                    }

                    request.Limit = limit;
                    break;
            }
        }

        if (request.SinceUnixSeconds is { } start && request.UntilUnixSeconds is { } end && end <= start)
        {
            error = "--until must be after --since.";
            return null;
        }

        request.EventKinds = kinds;
        return new AccountingArguments("report", Report: request);
    }

    private static AccountingArguments? ParseExport(string[] args, out string? error)
    {
        var options = ParseOptions(args, ["--format", "--since", "--until", "--output"], out error);
        if (options is null)
            return null;

        var request = new AccountingExportIpcRequest { Limit = ExportPageSize };
        string? outputPath = null;
        AccountingExportFormat? format = null;
        foreach (var (name, value) in options)
        {
            switch (name)
            {
                case "--format":
                    format = value.ToLowerInvariant() switch
                    {
                        "hledger" or "ledger" or "journal" => AccountingExportFormat.Hledger,
                        "beancount" => AccountingExportFormat.Beancount,
                        "csv" => AccountingExportFormat.Csv,
                        _ => null
                    };
                    if (format is null)
                    {
                        error = $"Unknown format '{value}': expected hledger, beancount or csv.";
                        return null;
                    }

                    break;
                case "--since":
                    if (!TryParseTime(name, value, out var since, out error))
                        return null;
                    request.SinceUnixSeconds = since;
                    break;
                case "--until":
                    if (!TryParseTime(name, value, out var until, out error))
                        return null;
                    request.UntilUnixSeconds = until;
                    break;
                case "--output":
                    outputPath = value;
                    break;
            }
        }

        if (format is not { } chosen)
        {
            error = "Missing --format (hledger, beancount or csv).";
            return null;
        }

        if (request.SinceUnixSeconds is { } start && request.UntilUnixSeconds is { } end && end <= start)
        {
            error = "--until must be after --since.";
            return null;
        }

        request.Format = (int)chosen;
        return new AccountingArguments("export", Export: request, OutputPath: outputPath);
    }

    // The options in order as (lower-case name, value); each as --name value or --name=value, every one at most once
    // except --kind
    private static List<(string Name, string Value)>? ParseOptions(string[] args, string[] allowed, out string? error)
    {
        error = null;
        var options = new List<(string, string)>();
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unexpected argument '{argument}'.";
                return null;
            }

            var nameAndValue = argument.Split('=', 2);
            var name = nameAndValue[0].ToLowerInvariant();
            if (!allowed.Contains(name))
            {
                error = $"Unknown option '{argument}' here (expected {string.Join(", ", allowed)}).";
                return null;
            }

            string value;
            if (nameAndValue.Length == 2)
                value = nameAndValue[1];
            else if (i + 1 < args.Length)
                value = args[++i];
            else
            {
                error = $"Missing value for {name}.";
                return null;
            }

            if (value.Length == 0)
            {
                error = $"Missing value for {name}.";
                return null;
            }

            if (name != "--kind" && options.Exists(o => o.Item1 == name))
            {
                error = $"{name} given twice.";
                return null;
            }

            options.Add((name, value));
        }

        return options;
    }

    private static bool TryParseTime(string name, string value, out long seconds, out string? error)
    {
        error = null;
        if (ClientApp.ParseTime(value) is { } parsed)
        {
            seconds = parsed;
            return true;
        }

        seconds = 0;
        error = $"Invalid {name.TrimStart('-')} '{value}': expected Unix seconds or an ISO date.";
        return false;
    }
}