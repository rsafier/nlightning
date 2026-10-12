using System.Globalization;

namespace NLightning.Client.Printers;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Client.Enums;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// Prints the answer of <c>accounting classify ...</c> (NL-602 A3-T3).
/// </summary>
public sealed class AccountingClassifyPrinter : IPrinter<AccountingClassifyIpcResponse>
{
    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    private readonly TextWriter _output;

    public AccountingClassifyPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(AccountingClassifyIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var action = (AccountingClassifyAction)item.Action;
        switch (action)
        {
            case AccountingClassifyAction.RuleAdd:
                foreach (var rule in item.Rules ?? [])
                    _output.WriteLine("Added " + Rule(rule));
                break;
            case AccountingClassifyAction.RuleList:
                if (item.Rules is not { Count: > 0 } rules)
                    _output.WriteLine("No classification rules.");
                else
                    foreach (var rule in rules)
                        _output.WriteLine(Rule(rule));
                break;
            case AccountingClassifyAction.RuleRemove:
            case AccountingClassifyAction.RuleEnable:
            case AccountingClassifyAction.RuleDisable:
                _output.WriteLine(item.Changed == true
                                      ? $"Rule {Verb(action)}."
                                      : "No such rule.");
                break;
            case AccountingClassifyAction.RuleTest when item.Test is { } test:
                PrintTest(test);
                break;
            case AccountingClassifyAction.Set when item.Override is { } stored:
                _output.WriteLine(string.Format(s_inv, "Override set: {0} -> {1}{2}", stored.EventKey, stored.Account,
                                                stored.Note is null ? string.Empty : $" ({stored.Note})"));
                break;
            case AccountingClassifyAction.Unset:
                _output.WriteLine(item.Changed == true ? "Override removed." : "No override for that event.");
                break;
            case AccountingClassifyAction.ListOverrides:
                if (item.Overrides is not { Count: > 0 } overrides)
                    _output.WriteLine("No overrides.");
                else
                    foreach (var o in overrides)
                        _output.WriteLine(string.Format(s_inv, "{0}  {1}  -> {2}{3}",
                                                        AccountingReportPrinter.Time(o.CreatedAtUnixMilliseconds),
                                                        o.EventKey, o.Account,
                                                        o.Note is null ? string.Empty : $"  ({o.Note})"));
                break;
            case AccountingClassifyAction.ListUnclassified:
                PrintUnclassified(item);
                break;
        }

        foreach (var warning in item.Warnings)
            _output.WriteLine("Warning: " + warning);
    }

    private void PrintTest(AccountingClassifyTestIpcResponse test)
    {
        _output.WriteLine(string.Format(s_inv, "#{0} {1} {2} at {3}", test.LedgerSeq, KindName(test.Kind), test.EventKey,
                                        AccountingReportPrinter.Time(test.OccurredAtUnixMilliseconds)));
        _output.WriteLine(test.Account is null
                              ? $"  {test.Reason}"
                              : string.Format(s_inv, "  -> {0} ({1}){2}", test.Account, test.Reason,
                                              test.IsUnclassified ? " [unclassified]" : string.Empty));
        foreach (var line in test.Lines)
            _output.WriteLine(string.Format(s_inv, "  {0}  {1} msat  ({2})", line.AccountName.PadRight(36),
                                            line.AmountMsat,
                                            Enum.IsDefined(typeof(AccountRole), line.Role)
                                                ? ((AccountRole)line.Role).ToString()
                                                : line.Role.ToString(s_inv)));
        if (test.TimedOutRuleIds.Count > 0)
            _output.WriteLine("  Label pattern timed out for rule(s) " + string.Join(", ", test.TimedOutRuleIds));
        if (test.CandidateMatches is { } matches)
            _output.WriteLine(test.CandidateTimedOut
                                  ? "  Candidate rule: label pattern timed out (no match)"
                                  : $"  Candidate rule: {(matches ? "matches" : "does not match")}");
    }

    private void PrintUnclassified(AccountingClassifyIpcResponse item)
    {
        var items = item.Unclassified ?? [];
        if (items.Count == 0)
            _output.WriteLine("No unclassified entries.");
        foreach (var entry in items)
            _output.WriteLine(string.Format(s_inv, "#{0}  {1}  {2}  {3}  {4} msat  -> {5}{6}", entry.LedgerSeq,
                                            AccountingReportPrinter.Time(entry.OccurredAtUnixMilliseconds),
                                            KindName(entry.Kind), entry.EventKey, entry.AmountMsat, entry.Account,
                                            entry.Label is null ? string.Empty : $"  [{entry.Label}]"));
        _output.WriteLine(item.HasMore
                              ? string.Format(s_inv, "Looked at {0} entries; more after --after {1}", item.Scanned,
                                              item.NextAfter)
                              : string.Format(s_inv, "Looked at {0} entries; end of the books (#{1})", item.Scanned,
                                              item.NextAfter));
    }

    private static string Rule(AccountingRuleIpcModel rule)
    {
        var matches = new List<string>();
        if (rule.Kinds is { Count: > 0 } kinds)
            matches.Add("kind=" + string.Join(',', kinds.Select(KindName)));
        if (rule.LabelPattern is { } label)
            matches.Add($"label~/{label}/");
        if (rule.TagKey is { } key)
            matches.Add($"tag:{key}={rule.TagValue ?? "*"}");
        if (rule.Counterparty is { } counterparty)
            matches.Add("counterparty=" + counterparty);
        if (rule.OfferId is { } offer)
            matches.Add("offer=" + offer);
        if (rule.ChannelId is { } channel)
            matches.Add("channel=" + channel);

        return string.Format(s_inv, "rule {0}  priority {1}{2}  {3} -> {4}{5}", rule.Id, rule.Priority,
                             rule.Enabled ? string.Empty : "  (disabled)",
                             matches.Count == 0 ? "(any)" : string.Join(' ', matches), rule.TargetAccount,
                             rule.Description is null ? string.Empty : $"  \"{rule.Description}\"");
    }

    private static string KindName(int kind) =>
        Enum.IsDefined(typeof(AccountingEventKind), kind) ? ((AccountingEventKind)kind).ToString() : kind.ToString(s_inv);

    private static string Verb(AccountingClassifyAction action) => action switch
    {
        AccountingClassifyAction.RuleRemove => "removed",
        AccountingClassifyAction.RuleEnable => "enabled",
        _ => "disabled"
    };
}