namespace NLightning.Domain.Tests.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// The financial classification engine (NL-602 A3-T3, D-A10): every match field, priority and id order, disabled and
/// invalid rules, the label regex timeout, override precedence, the defaults of the chart and the mapped lines.
/// </summary>
public class ClassificationEngineTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly CompactPubKey s_alice = Key(0x02, 0x11);
    private static readonly CompactPubKey s_bob = Key(0x03, 0x22);
    private static readonly ChannelId s_channelA = Channel(0xA1);
    private static readonly ChannelId s_channelB = Channel(0xB2);
    private static readonly Hash s_offer = new(Enumerable.Repeat((byte)0x0F, 32).ToArray());

    public static TheoryData<string, AccountingRule, bool> MatchCases => new()
    {
        { "kind matches", Rule(kinds: [AccountingEventKind.InvoiceSettled]), true },
        { "kind differs", Rule(kinds: [AccountingEventKind.PaymentSucceeded]), false },
        { "one of several kinds", Rule(kinds: [AccountingEventKind.PaymentSucceeded, AccountingEventKind.InvoiceSettled]), true },
        { "label regex matches", Rule(label: "^consult"), true },
        { "label regex differs", Rule(label: "payroll"), false },
        { "label regex inline case-insensitive", Rule(label: "(?i)CONSULTING"), true },
        { "tag key present", Rule(tagKey: "customer"), true },
        { "tag key absent", Rule(tagKey: "project"), false },
        { "tag glob star", Rule(tagKey: "customer", tagValue: "acme*"), true },
        { "tag glob question mark", Rule(tagKey: "customer", tagValue: "acme-?td"), true },
        { "tag glob differs", Rule(tagKey: "customer", tagValue: "globex*"), false },
        { "tag glob is case-sensitive", Rule(tagKey: "customer", tagValue: "ACME*"), false },
        { "counterparty matches", Rule(counterparty: s_alice), true },
        { "counterparty differs", Rule(counterparty: s_bob), false },
        { "offer id matches", Rule(offerId: s_offer), true },
        { "offer id differs", Rule(offerId: new Hash(new byte[32])), false },
        { "channel matches", Rule(channelId: s_channelA), true },
        { "channel differs", Rule(channelId: s_channelB), false },
        { "every field matches", Rule(kinds: [AccountingEventKind.InvoiceSettled], label: "consult", tagKey: "customer",
                                      tagValue: "*", counterparty: s_alice, offerId: s_offer, channelId: s_channelA),
          true },
        { "every field but one matches", Rule(kinds: [AccountingEventKind.InvoiceSettled], label: "consult",
                                              tagKey: "customer", counterparty: s_bob, channelId: s_channelA), false },
        { "no field matches anything", Rule(), true }
    };

    [Theory]
    [MemberData(nameof(MatchCases))]
    public void Given_ARule_When_AnInvoiceIsClassified_Then_ItMatchesOnlyWhenEverySetFieldMatches(
        string name, AccountingRule rule, bool expected)
    {
        // Arrange
        var (entry, accountingEvent) = Invoice(label: "consulting october",
                                               tags: [("customer", "acme-ltd")]);
        var engine = new ClassificationEngine(FinancialChart.Default, [rule]);

        // Act
        var result = engine.Classify(entry, accountingEvent, null);

        // Assert
        Assert.True(expected == (result.Source == AccountingClassificationSource.Rule), name);
        Assert.Equal(expected ? "income:consulting" : "income:sales", result.Account);
        Assert.Equal(expected ? rule.Id : null, result.RuleId);
    }

    [Fact]
    public void Given_SeveralMatchingRules_When_Classified_Then_TheLowestPriorityThenTheLowestIdWins()
    {
        // Arrange
        var (entry, accountingEvent) = Invoice(label: "consulting");
        AccountingRule[] rules =
        [
            Rule(id: 1, priority: 20, target: "income:late"),
            Rule(id: 3, priority: 10, target: "income:second"),
            Rule(id: 2, priority: 10, target: "income:first"),
            Rule(id: 4, priority: 30, target: "income:last")
        ];
        var engine = new ClassificationEngine(FinancialChart.Default, rules);

        // Act
        var result = engine.Classify(entry, accountingEvent, null);

        // Assert
        Assert.Equal("income:first", result.Account);
        Assert.Equal(2, result.RuleId);
        Assert.Equal("rule 2", result.Reason);
    }

    [Fact]
    public void Given_ADisabledRuleFirst_When_Classified_Then_TheNextEnabledRuleWins()
    {
        // Arrange
        var (entry, accountingEvent) = Invoice();
        var engine = new ClassificationEngine(FinancialChart.Default,
                                              [
                                                  Rule(id: 1, priority: 1, target: "income:disabled") with
                                                  {
                                                      Enabled = false
                                                  },
                                                  Rule(id: 2, priority: 2, target: "income:enabled")
                                              ]);

        // Act
        var result = engine.Classify(entry, accountingEvent, null);

        // Assert
        Assert.Equal("income:enabled", result.Account);
        Assert.Equal(2, result.RuleId);
    }

    [Fact]
    public void Given_AnOverrideAndAMatchingRule_When_Classified_Then_TheOverrideWins()
    {
        // Arrange
        var (entry, accountingEvent) = Invoice(label: "consulting");
        var engine = new ClassificationEngine(FinancialChart.Default, [Rule(id: 1, target: "income:consulting")]);
        var accountingOverride = new AccountingOverride(accountingEvent.EventKey, "income:gifts", "birthday", s_at);

        // Act
        var result = engine.Classify(entry, accountingEvent, accountingOverride);
        var lines = engine.MapPostings(entry, accountingEvent, result);

        // Assert
        Assert.Equal(AccountingClassificationSource.Override, result.Source);
        Assert.Equal("income:gifts", result.Account);
        Assert.Null(result.RuleId);
        Assert.Equal("override", result.Reason);
        Assert.Equal(["assets:lightning:channels", "income:gifts"], lines.Select(l => l.AccountName));
    }

    [Fact]
    public void Given_ALabelPatternThatRunsOutOfTime_When_Classified_Then_ItDoesNotMatchAndIsReported()
    {
        // Arrange: a label far longer than a real one (256 bytes) and a timeout of one tick
        var (entry, accountingEvent) = Invoice(label: new string('a', 2_000_000));
        var engine = new ClassificationEngine(FinancialChart.Default,
                                              [
                                                  Rule(id: 7, priority: 1, label: "(a|aa)*b", target: "income:slow"),
                                                  Rule(id: 8, priority: 2, kinds: [AccountingEventKind.InvoiceSettled],
                                                       target: "income:fallback")
                                              ], TimeSpan.FromTicks(1));

        // Act
        var result = engine.Classify(entry, accountingEvent, null);

        // Assert
        Assert.Equal([7L], result.TimedOutRuleIds);
        Assert.Equal("income:fallback", result.Account);
        Assert.Equal(8, result.RuleId);
    }

    [Fact]
    public void Given_TheDefaultTimeout_When_AShortLabelIsMatched_Then_ThePatternRunsWithoutTimingOut()
    {
        // Arrange
        var (entry, accountingEvent) = Invoice(label: new string('a', AccountingSchemaLimits.LabelMaxBytes));
        var engine = new ClassificationEngine(FinancialChart.Default, [Rule(id: 1, label: "(a|aa)*b")]);

        // Act
        var result = engine.Classify(entry, accountingEvent, null);

        // Assert
        Assert.Empty(result.TimedOutRuleIds);
        Assert.Equal(AccountingClassificationSource.Default, result.Source);
    }

    [Fact]
    public void Given_AnEventWithoutALabel_When_ARuleHasAPattern_Then_ItNeverMatches()
    {
        // Arrange
        var (entry, accountingEvent) = Invoice();
        var engine = new ClassificationEngine(FinancialChart.Default, [Rule(id: 1, label: ".*")]);

        // Act
        var result = engine.Classify(entry, accountingEvent, null);

        // Assert
        Assert.Equal(AccountingClassificationSource.Default, result.Source);
    }

    [Fact]
    public void Given_StoredRulesThatCannotRun_When_TheEngineIsBuilt_Then_TheyAreListedAndNeverMatch()
    {
        // Arrange: a backreference (refused by the non-backtracking engine) and an asset target
        var (entry, accountingEvent) = Invoice(label: "aa");
        AccountingRule[] rules =
        [
            Rule(id: 1, priority: 1, label: "(a)\\1", target: "income:never"),
            Rule(id: 2, priority: 2, target: "assets:lightning:channels"),
            Rule(id: 3, priority: 3, target: "income:valid")
        ];

        // Act
        var engine = new ClassificationEngine(FinancialChart.Default, rules);
        var result = engine.Classify(entry, accountingEvent, null);

        // Assert
        Assert.Equal([1L, 2L], engine.InvalidRuleIds);
        Assert.Equal("income:valid", result.Account);
    }

    public static TheoryData<AccountingEventKind, long, long, string, bool> DefaultCases => new()
    {
        // kind, amount, fee, the classified account, unclassified
        { AccountingEventKind.InvoiceSettled, 1_000, 0, "income:sales", false },
        { AccountingEventKind.PaymentSucceeded, -1_010, 10, "expenses:payments", false },
        { AccountingEventKind.ForwardSettled, 5, 0, "income:routing", false },
        { AccountingEventKind.PushReceived, 2_000, 0, "income:unclassified", true },
        { AccountingEventKind.PushSent, -2_000, 0, "expenses:unclassified", true },
        { AccountingEventKind.ForwardLostOnchain, -700, 0, "expenses:losses", false },
        { AccountingEventKind.WalletReceived, 9_000, 0, "equity:transfers:in", false },
        { AccountingEventKind.WalletSent, -9_000, 100, "equity:transfers:out", false }
    };

    [Theory]
    [MemberData(nameof(DefaultCases))]
    public void Given_NoRuleOrOverride_When_Classified_Then_TheDefaultOfTheLineRoleApplies(
        AccountingEventKind kind, long amount, long fee, string expected, bool unclassified)
    {
        // Arrange
        var accountingEvent = Event(kind, amount, fee,
                                    kind == AccountingEventKind.WalletReceived
                                        ? [(AccountingDetailKeys.Source, AccountingDetailKeys.ExternalSource)]
                                        : []);
        var entry = Entry(accountingEvent);
        var engine = new ClassificationEngine(FinancialChart.Default, []);

        // Act
        var result = engine.Classify(entry, accountingEvent, null);
        var lines = engine.MapPostings(entry, accountingEvent, result);

        // Assert
        Assert.Equal(AccountingClassificationSource.Default, result.Source);
        Assert.Equal(expected, result.Account);
        Assert.Equal(unclassified, result.IsUnclassified);
        Assert.Contains(lines, l => l.AccountName == expected);
        Assert.Equal(0, lines.Sum(l => l.AmountMsat));
        Assert.All(lines, l => Assert.True(FinancialAccountNameRules.TryValidate(l.AccountName, out _)));
    }

    [Fact]
    public void Given_ARebalance_When_Classified_Then_BothHalvesGoToTheRebalanceTransferAndOnlyTheFeeIsAnExpense()
    {
        // Arrange
        var paid = Event(AccountingEventKind.PaymentSucceeded, -1_010, 10,
                         [(AccountingDetailKeys.SelfPayment, AccountingDetailKeys.True)]);
        var received = Event(AccountingEventKind.InvoiceSettled, 1_000, 0,
                             [(AccountingDetailKeys.SelfPayment, AccountingDetailKeys.True)]);
        var engine = new ClassificationEngine(FinancialChart.Default, []);

        // Act
        var paidLines = engine.MapPostings(Entry(paid), paid, engine.Classify(Entry(paid), paid, null));
        var receivedLines = engine.MapPostings(Entry(received), received,
                                               engine.Classify(Entry(received), received, null));

        // Assert
        Assert.Contains(paidLines, l => l is { AccountName: "equity:transfers:rebalance", AmountMsat: 1_000 });
        Assert.Contains(paidLines, l => l is { AccountName: "expenses:fees:routing", AmountMsat: 10 });
        Assert.Contains(receivedLines, l => l is { AccountName: "equity:transfers:rebalance", AmountMsat: -1_000 });
    }

    [Fact]
    public void Given_AnEntryWithoutAClassifiableLine_When_Classified_Then_NothingIsLookedAtAndLinesKeepTheChart()
    {
        // Arrange: a mutual close moves the channel balance to clearing and pays a fee
        var accountingEvent = Event(AccountingEventKind.ChannelClosedMutual, -5_000, 300);
        var entry = Entry(accountingEvent);
        var engine = new ClassificationEngine(FinancialChart.Default, [Rule(id: 1, target: "income:any")]);
        var accountingOverride = new AccountingOverride(accountingEvent.EventKey, "income:ignored", null, s_at);

        // Act
        var result = engine.Classify(entry, accountingEvent, accountingOverride);
        var lines = engine.MapPostings(entry, accountingEvent, result);

        // Assert
        Assert.Null(result.Account);
        Assert.False(result.HasClassifiableLine);
        Assert.False(result.IsUnclassified);
        Assert.Equal(["assets:lightning:channels", "assets:onchain:clearing", "expenses:fees:close"],
                     lines.Select(l => l.AccountName).Order());
    }

    [Fact]
    public void Given_AReversal_When_ARuleMatchesTheKindItReverses_Then_TheReversalFollowsIt()
    {
        // Arrange
        var accountingEvent = Event(AccountingEventKind.Reversal, -9_000, 0,
                                    [
                                        (AccountingConfirmations.OriginalKindDetail,
                                         nameof(AccountingEventKind.WalletReceived)),
                                        (AccountingConfirmations.ReversesDetail, "wallet:x")
                                    ]);
        var entry = Entry(accountingEvent,
                          [new AccountingPosting(AccountRole.Wallet, -9_000),
                              new AccountingPosting(AccountRole.TransfersIn, 9_000)]);
        var engine = new ClassificationEngine(FinancialChart.Default,
                                              [Rule(id: 4, kinds: [AccountingEventKind.WalletReceived],
                                                    target: "income:customer-deposits")]);

        // Act
        var result = engine.Classify(entry, accountingEvent, null);

        // Assert
        Assert.Equal("income:customer-deposits", result.Account);
        Assert.Equal(4, result.RuleId);
    }

    [Fact]
    public void Given_AForward_When_ARuleNamesItsOutgoingChannel_Then_ItMatches()
    {
        // Arrange
        var accountingEvent = Event(AccountingEventKind.ForwardSettled, 5, 0,
                                    [("incomingChannelId", s_channelA.ToString()),
                                        ("outgoingChannelId", s_channelB.ToString())]);
        var engine = new ClassificationEngine(FinancialChart.Default,
                                              [Rule(id: 1, channelId: s_channelB, target: "income:routing-b")]);

        // Act
        var result = engine.Classify(Entry(accountingEvent), accountingEvent, null);

        // Assert
        Assert.Equal("income:routing-b", result.Account);
    }

    [Fact]
    public void Given_ARuleTargetingAnUnclassifiedAccount_When_Classified_Then_TheEntryIsUnclassified()
    {
        // Arrange
        var (entry, accountingEvent) = Invoice();
        var engine = new ClassificationEngine(FinancialChart.Default, [Rule(id: 1, target: "income:unclassified")]);

        // Act
        var result = engine.Classify(entry, accountingEvent, null);

        // Assert
        Assert.True(result.IsUnclassified);
        Assert.Equal(AccountingClassificationSource.Rule, result.Source);
    }

    [Fact]
    public void Given_ACandidateRule_When_TestedAlone_Then_ItsMatchIsReportedEvenWhenDisabled()
    {
        // Arrange
        var (entry, accountingEvent) = Invoice(label: "consulting");
        var engine = new ClassificationEngine(FinancialChart.Default, []);

        // Act
        var matches = engine.RuleMatches(Rule(label: "consult") with { Enabled = false }, entry, accountingEvent,
                                         out var timedOut);
        var differs = engine.RuleMatches(Rule(label: "payroll"), entry, accountingEvent, out _);

        // Assert
        Assert.True(matches);
        Assert.False(timedOut);
        Assert.False(differs);
    }

    [Theory]
    [InlineData("acme*", "acme-ltd", true)]
    [InlineData("*ltd", "acme-ltd", true)]
    [InlineData("a*e*d", "acme-ltd", true)]
    [InlineData("acme-?td", "acme-ltd", true)]
    [InlineData("*", "", true)]
    [InlineData("?", "", false)]
    [InlineData("acme", "acme-ltd", false)]
    [InlineData("a*z", "acme-ltd", false)]
    [InlineData("**ltd", "acme-ltd", true)]
    [InlineData("[a]cme", "[a]cme", true)]
    public void Given_AGlob_When_Matched_Then_StarAndQuestionMarkAreTheOnlyWildcards(string glob, string text,
                                                                                    bool expected)
    {
        // Act
        var result = ClassificationEngine.GlobMatches(glob, text);

        // Assert
        Assert.Equal(expected, result);
    }

    private static (AccountingEntry Entry, AccountingEventModel Event) Invoice(
        string? label = null, (string Key, string Value)[]? tags = null)
    {
        var details = new List<(string, string)> { (ClassificationEngine.OfferIdDetail, s_offer.ToString()) };
        if (label is not null)
            details.Add((ClassificationEngine.LabelDetail, label));
        foreach (var (key, value) in tags ?? [])
            details.Add((ClassificationEngine.TagDetailPrefix + key, value));

        var accountingEvent = Event(AccountingEventKind.InvoiceSettled, 1_000, 0, [.. details]);
        return (Entry(accountingEvent), accountingEvent);
    }

    private static AccountingEventModel Event(AccountingEventKind kind, long amount, long fee,
                                              (string Key, string Value)[]? details = null) =>
        new()
        {
            EventKey = $"{kind}:{amount}",
            Kind = kind,
            OccurredAt = s_at,
            ChannelId = s_channelA,
            Counterparty = s_alice,
            AmountMsat = amount,
            FeeMsat = fee,
            Details = AccountingDetailsCodec.Create((details ?? []).Select(d => (d.Key, (string?)d.Value)).ToArray()),
            LedgerSeq = 1
        };

    private static AccountingEntry Entry(AccountingEventModel accountingEvent,
                                         IReadOnlyList<AccountingPosting>? postings = null) =>
        new(1, accountingEvent.EventKey, accountingEvent.Kind, accountingEvent.OccurredAt, accountingEvent.ChannelId,
            null, postings ?? AccountingPostingRules.Post(accountingEvent, _ => null));

    private static AccountingRule Rule(long id = 9, int priority = 0, AccountingEventKind[]? kinds = null,
                                       string? label = null, string? tagKey = null, string? tagValue = null,
                                       CompactPubKey? counterparty = null, Hash? offerId = null,
                                       ChannelId? channelId = null, string target = "income:consulting") =>
        new(id, priority, kinds, label, tagKey, tagValue, counterparty, offerId, channelId, target, true, s_at);

    private static CompactPubKey Key(byte prefix, byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 33).ToArray();
        bytes[0] = prefix;
        return new CompactPubKey(bytes);
    }

    private static ChannelId Channel(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());
}