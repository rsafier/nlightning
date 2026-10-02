using System.Text.RegularExpressions;

namespace NLightning.Domain.Accounting.Financial.Classification;

using Books;
using Constants;
using Enums;
using Models;
using Services;

/// <summary>
/// Classifies the operational entries for the financial book (NL-602 A3-T3, plan
/// <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.2, D-A10). Pure: the rules, the override and the chart come in, the answer
/// goes out.
/// </summary>
/// <remarks>
/// <para><b>Order.</b> An override of the event key wins; else the first enabled rule in (priority, id) order whose
/// every set match field matches; else the default of the line's role in the <see cref="FinancialChart"/> (a received
/// payment that paid our own invoice, <c>selfPayment</c>, is a rebalance). Only the entry's classifiable lines
/// (<see cref="FinancialChart.IsClassifiable"/>) move; an entry without one answers a null account and nothing is
/// looked at.</para>
/// <para><b>Match fields</b> (a null field matches anything): the kind (a <see cref="AccountingEventKind.Reversal"/>
/// also matches by the kind it reverses, so a reversal follows its original); the label (event detail
/// <see cref="LabelDetail"/>; a regex run with <see cref="RegexOptions.NonBacktracking"/> and a 100 ms timeout; an
/// event without a label never matches a pattern; a timeout is no match and is reported); a tag key (detail
/// <c>tag.&lt;key&gt;</c>) and a glob on its value (<c>*</c> any run, <c>?</c> one character, case-sensitive); the
/// counterparty; the BOLT 12 offer id (detail <see cref="OfferIdDetail"/>, written on received payments); the channel
/// (the event's channel, or a forward's incoming or outgoing channel).</para>
/// <para>A stored rule whose pattern does not compile (written by another build) never matches and is listed in
/// <see cref="InvalidRuleIds"/>; the engine never throws for a rule.</para>
/// </remarks>
public sealed class ClassificationEngine
{
    /// <summary>The label of an event (A3-T1 copies it from the source row).</summary>
    public const string LabelDetail = AccountingDetailKeys.Label;

    /// <summary>The prefix of a tag's detail (<c>tag.&lt;key&gt;</c>, A3-T1).</summary>
    public const string TagDetailPrefix = AccountingDetailKeys.TagPrefix;

    /// <summary>The BOLT 12 offer id of a received payment (hex).</summary>
    public const string OfferIdDetail = "offerId";

    private const string IncomingChannelDetail = "incomingChannelId";
    private const string OutgoingChannelDetail = "outgoingChannelId";

    private readonly IReadOnlyList<CompiledRule> _rules;

    /// <summary>
    /// An engine over <paramref name="rules"/> (any order; the disabled ones are skipped) and
    /// <paramref name="chart"/>. <paramref name="regexTimeout"/> replaces the 100 ms of D-A10 (tests only).
    /// </summary>
    public ClassificationEngine(FinancialChart chart, IEnumerable<AccountingRule> rules, TimeSpan? regexTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(rules);
        Chart = chart;

        var timeout = regexTimeout ?? AccountingRuleValidator.RegexTimeout;
        var compiled = new List<CompiledRule>();
        var invalid = new List<long>();
        foreach (var rule in rules.Where(r => r.Enabled).OrderBy(r => r.Priority).ThenBy(r => r.Id))
        {
            Regex? regex = null;
            if (rule.LabelPattern is { } pattern
             && !AccountingRuleValidator.TryCompile(pattern, timeout, out regex, out _))
            {
                invalid.Add(rule.Id);
                continue;
            }

            if (!FinancialAccountNameRules.TryValidateTarget(rule.TargetAccount, out _))
            {
                invalid.Add(rule.Id);
                continue;
            }

            compiled.Add(new CompiledRule(rule, regex));
        }

        _rules = compiled;
        InvalidRuleIds = invalid;
    }

    /// <summary>The chart the engine classifies into.</summary>
    public FinancialChart Chart { get; }

    /// <summary>The enabled rules that can never match (an invalid pattern or target).</summary>
    public IReadOnlyList<long> InvalidRuleIds { get; }

    /// <summary>
    /// Classifies <paramref name="entry"/> (the operational entry of <paramref name="accountingEvent"/>) with the
    /// override of its event key, if any.
    /// </summary>
    public AccountingClassification Classify(AccountingEntry entry, AccountingEventModel accountingEvent,
                                             AccountingOverride? accountingOverride)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(accountingEvent);

        var role = MainClassifiableRole(entry);
        if (role is null)
            return new AccountingClassification(null, AccountingClassificationSource.Default, null, null, false,
                                                "nothing to classify (no income, expense or transfer line)");

