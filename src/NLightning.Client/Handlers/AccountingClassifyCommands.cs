using System.Globalization;

namespace NLightning.Client.Handlers;

using Domain.Accounting.Enums;
using Domain.Client.Enums;
using Transport.Ipc.Requests;

/// <summary>
/// <c>accounting classify ...</c> (ClientCommand 45, action 5, NL-602 A3-T3, D-A10): the financial book's
/// classification rules and overrides.
/// </summary>
/// <remarks>
/// <code>
/// accounting classify rule add --account &lt;name&gt; [match options] [--priority &lt;n&gt;] [--disabled] [--description &lt;text&gt;]
/// accounting classify rule list
/// accounting classify rule remove|enable|disable &lt;id&gt;
/// accounting classify rule test &lt;event key&gt; [match options] [--account &lt;name&gt;]
/// accounting classify set &lt;event key&gt; &lt;account&gt; [--note &lt;text&gt;]
/// accounting classify unset &lt;event key&gt;
/// accounting classify list [--skip &lt;n&gt;] [--limit &lt;n&gt;]
/// accounting classify list --unclassified [--after &lt;seq&gt;] [--limit &lt;n&gt;]
/// </code>
/// Match options: <c>--kind &lt;kind&gt;[,&lt;kind&gt;...]</c> (names such as <c>InvoiceSettled</c> or numbers),
/// <c>--label &lt;regex&gt;</c>, <c>--tag &lt;key&gt;[=&lt;glob&gt;]</c>, <c>--counterparty &lt;node id&gt;</c>,
/// <c>--offer &lt;offer id&gt;</c>, <c>--channel &lt;channel id&gt;</c>. Options are <c>--name value</c> or
/// <c>--name=value</c>.
/// </remarks>
internal static class AccountingClassifyCommands
{
    /// <summary>The usage of <c>accounting classify</c>.</summary>
    internal const string Usage =
        "accounting classify rule add --account <name> [--priority <n>] [--kind <kinds>] [--label <regex>] "
      + "[--tag <key>[=<glob>]] [--counterparty <node id>] [--offer <offer id>] [--channel <channel id>] [--disabled] "
      + "[--description <text>] | accounting classify rule list | accounting classify rule remove|enable|disable <id> "
      + "| accounting classify rule test <event key> [match options] | accounting classify set <event key> <account> "
      + "[--note <text>] | accounting classify unset <event key> | accounting classify list [--unclassified] "
      + "[--after <seq>] [--skip <n>] [--limit <n>]";

    private static readonly string[] s_matchOptions =
        ["--kind", "--label", "--tag", "--counterparty", "--offer", "--channel"];

    /// <summary>
    /// Parses the arguments after <c>accounting classify</c>.
    /// </summary>
    /// <returns>The admin request, or null with <paramref name="error"/> set.</returns>
    internal static AccountingAdminIpcRequest? Parse(string[] args, out string? error)
    {
        error = null;
        if (args.Length == 0)
        {
            error = "Missing classify subcommand (rule, set, unset or list).";
            return null;
        }

        var classify = args[0].ToLowerInvariant() switch
        {
            "rule" => ParseRule(args[1..], out error),
            "set" => ParseSet(args[1..], out error),
            "unset" => ParseUnset(args[1..], out error),
            "list" => ParseList(args[1..], out error),
            _ => Fail($"Unknown classify subcommand '{args[0]}'.", out error)
        };

        return classify is null
                   ? null
                   : new AccountingAdminIpcRequest { Action = (int)AccountingAdminAction.Classify, Classify = classify };
    }

