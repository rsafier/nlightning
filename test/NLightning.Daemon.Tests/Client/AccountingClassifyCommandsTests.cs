namespace NLightning.Daemon.Tests.Client;

using Domain.Accounting.Enums;
using Domain.Client.Enums;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI's <c>accounting classify</c> subcommands (NL-602 A3-T3): rules, overrides and listings parsed into the admin
/// request, usage errors, and the printer.
/// </summary>
public class AccountingClassifyCommandsTests
{
    [Fact]
    public void Given_ARuleAddWithEveryOption_When_Parsed_Then_TheRuleCarriesThem()
    {
        // Act
        var arguments = AccountingBooksCommands.Parse(
            [
                "classify", "rule", "add", "--account", "income:consulting", "--priority", "-3", "--kind",
                "invoicesettled,12", "--label", "^inv", "--tag=customer=acme*", "--counterparty", "02aa", "--offer", "0f",
                "--channel", "33", "--disabled", "--description", "consulting work"
            ], out var error);

        // Assert
        Assert.Null(error);
        var admin = arguments!.Admin!;
        Assert.Equal((int)AccountingAdminAction.Classify, admin.Action);
        Assert.Equal((int)AccountingClassifyAction.RuleAdd, admin.Classify!.Action);
        var rule = admin.Classify.Rule!;
        Assert.Equal("income:consulting", rule.TargetAccount);
        Assert.Equal(-3, rule.Priority);
        Assert.Equal([(int)AccountingEventKind.InvoiceSettled, (int)AccountingEventKind.PushReceived], rule.Kinds);
        Assert.Equal("^inv", rule.LabelPattern);
        Assert.Equal("customer", rule.TagKey);
        Assert.Equal("acme*", rule.TagValue);
        Assert.Equal("02aa", rule.Counterparty);
        Assert.Equal("0f", rule.OfferId);
        Assert.Equal("33", rule.ChannelId);
        Assert.False(rule.Enabled);
        Assert.Equal("consulting work", rule.Description);
    }

