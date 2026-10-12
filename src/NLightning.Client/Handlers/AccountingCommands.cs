using System.Globalization;

namespace NLightning.Client.Handlers;

using Domain.Accounting.Enums;
using Ipc;
using Printers;
using Transport.Ipc.Requests;

/// <summary>
/// The accounting commands of the CLI (NL-602): <c>listaccountingevents|list-accounting-events [--after &lt;seq&gt;]
/// [--limit &lt;n&gt;] [--kind &lt;kind&gt;[,&lt;kind&gt;...]] [--channel &lt;channel&gt;] [--since &lt;time&gt;]
/// [--until &lt;time&gt;]</c> (ClientCommand 41) and <c>accountingsnapshot|accounting-snapshot</c> (42).
/// </summary>
internal static class AccountingCommands
{
    /// <summary>The usage of listaccountingevents.</summary>
    internal const string ListAccountingEventsUsage =
        "[--after <seq>] [--limit <n>] [--kind <kind>[,<kind>...]] [--channel <channel>] [--since <time>] "
      + "[--until <time>]";

    /// <summary>The largest page.</summary>
    internal const int MaxLimit = 1_000;

    /// <summary>Whether <paramref name="cmd"/> is listaccountingevents.</summary>
    internal static bool IsListAccountingEvents(string cmd) =>
        cmd is "listaccountingevents" or "list-accounting-events";

    /// <summary>
    /// Checks the arguments of an accounting command.
    /// </summary>
    /// <returns>An error message with the usage, or null when they are valid.</returns>
    internal static string? Validate(string cmd, string[] commandArgs)
    {
        if (!IsListAccountingEvents(cmd))
            return commandArgs.Length == 0 ? null : $"Unexpected argument '{commandArgs[0]}'. Usage: {cmd}";

        return ParseListAccountingEventsOptions(commandArgs, out var error) is null
                   ? $"{error} Usage: {cmd} {ListAccountingEventsUsage}"
                   : null;
    }

    /// <summary>
    /// Runs a validated accounting command and prints its result.
    /// </summary>
    internal static async Task RunAsync(string cmd, string[] commandArgs, NamedPipeIpcClient client,
                                        TextWriter output, CancellationToken cancellationToken)
    {
        if (IsListAccountingEvents(cmd))
        {
            var request = ParseListAccountingEventsOptions(commandArgs, out _)!;
            new ListAccountingEventsPrinter(output).Print(
                await client.ListAccountingEventsAsync(request, cancellationToken));
            return;
        }

        new AccountingSnapshotPrinter(output).Print(await client.AccountingSnapshotAsync(cancellationToken));
    }

    /// <summary>
    /// Parses the options of listaccountingevents, in any order, each as <c>--name value</c> or <c>--name=value</c>;
    /// <c>--kind</c> may repeat and takes comma-separated kind names (case does not matter) or numbers; a time is Unix
    /// seconds or an ISO date; the channel a 64-hex channel id or a short_channel_id.
    /// </summary>
    /// <returns>The request, or null with <paramref name="error"/> set.</returns>
    internal static ListAccountingEventsIpcRequest? ParseListAccountingEventsOptions(string[] commandArgs,
                                                                                     out string? error)
    {
        error = null;
        var request = new ListAccountingEventsIpcRequest();
        List<int>? kinds = null;
        for (var i = 0; i < commandArgs.Length; i++)
        {
            var argument = commandArgs[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unexpected argument '{argument}'.";
                return null;
            }

            var nameAndValue = argument.Split('=', 2);
            var name = nameAndValue[0].ToLowerInvariant();
            if (name is not ("--after" or "--limit" or "--kind" or "--channel" or "--since" or "--until"))
            {
                error = $"Unknown option '{argument}'.";
                return null;
            }

            string value;
            if (nameAndValue.Length == 2)
            {
                value = nameAndValue[1];
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
                case "--kind":
                    kinds ??= [];
                    foreach (var text in value.Split(',', StringSplitOptions.TrimEntries
                                                        | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!TryParseKind(text, out var kind))
                        {
                            error = $"Unknown kind '{text}': expected one of "
                                  + string.Join(", ", Enum.GetNames<AccountingEventKind>()) + ".";
                            return null;
                        }

                        if (!kinds.Contains(kind))
                            kinds.Add(kind);
                    }

                    if (kinds.Count == 0)
                    {
                        error = "Missing value for --kind.";
                        return null;
                    }

                    break;
                case "--channel":
                    if (value.Length == 0)
                    {
                        error = "Missing value for --channel.";
                        return null;
                    }

                    request.Channel = value;
                    break;
                case "--since":
                    if (ClientApp.ParseTime(value) is not { } since)
                    {
                        error = $"Invalid since '{value}': expected Unix seconds or an ISO date.";
                        return null;
                    }

                    request.SinceUnixSeconds = since;
                    break;
                case "--until":
                    if (ClientApp.ParseTime(value) is not { } until)
                    {
                        error = $"Invalid until '{value}': expected Unix seconds or an ISO date.";
                        return null;
                    }

                    request.UntilUnixSeconds = until;
                    break;
            }
        }

        request.Kinds = kinds;
        return request;
    }

    /// <summary>A kind's name (case does not matter) or number, when it is a known kind.</summary>
    internal static bool TryParseKind(string text, out int kind)
    {
        kind = 0;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            kind = number;
            return Enum.IsDefined(typeof(AccountingEventKind), number);
        }

        if (!Enum.TryParse<AccountingEventKind>(text, ignoreCase: true, out var parsed)
         || !Enum.IsDefined(parsed))
            return false;

        kind = (int)parsed;
        return true;
    }
}