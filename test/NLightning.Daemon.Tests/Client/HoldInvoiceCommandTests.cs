namespace NLightning.Daemon.Tests.Client;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of the hold invoice verbs (NL-995, Cashu plan C4): createholdinvoice, settleholdinvoice and
/// cancelholdinvoice, in both their spellings.
/// </summary>
public class HoldInvoiceCommandTests
{
    private const string HashHex = "abababababababababababababababababababababababababababababababab";
    private const string PreimageHex = "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd";

    [Fact]
    public void Given_OnlyAPaymentHash_When_CreateIsParsed_Then_AnyAmountNoDescriptionNoExpiry()
    {
        // Act
        var parsed = HoldInvoiceCommands.ParseCreate([HashHex], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(HashHex, parsed!.Value.PaymentHash.ToString());
        Assert.Null(parsed.Value.Amount);
        Assert.Equal(string.Empty, parsed.Value.Description);
        Assert.Null(parsed.Value.ExpirySeconds);
        Assert.Null(ClientApp.ValidateArguments("createholdinvoice", [HashHex]));
        Assert.Null(ClientApp.ValidateArguments("create-hold-invoice", [HashHex]));
    }

    [Theory]
    [InlineData(new[] { HashHex, "any" }, null)]
    [InlineData(new[] { HashHex, "ANY" }, null)]
    [InlineData(new[] { HashHex, "50000123" }, 50_000_123UL)]
    public void Given_AnAmount_When_CreateIsParsed_Then_ItIsMsatOrAny(string[] args, ulong? expectedMsat)
    {
        // Act - createinvoice's convention: msat, `any` for an any-amount invoice, never 0
        var parsed = HoldInvoiceCommands.ParseCreate(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(expectedMsat, parsed!.Value.Amount?.MilliSatoshi);
    }

    [Fact]
    public void Given_AmountDescriptionAndExpiry_When_CreateIsParsed_Then_AllAreRead()
    {
        // Act
        var parsed = HoldInvoiceCommands.ParseCreate(
            [HashHex, "1000", "two words", "--expiry", "3600"], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(1_000UL, parsed!.Value.Amount!.MilliSatoshi);
        Assert.Equal("two words", parsed.Value.Description);
        Assert.Equal(3_600U, parsed.Value.ExpirySeconds);
        Assert.Null(ClientApp.ValidateArguments("createholdinvoice",
                                                [HashHex, "1000", "two words", "--expiry", "3600"]));
    }

    [Fact]
    public void Given_TheExpiryAnywhereAndInline_When_CreateIsParsed_Then_ItIsRead()
    {
        // Act
        var before = HoldInvoiceCommands.ParseCreate(["--expiry=120", HashHex], out var beforeError);
        var between = HoldInvoiceCommands.ParseCreate([HashHex, "--expiry", "120", "1000"], out var betweenError);

        // Assert
        Assert.Null(beforeError);
        Assert.Equal(120U, before!.Value.ExpirySeconds);
        Assert.Null(betweenError);
        Assert.Equal(120U, between!.Value.ExpirySeconds);
        Assert.Equal(1_000UL, between.Value.Amount!.MilliSatoshi);
    }

    [Fact]
    public void Given_APaymentHashAndAPreimage_When_SettleIsParsed_Then_BothBytesAreRead()
    {
        // Act
        var parsed = HoldInvoiceCommands.ParseSettle([HashHex, PreimageHex], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(HashHex, parsed!.Value.PaymentHash.ToString());
        Assert.Equal(PreimageHex, Convert.ToHexString((byte[])parsed.Value.Preimage).ToLowerInvariant());
        Assert.Null(ClientApp.ValidateArguments("settleholdinvoice", [HashHex, PreimageHex]));
        Assert.Null(ClientApp.ValidateArguments("settle-hold-invoice", [HashHex, PreimageHex]));
    }

    [Fact]
    public void Given_APaymentHash_When_CancelIsParsed_Then_ItIsRead()
    {
        // Act
        var parsed = HoldInvoiceCommands.ParseCancel([HashHex], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(HashHex, parsed!.Value.ToString());
        Assert.Null(ClientApp.ValidateArguments("cancelholdinvoice", [HashHex]));
        Assert.Null(ClientApp.ValidateArguments("cancel-hold-invoice", [HashHex]));
    }

    public static TheoryData<string, string[]> BadArguments => new()
    {
        // createholdinvoice: the payment hash, the amount, the expiry
        { "createholdinvoice", [] },
        { "create-hold-invoice", ["abcd"] },
        { "createholdinvoice", [HashHex + "0"] },
        { "createholdinvoice", [HashHex, "0"] },
        { "createholdinvoice", [HashHex, "-5"] },
        { "createholdinvoice", [HashHex, "12abc"] },
        { "createholdinvoice", [HashHex, "1000", "desc", "extra"] },
        { "createholdinvoice", [HashHex, "--expiry"] },
        { "createholdinvoice", [HashHex, "--expiry", "0"] },
        { "createholdinvoice", [HashHex, "--fast"] },
        // settleholdinvoice: the payment hash and the preimage
        { "settleholdinvoice", [] },
        { "settle-hold-invoice", [HashHex] },
        { "settleholdinvoice", ["zz" + HashHex[..62], PreimageHex] },
        { "settleholdinvoice", [HashHex, "nope"] },
        { "settleholdinvoice", [HashHex, PreimageHex + "0"] },
        { "settleholdinvoice", [HashHex, PreimageHex, "extra"] },
        // cancelholdinvoice: the payment hash
        { "cancelholdinvoice", [] },
        { "cancel-hold-invoice", ["short"] },
        { "cancelholdinvoice", [HashHex, "extra"] }
    };

    [Theory]
    [MemberData(nameof(BadArguments))]
    public void Given_BadArguments_When_Validated_Then_UsageError(string cmd, string[] args)
    {
        // Act
        var error = ClientApp.ValidateArguments(cmd, args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("Usage: ", error);
    }

    [Fact]
    public void Given_ANonHexPaymentHash_When_Validated_Then_TheErrorNamesIt()
    {
        // Act
        var create = ClientApp.ValidateArguments("createholdinvoice", ["nothex"]);
        var settle = ClientApp.ValidateArguments("settleholdinvoice", ["nothex", PreimageHex]);
        var cancel = ClientApp.ValidateArguments("cancelholdinvoice", ["nothex"]);

        // Assert
        Assert.Contains("Invalid payment hash 'nothex': expected 64 hex characters.", create);
        Assert.Contains("Invalid payment hash 'nothex'", settle);
        Assert.Contains("Invalid payment hash 'nothex'", cancel);
    }

    [Fact]
    public void Given_CreateHoldInvoiceWithALabel_When_Validated_Then_TheLabelIsAccepted()
    {
        // Act - NL-602 A3-T1: createholdinvoice is a labelled command; settle and cancel are not
        var error = ClientApp.ValidateArguments("createholdinvoice",
                                                [HashHex, "1000", "--label", "mint", "--tag", "nut=14"]);

        // Assert
        Assert.Null(error);
        Assert.True(LabelOptions.IsLabelledCommand("createholdinvoice"));
        Assert.True(LabelOptions.IsLabelledCommand("create-hold-invoice"));
        Assert.False(LabelOptions.IsLabelledCommand("settleholdinvoice"));
        Assert.False(LabelOptions.IsLabelledCommand("cancelholdinvoice"));
        // a label on settle/cancel is not taken out, so the verb's own parser refuses it
        Assert.NotNull(ClientApp.ValidateArguments("settleholdinvoice", [HashHex, PreimageHex, "--label", "x"]));
    }

    [Fact]
    public void Given_AHoldInvoiceAnswer_When_Printed_Then_TheInvoiceIsPrintedLikeCreateInvoice()
    {
        // Arrange
        var output = new StringWriter();
        var response = new HoldInvoiceIpcResponse
        {
            Invoice = new InvoiceInfoIpcResponse
            {
                PaymentHash = new Hash(Convert.FromHexString(HashHex)),
                Amount = LightningMoney.MilliSatoshis(50_000_123),
                Description = "coffee",
                Status = InvoiceStatus.Held,
                CreatedAt = DateTimeOffset.UnixEpoch,
                ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(1)
            }
        };

        // Act
        new HoldInvoicePrinter(output).Print(response);

        // Assert
        Assert.StartsWith("Invoice:", output.ToString());
        Assert.Contains(HashHex, output.ToString());
        Assert.Contains("Held", output.ToString());
    }
}