    private static AccountingClassifyIpcRequest? ParseRule(string[] args, out string? error)
    {
        if (args.Length == 0)
            return Fail("Missing rule subcommand (add, list, remove, enable, disable or test).", out error);

        var subcommand = args[0].ToLowerInvariant();
        switch (subcommand)
        {
            case "list":
                if (args.Length > 1)
                    return Fail($"Unexpected argument '{args[1]}'.", out error);
                error = null;
                return new AccountingClassifyIpcRequest { Action = (int)AccountingClassifyAction.RuleList };
            case "remove":
            case "enable":
            case "disable":
                if (args.Length != 2)
                    return Fail($"rule {subcommand} takes one rule id.", out error);
                if (!long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                    return Fail($"Invalid rule id '{args[1]}'.", out error);
                error = null;
                return new AccountingClassifyIpcRequest
                {
                    Action = (int)(subcommand switch
                    {
                        "remove" => AccountingClassifyAction.RuleRemove,
                        "enable" => AccountingClassifyAction.RuleEnable,
                        _ => AccountingClassifyAction.RuleDisable
                    }),
                    RuleId = id
                };
            case "add":
                {
                    var options = ParseOptions(args[1..], [.. s_matchOptions, "--account", "--priority", "--description"],
                                               ["--disabled"], out error);
                    if (options is null)
                        return null;
                    if (!options.ContainsKey("--account"))
                        return Fail("rule add needs --account <name>.", out error);

                    var rule = BuildRule(options, out error);
                    return rule is null
                               ? null
                               : new AccountingClassifyIpcRequest
                               {
                                   Action = (int)AccountingClassifyAction.RuleAdd,
                                   Rule = rule
                               };
                }
            case "test":
                {
                    if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
                        return Fail("rule test needs an event key.", out error);

                    var options = ParseOptions(args[2..], [.. s_matchOptions, "--account", "--priority"], [], out error);
                    if (options is null)
                        return null;

                    AccountingRuleIpcModel? candidate = null;
                    if (options.Keys.Any(k => s_matchOptions.Contains(k)))
                    {
                        candidate = BuildRule(options, out error);
                        if (candidate is null)
                            return null;
                    }
                    else if (options.Count > 0)
                    {
                        return Fail("--account and --priority need a match option to test a candidate rule.", out error);
                    }

                    return new AccountingClassifyIpcRequest
                    {
                        Action = (int)AccountingClassifyAction.RuleTest,
                        EventKey = args[1],
                        Rule = candidate
                    };
                }
            default:
                return Fail($"Unknown rule subcommand '{args[0]}'.", out error);
        }
    }

    private static AccountingClassifyIpcRequest? ParseSet(string[] args, out string? error)
    {
        if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal)
                            || args[1].StartsWith("--", StringComparison.Ordinal))
            return Fail("set needs an event key and an account.", out error);

        var options = ParseOptions(args[2..], ["--note"], [], out error);
        if (options is null)
            return null;

