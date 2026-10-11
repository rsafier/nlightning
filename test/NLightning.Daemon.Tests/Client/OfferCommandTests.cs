namespace NLightning.Daemon.Tests.Client;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Enums;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of <c>createoffer</c>, <c>listoffers</c> and <c>disableoffer</c>: argument parsing and the printed
/// offer.
/// </summary>
public class OfferCommandTests
{
    private const string OfferId = "0101010101010101010101010101010101010101010101010101010101010101";

    [Fact]
    public void Given_AnAmountADescriptionAndOptions_When_Parsed_Then_AllAreRead()
    {
        // Arrange
        string[] args = ["10000", "coffee", "--issuer", "nltg", "--quantity-max=0", "--absolute-expiry", "1900000000",
                         "--paths"];

        // Act
        var parsed = OfferCommands.ParseCreateOfferOptions(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(LightningMoney.MilliSatoshis(10_000), parsed!.Amount);
        Assert.Equal("coffee", parsed.Description);
        Assert.Equal("nltg", parsed.Issuer);
        Assert.Equal(0UL, parsed.QuantityMax);
        Assert.Equal(1_900_000_000UL, parsed.AbsoluteExpiry);
        Assert.True(parsed.ForcePaths);
        Assert.Null(ClientApp.ValidateArguments("createoffer", args));
    }

    [Fact]
    public void Given_AnyAmountWithoutDescription_When_Parsed_Then_Accepted()
    {
        // Act
        var parsed = OfferCommands.ParseCreateOfferOptions(["any"], out var error);

        // Assert
        Assert.Null(error);
        Assert.Null(parsed!.Amount);
        Assert.Null(parsed.Description);
        Assert.False(parsed.ForcePaths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10000")] // an amount needs a description
    [InlineData("0|x")]
    [InlineData("any|x|y")]
    [InlineData("any|--quantity-max|many")]
    [InlineData("any|--absolute-expiry")]
    [InlineData("any|--paths=yes")]
    [InlineData("any|--unknown")]
    public void Given_BadCreateOfferArguments_When_Validated_Then_UsageError(string joined)
    {
        // Arrange
        var args = Split(joined);

        // Act
        var error = ClientApp.ValidateArguments("create-offer", args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("Usage: create-offer", error);
    }

    [Theory]
    [InlineData("", false, 100, 0)]
    [InlineData("--active|10|5", true, 10, 5)]
    [InlineData("20|--active", true, 20, 0)]
    public void Given_ListOffersArguments_When_Parsed_Then_TheyAreRead(string joined, bool active, int take,
                                                                      int skip)
    {
        // Arrange
        var args = Split(joined);

        // Act
        var parsed = OfferCommands.ParseListOffersOptions(args, ClientApp.MaxListCount, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal((active, take, skip), parsed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("x")]
    [InlineData("1|2|3")]
    public void Given_BadListOffersArguments_When_Validated_Then_UsageError(string joined)
    {
        // Arrange
        var args = Split(joined);

        // Act / Assert
        Assert.NotNull(ClientApp.ValidateArguments("listoffers", args));
    }

    [Theory]
    [InlineData(OfferId, true)]
    [InlineData("01", false)]
    [InlineData("", false)]
    public void Given_DisableOfferArguments_When_Validated_Then_AnOfferIdIsNeeded(string joined, bool valid)
    {
        // Arrange
        var args = Split(joined);

        // Act
        var error = ClientApp.ValidateArguments("disable-offer", args);

        // Assert
        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void Given_AnOffer_When_Printed_Then_TheLayoutIsStable()
    {
        // Arrange
        var output = new StringWriter { NewLine = "\n" };
        var offer = new OfferInfoIpcResponse
        {
            OfferId = new Hash(Convert.FromHexString(OfferId)),
            Bolt12 = "lno1qqqq",
            Description = "coffee",
            Amount = LightningMoney.MilliSatoshis(10_000),
            QuantityMax = 0,
            HasPaths = true,
            Status = OfferStatus.Active,
            IsActive = true,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            AbsoluteExpiry = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000),
            PaidInvoices = 2,
            UnpaidInvoices = 1
        };

        // Act
        new CreateOfferPrinter(output).Print(new CreateOfferIpcResponse { Offer = offer });

        // Assert
        Assert.Equal("Offer:\n"
                   + "  Offer:              lno1qqqq\n"
                   + $"  Offer Id:           {OfferId}\n"
                   + "  Amount (msat):      10000\n"
                   + "  Description:        coffee\n"
                   + "  Quantity Max:       unlimited\n"
                   + "  Status:             Active\n"
                   + "  Blinded Paths:      yes\n"
                   + "  Created:            2027-01-15 08:00:00Z\n"
                   + "  Expires:            2030-03-17 17:46:40Z\n"
                   + "  Invoices:           2 paid, 1 unpaid\n", output.ToString());
    }

    private static string[] Split(string joined) =>
        joined.Length == 0 ? [] : joined.Split('|');
}