    [Theory]
    [InlineData(new[] { "classify", "rule", "list" }, AccountingClassifyAction.RuleList)]
    [InlineData(new[] { "classify", "rule", "remove", "4" }, AccountingClassifyAction.RuleRemove)]
    [InlineData(new[] { "classify", "rule", "enable", "4" }, AccountingClassifyAction.RuleEnable)]
    [InlineData(new[] { "classify", "rule", "disable", "4" }, AccountingClassifyAction.RuleDisable)]
    [InlineData(new[] { "classify", "rule", "test", "inv:ab" }, AccountingClassifyAction.RuleTest)]
    [InlineData(new[] { "classify", "set", "inv:ab", "income:gifts" }, AccountingClassifyAction.Set)]
    [InlineData(new[] { "classify", "unset", "inv:ab" }, AccountingClassifyAction.Unset)]
    [InlineData(new[] { "classify", "list" }, AccountingClassifyAction.ListOverrides)]
    [InlineData(new[] { "classify", "list", "--unclassified" }, AccountingClassifyAction.ListUnclassified)]
    public void Given_AClassifySubcommand_When_Parsed_Then_ItsActionIsSent(string[] args,
                                                                          AccountingClassifyAction expected)
    {
        // Act
        var arguments = AccountingBooksCommands.Parse(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal((int)expected, arguments!.Admin!.Classify!.Action);
    }

    [Fact]
    public void Given_ATestWithMatchOptions_When_Parsed_Then_ACandidateRuleWithoutATargetIsSent()
    {
        // Act
        var test = AccountingBooksCommands.Parse(["classify", "rule", "test", "inv:ab", "--label", "x"], out _)!
                                          .Admin!.Classify!;
        var plain = AccountingBooksCommands.Parse(["classify", "rule", "test", "inv:ab"], out _)!.Admin!.Classify!;
        var set = AccountingBooksCommands.Parse(["classify", "set", "inv:ab", "income:gifts", "--note", "gift"],
                                                out _)!.Admin!.Classify!;
        var list = AccountingBooksCommands.Parse(["classify", "list", "--unclassified", "--after", "9", "--limit=5"],
                                                 out _)!.Admin!.Classify!;

        // Assert
        Assert.Equal("inv:ab", test.EventKey);
        Assert.Equal("x", test.Rule!.LabelPattern);
        Assert.Equal(string.Empty, test.Rule.TargetAccount);
        Assert.Null(plain.Rule);
        Assert.Equal("income:gifts", set.Account);
        Assert.Equal("gift", set.Note);
        Assert.Equal(9, list.AfterLedgerSeq);
        Assert.Equal(5, list.Limit);
    }

    [Theory]
    [InlineData(new[] { "classify" }, "Missing classify subcommand")]
    [InlineData(new[] { "classify", "tag" }, "Unknown classify subcommand")]
    [InlineData(new[] { "classify", "rule" }, "Missing rule subcommand")]
    [InlineData(new[] { "classify", "rule", "add", "--label", "x" }, "needs --account")]
    [InlineData(new[] { "classify", "rule", "add", "--account", "income:x", "--kind", "Coffee" }, "Unknown accounting event kind 'Coffee'")]
    [InlineData(new[] { "classify", "rule", "add", "--account", "income:x", "--tag", "=v" }, "Invalid tag")]
    [InlineData(new[] { "classify", "rule", "add", "--account", "income:x", "--disabled=yes" }, "takes no value")]
    [InlineData(new[] { "classify", "rule", "add", "--account", "income:x", "--account", "income:y" }, "given twice")]
    [InlineData(new[] { "classify", "rule", "remove" }, "takes one rule id")]
    [InlineData(new[] { "classify", "rule", "remove", "x" }, "Invalid rule id")]
    [InlineData(new[] { "classify", "rule", "test" }, "needs an event key")]
    [InlineData(new[] { "classify", "rule", "test", "k", "--account", "income:x" }, "need a match option")]
    [InlineData(new[] { "classify", "set", "k" }, "needs an event key and an account")]
    [InlineData(new[] { "classify", "unset" }, "takes one event key")]
    [InlineData(new[] { "classify", "list", "--unclassified", "--skip", "3" }, "--skip lists overrides")]
    [InlineData(new[] { "classify", "list", "--after", "3" }, "--after pages the unclassified")]
    [InlineData(new[] { "classify", "list", "--limit", "0" }, "Invalid limit")]
    public void Given_ABadClassifyArgument_When_Validated_Then_UsageError(string[] args, string expected)
    {
        // Act
        var error = ClientApp.ValidateArguments("accounting", args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expected, error);
        Assert.Contains("accounting classify rule add", error);
    }

    [Fact]
    public void Given_ClassifyAnswers_When_Printed_Then_RulesTestsAndTheUnclassifiedAreReadable()
    {
        // Arrange
        var output = new StringWriter();
        var printer = new AccountingAdminPrinter(output);

        // Act
        printer.Print(Admin(new AccountingClassifyIpcResponse
        {
            Action = (int)AccountingClassifyAction.RuleList,
            Profile = 1,
            Rules =
            [
                new AccountingRuleIpcModel
                {
                    Id = 3, Priority = 10, Kinds = [(int)AccountingEventKind.InvoiceSettled], LabelPattern = "^inv",
                    TagKey = "customer", TargetAccount = "income:consulting", Enabled = false
                }
            ],
            Warnings = ["Rule 9 can never match"]
        }));
        printer.Print(Admin(new AccountingClassifyIpcResponse
        {
            Action = (int)AccountingClassifyAction.RuleTest,
            Profile = 1,
            Test = new AccountingClassifyTestIpcResponse
            {
                LedgerSeq = 4,
                EventKey = "inv:ab",
                Kind = (int)AccountingEventKind.InvoiceSettled,
                OccurredAtUnixMilliseconds = 0,
                Account = "income:consulting",
                Source = 2,
                RuleId = 3,
                IsUnclassified = false,
                Reason = "rule 3",
                CandidateMatches = false,
                Lines = [new AccountingClassifyLineIpcResponse { Role = 10, AmountMsat = -1_000, AccountName = "income:consulting" }]
            }
        }));
        printer.Print(Admin(new AccountingClassifyIpcResponse
        {
            Action = (int)AccountingClassifyAction.ListUnclassified,
            Profile = 1,
            Unclassified =
            [
                new AccountingUnclassifiedIpcItem
                {
                    LedgerSeq = 7, EventKey = "push:1", Kind = (int)AccountingEventKind.PushReceived,
                    OccurredAtUnixMilliseconds = 0, AmountMsat = -2_000, Account = "income:unclassified",
                    Reason = "default for PushReceived", Label = "gift"
                }
            ],
            NextAfter = 7,
            HasMore = true,
            Scanned = 7
        }));

        // Assert
        var text = output.ToString();
        Assert.Contains("rule 3  priority 10  (disabled)  kind=InvoiceSettled label~/^inv/ tag:customer=* -> "
                      + "income:consulting", text);
        Assert.Contains("Warning: Rule 9 can never match", text);
        Assert.Contains("#4 InvoiceSettled inv:ab", text);
        Assert.Contains("-> income:consulting (rule 3)", text);
        Assert.Contains("(Received)", text);
        Assert.Contains("Candidate rule: does not match", text);
        Assert.Contains("PushReceived  push:1  -2000 msat  -> income:unclassified  [gift]", text);
        Assert.Contains("more after --after 7", text);
    }

    private static AccountingAdminIpcResponse Admin(AccountingClassifyIpcResponse classify) =>
        new() { Action = (int)AccountingAdminAction.Classify, Classify = classify };
}