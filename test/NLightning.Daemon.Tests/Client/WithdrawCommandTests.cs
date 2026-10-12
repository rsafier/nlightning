namespace NLightning.Daemon.Tests.Client;

using Domain.Bitcoin.ValueObjects;
using NLightning.Client;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of <c>withdraw</c> (ClientCommand 25): argument parsing and the printed result.
/// </summary>
public class WithdrawCommandTests
{
    private const string Address = "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080";

    [Fact]
    public void Given_AddressAndAmount_When_Parsed_Then_NoFeeRate()
    {
        // Act
        var parsed = ClientApp.ParseWithdrawOptions([Address, "40000"], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(Address, parsed!.Address);
        Assert.Equal(40_000UL, parsed.AmountSat);
        Assert.Null(parsed.SatPerVbyte);
        Assert.Null(ClientApp.ValidateArguments("withdraw", [Address, "40000"]));
    }

    [Theory]
    [InlineData("--sat-per-vb", "12")]
    [InlineData("--sat-per-vb=12", null)]
    public void Given_AllAndAFeeRateAnywhere_When_Parsed_Then_AllAtThatRate(string option, string? value)
    {
        // Arrange
        string[] args = value is null ? [option, Address, "ALL"] : [Address, option, value, "all"];

        // Act
        var parsed = ClientApp.ParseWithdrawOptions(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.Null(parsed!.AmountSat);
        Assert.Equal(12UL, parsed.SatPerVbyte);
        Assert.Null(ClientApp.ValidateArguments("sendcoins", args));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Given_UtxoOptions_When_Parsed_Then_TheyAreKeptInOrder(bool silentPayment, bool all)
    {
        // Arrange (NL-1296)
        var first = new string('a', 64) + ":0";
        var second = new string('b', 64) + ":12";
        var destination = silentPayment ? "sp1qqgste7k9hx0qftg6qmwlkqtwuy6cycyavzmzj85c6qdfhjdpdjtdgqjuexzk6murw56suy3e0rd2cgqvycxttddwsvgxe2usfpxumr70xc9pkqwv" : Address;
        string[] args = [destination, "--utxo", first, all ? "all" : "20000", $"--utxo={second}"];

        // Act
        var parsed = ClientApp.ParseWithdrawOptions(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(all ? (ulong?)null : 20_000UL, parsed!.AmountSat);
        Assert.Equal(destination, parsed.Address);
        Assert.Equal([first, second], parsed.Utxos);
        Assert.Null(ClientApp.ValidateArguments("withdraw", args));
    }

    [Theory]
    [InlineData("ADDR")]
    [InlineData("ADDR 0")]
    [InlineData("ADDR -5")]
    [InlineData("ADDR some")]
    [InlineData("ADDR 1000 extra")]
    [InlineData("ADDR 1000 --sat-per-vb")]
    [InlineData("ADDR 1000 --sat-per-vb 0")]
    [InlineData("ADDR 1000 --sat-per-vb 1001")]
    [InlineData("ADDR 1000 --fee 3")]
    [InlineData("ADDR all --utxo")]
    [InlineData("ADDR all --utxo abcd:0")]
    [InlineData("ADDR all --utxo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Given_InvalidArguments_When_Validated_Then_UsageError(string arguments)
    {
        // Arrange
        var args = arguments.Replace("ADDR", Address, StringComparison.Ordinal).Split(' ');

        // Act
        var error = ClientApp.ValidateArguments("withdraw", args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(ClientApp.WithdrawUsage, error);
    }

    [Fact]
    public void Given_APublishedWithdrawal_When_Printed_Then_MatchesSnapshot()
    {
        // Arrange
        var output = new StringWriter();
        var response = new WithdrawIpcResponse
        {
            TxId = new TxId(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
            AmountSat = 40_000,
            FeeSat = 566,
            ChangeSat = 59_434,
            FeeRatePerKw = 2_500,
            Weight = 561,
            InputCount = 1,
            AnchorReserveSat = 10_000,
            Published = true
        };

        // Act
        new WithdrawPrinter(output).Print(response);

        // Assert
        Assert.Equal(
            "Withdrawal broadcast\n"
          + "  TxId:           1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100\n"
          + "  Amount:         40000 sats\n"
          + "  Fee:            566 sats (2500 sat/kw, 141 vB)\n"
          + "  Change:         59434 sats\n"
          + "  Inputs:         1\n"
          + "  Anchor reserve: 10000 sats kept in the wallet\n",
            output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Given_ARefusedBroadcast_When_Printed_Then_SaysItIsSentAgain()
    {
        // Arrange
        var output = new StringWriter();
        var response = new WithdrawIpcResponse
        {
            TxId = new TxId(new byte[32]),
            AmountSat = 1_000,
            Published = false
        };

        // Act
        new WithdrawPrinter(output).Print(response);

        // Assert
        var text = output.ToString().ReplaceLineEndings(" ");
        Assert.StartsWith("Withdrawal stored, broadcast refused", text);
        Assert.Contains("sends it again   after every block", text);
        Assert.Contains("could confirm and pay twice", text);
        Assert.DoesNotContain("Anchor reserve", text);
    }
}