        return new AccountingClassifyIpcRequest
        {
            Action = (int)AccountingClassifyAction.Set,
            EventKey = args[0],
            Account = args[1],
            Note = options.GetValueOrDefault("--note")
        };
    }

    private static AccountingClassifyIpcRequest? ParseUnset(string[] args, out string? error)
    {
        if (args.Length != 1 || args[0].StartsWith("--", StringComparison.Ordinal))
            return Fail("unset takes one event key.", out error);

        error = null;
        return new AccountingClassifyIpcRequest { Action = (int)AccountingClassifyAction.Unset, EventKey = args[0] };
    }

    private static AccountingClassifyIpcRequest? ParseList(string[] args, out string? error)
    {
        var options = ParseOptions(args, ["--after", "--skip", "--limit"], ["--unclassified"], out error);
        if (options is null)
            return null;

        var unclassified = options.ContainsKey("--unclassified");
        if (unclassified && options.ContainsKey("--skip"))
            return Fail("--skip lists overrides; the unclassified listing pages with --after.", out error);
        if (!unclassified && options.ContainsKey("--after"))
            return Fail("--after pages the unclassified listing (with --unclassified); overrides page with --skip.",
                        out error);

        var request = new AccountingClassifyIpcRequest
        {
            Action = (int)(unclassified ? AccountingClassifyAction.ListUnclassified
                                        : AccountingClassifyAction.ListOverrides)
        };
        if (options.TryGetValue("--after", out var after))
        {
            if (!long.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return Fail($"Invalid after '{after}'.", out error);
            request.AfterLedgerSeq = value;
        }

        if (options.TryGetValue("--skip", out var skip))
        {
            if (!int.TryParse(skip, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return Fail($"Invalid skip '{skip}'.", out error);
            request.Skip = value;
        }

        if (options.TryGetValue("--limit", out var limit))
        {
            if (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
             || value is < 1 or > AccountingBooksCommands.MaxLimit)
                return Fail($"Invalid limit '{limit}': 1 to {AccountingBooksCommands.MaxLimit}.", out error);
            request.Limit = value;
        }

        error = null;
        return request;
    }

    private static AccountingRuleIpcModel? BuildRule(Dictionary<string, string> options, out string? error)
    {
        error = null;
        var rule = new AccountingRuleIpcModel
        {
            TargetAccount = options.GetValueOrDefault("--account") ?? string.Empty,
            LabelPattern = options.GetValueOrDefault("--label"),
            Counterparty = options.GetValueOrDefault("--counterparty"),
            OfferId = options.GetValueOrDefault("--offer"),
            ChannelId = options.GetValueOrDefault("--channel"),
            Description = options.GetValueOrDefault("--description"),
            Enabled = !options.ContainsKey("--disabled")
        };

        if (options.TryGetValue("--priority", out var priority))
        {
            if (!int.TryParse(priority, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                return Fail<AccountingRuleIpcModel>($"Invalid priority '{priority}'.", out error);
            rule.Priority = value;
        }

        if (options.TryGetValue("--tag", out var tag))
        {
            var separator = tag.IndexOf('=');
            rule.TagKey = separator < 0 ? tag : tag[..separator];
            rule.TagValue = separator < 0 ? null : tag[(separator + 1)..];
            if (rule.TagKey.Length == 0 || rule.TagValue is { Length: 0 })
                return Fail<AccountingRuleIpcModel>($"Invalid tag '{tag}': expected <key> or <key>=<glob>.", out error);
        }

        if (options.TryGetValue("--kind", out var kinds))
        {
            rule.Kinds = [];
            foreach (var name in kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<AccountingEventKind>(name, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
                    return Fail<AccountingRuleIpcModel>($"Unknown accounting event kind '{name}'.", out error);
                rule.Kinds.Add((int)kind);
            }

            if (rule.Kinds.Count == 0)
                return Fail<AccountingRuleIpcModel>("--kind needs at least one kind.", out error);
        }

        return rule;
    }

    // Options as --name value or --name=value (each at most once) and flags without a value
    private static Dictionary<string, string>? ParseOptions(string[] args, string[] valued, string[] flags,
                                                            out string? error)
    {
        error = null;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
                return Fail<Dictionary<string, string>>($"Unexpected argument '{argument}'.", out error);

            var nameAndValue = argument.Split('=', 2);
            var name = nameAndValue[0].ToLowerInvariant();
            if (options.ContainsKey(name))
                return Fail<Dictionary<string, string>>($"{name} given twice.", out error);

            if (flags.Contains(name))
            {
                if (nameAndValue.Length == 2)
                    return Fail<Dictionary<string, string>>($"{name} takes no value.", out error);
                options[name] = string.Empty;
                continue;
            }

            if (!valued.Contains(name))
                return Fail<Dictionary<string, string>>($"Unknown option '{argument}' here (expected {string.Join(", ", valued.Concat(flags))}).",
                            out error);

            string value;
            if (nameAndValue.Length == 2)
                value = nameAndValue[1];
            else if (i + 1 < args.Length)
                value = args[++i];
            else
                return Fail<Dictionary<string, string>>($"Missing value for {name}.", out error);

            if (value.Length == 0)
                return Fail<Dictionary<string, string>>($"Missing value for {name}.", out error);

            options[name] = value;
        }

        return options;
    }

    private static T? Fail<T>(string message, out string? error) where T : class
    {
        error = message;
        return null;
    }

    private static AccountingClassifyIpcRequest? Fail(string message, out string? error) =>
        Fail<AccountingClassifyIpcRequest>(message, out error);
}