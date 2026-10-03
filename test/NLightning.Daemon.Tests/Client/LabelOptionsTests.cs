namespace NLightning.Daemon.Tests.Client;

using Domain.Crypto.ValueObjects;
using Domain.Payments.Enums;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of the operator's label and tags (NL-602 A3-T1): <c>--label</c> and repeatable <c>--tag</c> on the
/// seven commands that take them, checked with the daemon's rules before anything is sent, and printed by the list
/// commands.
/// </summary>
public class LabelOptionsTests
{
    private const string Address = "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080";
    private const string NodeId = "0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c";

    [Fact]
    public void Given_LabelAndTagsInBothForms_When_Extracted_Then_TheRestKeepsItsOrderAndTheTagsAreSorted()
    {
        // Arrange
        string[] args = ["1000", "--tag", "project=alpha", "coffee", "--label=café", "--TAG=customer=a=b", "60"];

        // Act
        var rest = LabelOptions.Extract(args, out var labels, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(["1000", "coffee", "60"], rest!);
        Assert.Equal("café", labels.Label);
        Assert.Equal(["customer=a=b", "project=alpha"], labels.Tags);
        Assert.Equal(["customer=a=b", "project=alpha"], labels.TagsOrNull);
    }

    [Fact]
    public void Given_NoLabelOrTag_When_Extracted_Then_NoneAndTheArgumentsUnchanged()
    {
        // Act
        var rest = LabelOptions.Extract(["lnbcrt1", "--max-parts", "2"], out var labels, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(["lnbcrt1", "--max-parts", "2"], rest!);
        Assert.Same(LabelArguments.None, labels);
        Assert.Null(labels.TagsOrNull);
    }

    [Theory]
    [InlineData("--label", "Missing value for --label.")]
    [InlineData("--label a --label b", "--label is given more than once.")]
    [InlineData("--tag novalue", "must be key=value")]
    [InlineData("--tag Key=v", "may only hold a-z")]
    [InlineData("--tag k=1 --tag k=2", "more than once")]
    [InlineData("--tag =v", "must not be empty")]
    public void Given_ABrokenOption_When_Extracted_Then_AnErrorAndNoArguments(string arguments, string expected)
    {
        // Act
        var rest = LabelOptions.Extract(arguments.Split(' '), out var labels, out var error);

        // Assert
        Assert.Null(rest);
        Assert.Same(LabelArguments.None, labels);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Given_SeventeenTags_When_Extracted_Then_RefusedWithTheLimit()
    {
        // Arrange
        var args = Enumerable.Range(0, 17).SelectMany(i => new[] { "--tag", $"k{i}=v" }).ToArray();

        // Act
        var rest = LabelOptions.Extract(args, out _, out var error);

        // Assert
        Assert.Null(rest);
        Assert.Contains("At most 16 tags", error);
    }

    [Theory]
    [InlineData("createinvoice", "1000 coffee 60 --label shop --tag till=2")]
    [InlineData("addinvoice", "--label shop any")]
    [InlineData("payinvoice", "lnbcrt1 --tag category=supplies --max-parts 2")]
    [InlineData("pay", "lnbcrt1 any 30 --label supplier")]
    [InlineData("keysend", NodeId + " 21 --label tip --tlv 65537=00")]
    [InlineData("createoffer", "any --label shop --issuer acme")]
    [InlineData("payoffer", "lno1qgsqvgnwgcg35z6ee2h3yczraddm72xrfua9uve2rlrm9deu7xyfzrc 1000 --tag vendor=acme")]
    [InlineData("withdraw", Address + " 40000 --label cold --sat-per-vb 2")]
    [InlineData("openchannel", NodeId + " 100000 --public --label routing --tag peer=acme")]
    public void Given_ALabelledCommand_When_Validated_Then_TheLabelAndTagsAreAccepted(string cmd, string arguments)
    {
        // Act
        var error = ClientApp.ValidateArguments(cmd, arguments.Split(' '));

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData("createinvoice", "1000 --tag Bad=1")]
    [InlineData("payinvoice", "lnbcrt1 --tag k")]
    [InlineData("keysend", NodeId + " 21 --label")]
    [InlineData("withdraw", Address + " all --tag a=1 --tag a=2")]
    [InlineData("openchannel", NodeId + " 100000 --tag peer:acme")]
    public void Given_ABrokenLabelOrTag_When_Validated_Then_UsageErrorBeforeTheCommandParser(string cmd,
        string arguments)
    {
        // Act
        var error = ClientApp.ValidateArguments(cmd, arguments.Split(' '));

        // Assert
        Assert.NotNull(error);
        Assert.Contains(LabelOptions.Usage, error);
    }

    [Fact]
    public void Given_ACommandWithoutLabels_When_ALabelIsGiven_Then_ItIsNotTakenOut()
    {
        // Act: listinvoices does not take --label, so its own parser sees (and refuses) it
        var error = ClientApp.ValidateArguments("listinvoices", ["--label", "x"]);

        // Assert
        Assert.NotNull(error);
        Assert.False(LabelOptions.IsLabelledCommand("listinvoices"));
        Assert.True(LabelOptions.IsLabelledCommand("sendcoins"));
    }

    [Fact]
    public void Given_AnInvoiceAndAPaymentWithLabels_When_Printed_Then_TheLabelAndEachTagAreShown()
    {
        // Arrange
        var hash = new Hash(new byte[32]);
        var invoice = new InvoiceInfoIpcResponse
        {
            PaymentHash = hash,
            Status = InvoiceStatus.Open,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ExpiresAt = DateTimeOffset.UnixEpoch,
            Label = "rent",
            Tags = ["month=2026-10", "unit=4b"]
        };
        var plain = new InvoiceInfoIpcResponse
        {
            PaymentHash = hash,
            Status = InvoiceStatus.Open,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ExpiresAt = DateTimeOffset.UnixEpoch
        };

        // Act
        var labelled = Print(w => new ListInvoicesPrinter(w).Print(new ListInvoicesIpcResponse { Invoices = [invoice] }));
        var unlabelled = Print(w => new ListInvoicesPrinter(w).Print(new ListInvoicesIpcResponse { Invoices = [plain] }));

        // Assert
        Assert.Contains("  Label:              rent\n", labelled);
        Assert.Contains("  Tag:                month=2026-10\n  Tag:                unit=4b\n", labelled);
        Assert.DoesNotContain("Label:", unlabelled);
        Assert.DoesNotContain("Tag:", unlabelled);
    }

    private static string Print(Action<TextWriter> print)
    {
        using var writer = new StringWriter { NewLine = "\n" };
        print(writer);
        return writer.ToString();
    }
}