        if (accountingOverride is not null)
            return new AccountingClassification(accountingOverride.Account, AccountingClassificationSource.Override,
                                                null, role, Chart.IsUnclassified(accountingOverride.Account),
                                                "override");

        var timedOut = new List<long>();
        foreach (var rule in _rules)
        {
            if (!Matches(rule, entry, accountingEvent, timedOut))
                continue;

            var reason = rule.Rule.Description is { Length: > 0 } description
                             ? $"rule {rule.Rule.Id} ({description})"
                             : $"rule {rule.Rule.Id}";
            return new AccountingClassification(rule.Rule.TargetAccount, AccountingClassificationSource.Rule,
                                                rule.Rule.Id, role, Chart.IsUnclassified(rule.Rule.TargetAccount),
                                                reason)
            {
                TimedOutRuleIds = timedOut
            };
        }

        var account = Chart[DefaultAccountOf(role.Value, accountingEvent)];
        return new AccountingClassification(account, AccountingClassificationSource.Default, null, role,
                                            Chart.IsUnclassified(account), $"default for {role.Value}")
        {
            TimedOutRuleIds = timedOut
        };
    }

    /// <summary>
    /// Whether <paramref name="rule"/> alone matches the entry (enabled or not; a <c>rule test</c> of a candidate).
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="entry">The operational entry.</param>
    /// <param name="accountingEvent">Its event.</param>
    /// <param name="timedOut">True when the label pattern ran out of time (no match).</param>
    /// <exception cref="ArgumentException">The rule's pattern does not compile.</exception>
    public bool RuleMatches(AccountingRule rule, AccountingEntry entry, AccountingEventModel accountingEvent,
                            out bool timedOut)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Regex? regex = null;
        if (rule.LabelPattern is { } pattern
         && !AccountingRuleValidator.TryCompile(pattern, AccountingRuleValidator.RegexTimeout, out regex,
                                                out var error))
            throw new ArgumentException(error, nameof(rule));

        var timeouts = new List<long>();
        var matches = Matches(new CompiledRule(rule, regex), entry, accountingEvent, timeouts);
        timedOut = timeouts.Count > 0;
        return matches;
    }

    /// <summary>
    /// The financial lines of <paramref name="entry"/> under <paramref name="classification"/>: every posting with its
    /// <see cref="AccountingPosting.AccountName"/> set (the classified account for a classifiable line, its chart
    /// account otherwise; with a default classification each classifiable line gets its own role's default). Amounts
    /// and roles are the operational ones, so the lines still balance, with one exception: the operational book keeps
    /// a rebalance's route fee in the paying side's <see cref="AccountRole.Rebalance"/> line (NL-609, Dr Rebalance
    /// (a + fee)), and the financial book splits it into the classified line (a, a transfer by default) and a
    /// <see cref="AccountRole.RoutingFees"/> line of the fee in the chart's routing fee account, because only the fee of
    /// a rebalance disposes (D-A12).
    /// </summary>
    public IReadOnlyList<AccountingPosting> MapPostings(AccountingEntry entry, AccountingEventModel accountingEvent,
                                                        AccountingClassification classification)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(accountingEvent);
        ArgumentNullException.ThrowIfNull(classification);

        var lines = new List<AccountingPosting>(entry.Postings.Count);
        foreach (var posting in entry.Postings)
        {
            string name;
            if (!FinancialChart.IsClassifiable(posting.Account))
                name = Chart[FinancialChart.DefaultAccountOf(posting.Account)];
            else if (classification.Source != AccountingClassificationSource.Default
                  && classification.Account is { } account)
                name = account;
            else
                name = Chart[DefaultAccountOf(posting.Account, accountingEvent)];

            if (IsRebalanceWithFee(posting, accountingEvent))
            {
                var fee = accountingEvent.FeeMsat;
                lines.Add(new AccountingPosting(posting.Account, checked(posting.AmountMsat - fee)) { AccountName = name });
                lines.Add(new AccountingPosting(AccountRole.RoutingFees, fee)
                {
                    AccountName = Chart[FinancialChart.DefaultAccountOf(AccountRole.RoutingFees)]
                });
                continue;
            }

            lines.Add(new AccountingPosting(posting.Account, posting.AmountMsat) { AccountName = name });
        }

        return lines;
    }

    /// <summary>The default account of a classifiable line of <paramref name="role"/> in this event.</summary>
    public static FinancialAccount DefaultAccountOf(AccountRole role, AccountingEventModel accountingEvent)
    {
        ArgumentNullException.ThrowIfNull(accountingEvent);
        if (role == AccountRole.Received && IsSet(accountingEvent, AccountingDetailKeys.SelfPayment))
            return FinancialAccount.Rebalance;

        return FinancialChart.DefaultAccountOf(role);
    }

    // The classifiable line with the largest amount (the first role in posting order on a tie)
    private static AccountRole? MainClassifiableRole(AccountingEntry entry)
    {
        AccountRole? role = null;
        var largest = -1L;
        foreach (var posting in entry.Postings)
        {
            if (!FinancialChart.IsClassifiable(posting.Account))
                continue;

            var size = posting.AmountMsat == long.MinValue ? long.MaxValue : Math.Abs(posting.AmountMsat);
            if (size <= largest)
                continue;

            largest = size;
            role = posting.Account;
        }

        return role;
    }

    private static bool Matches(CompiledRule compiled, AccountingEntry entry, AccountingEventModel e,
                                List<long> timedOut)
    {
        var rule = compiled.Rule;
        if (rule.Kinds is { Count: > 0 } kinds && !KindMatches(kinds, e))
            return false;

        if (rule.Counterparty is { } counterparty && (e.Counterparty is not { } other || other != counterparty))
            return false;

        if (rule.ChannelId is { } channelId && !ChannelMatches(channelId.ToString(), entry, e))
            return false;

        if (rule.OfferId is { } offerId
         && !(e.Details.TryGetValue(OfferIdDetail, out var offer)
           && string.Equals(offer, offerId.ToString(), StringComparison.OrdinalIgnoreCase)))
            return false;

        if (rule.TagKey is { } key)
        {
            if (!e.Details.TryGetValue(TagDetailPrefix + key, out var value))
                return false;
            if (rule.TagValue is { } glob && !GlobMatches(glob, value))
                return false;
        }

        if (compiled.Regex is { } regex)
        {
            if (!e.Details.TryGetValue(LabelDetail, out var label))
                return false;

            try
            {
                if (!regex.IsMatch(label))
                    return false;
            }
            catch (RegexMatchTimeoutException)
            {
                timedOut.Add(rule.Id);
                return false;
            }
        }

        return true;
    }

    private static bool KindMatches(IReadOnlyList<AccountingEventKind> kinds, AccountingEventModel e)
    {
        if (kinds.Contains(e.Kind))
            return true;

        return e.Kind == AccountingEventKind.Reversal
            && e.Details.TryGetValue(AccountingConfirmations.OriginalKindDetail, out var original)
            && Enum.TryParse<AccountingEventKind>(original, ignoreCase: false, out var originalKind)
            && kinds.Contains(originalKind);
    }

    private static bool ChannelMatches(string channelId, AccountingEntry entry, AccountingEventModel e)
    {
        if ((e.ChannelId ?? entry.ChannelId) is { } own
         && string.Equals(own.ToString(), channelId, StringComparison.OrdinalIgnoreCase))
            return true;

        return (e.Details.TryGetValue(IncomingChannelDetail, out var incoming)
             && string.Equals(incoming, channelId, StringComparison.OrdinalIgnoreCase))
            || (e.Details.TryGetValue(OutgoingChannelDetail, out var outgoing)
             && string.Equals(outgoing, channelId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether <paramref name="text"/> matches <paramref name="glob"/>: <c>*</c> any run (empty included), <c>?</c>
    /// exactly one character, everything else literal and case-sensitive. Linear in practice (one backtrack point).
    /// </summary>
    public static bool GlobMatches(string glob, string text)
    {
        ArgumentNullException.ThrowIfNull(glob);
        ArgumentNullException.ThrowIfNull(text);

        int g = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (g < glob.Length && (glob[g] == '?' || glob[g] == text[t]) && glob[g] != '*')
            {
                g++;
                t++;
            }
            else if (g < glob.Length && glob[g] == '*')
            {
                star = g++;
                mark = t;
            }
            else if (star >= 0)
            {
                g = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (g < glob.Length && glob[g] == '*')
            g++;

        return g == glob.Length;
    }

    // The paying side of a rebalance whose Rebalance line carries the route fee (NL-609): Dr Rebalance (a + fee)
    private static bool IsRebalanceWithFee(AccountingPosting posting, AccountingEventModel accountingEvent) =>
        posting.Account == AccountRole.Rebalance
     && accountingEvent.Kind == AccountingEventKind.PaymentSucceeded
     && IsSet(accountingEvent, AccountingDetailKeys.SelfPayment)
     && accountingEvent.FeeMsat > 0
     && posting.AmountMsat >= accountingEvent.FeeMsat;

    private static bool IsSet(AccountingEventModel e, string key) =>
        e.Details.TryGetValue(key, out var value) && value == AccountingDetailKeys.True;

    private sealed record CompiledRule(AccountingRule Rule, Regex? Regex);
}