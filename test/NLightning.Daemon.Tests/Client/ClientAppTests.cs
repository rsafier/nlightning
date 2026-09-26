namespace NLightning.Daemon.Tests.Client;

using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Ipc;
using TestCollections;

[Collection(SerialTestCollection.Name)]
public class ClientAppTests
{
    private static readonly string s_missingCookieDir =
        Path.Combine(Path.GetTempPath(), $"nltg-missing-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public async Task GivenHelpAndMissingCookieDirectory_WhenRunAsync_ThenShowsHelpAndSucceeds(string helpFlag)
    {
        // Arrange
        string[] args = ["--cookie", s_missingCookieDir, helpFlag];

        // Act
        var exitCode = await ClientApp.RunAsync(args, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ClientApp.Success, exitCode);
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("connect-peer")]
    [InlineData("openchannel")]
    [InlineData("open-channel", "peer@host")]
    [InlineData("openchannel", "-n", "regtest")]
    [InlineData("openchannel", "--network", "regtest")]
    [InlineData("openchannel", "peer@host", "--cookie=/tmp/nltg.cookie")]
    [InlineData("unknown-command")]
    [InlineData("createinvoice")]
    [InlineData("addinvoice", "12abc")]
    [InlineData("create-invoice", "-5")]
    [InlineData("createinvoice", "1000", "desc", "0")]
    [InlineData("payinvoice")]
    [InlineData("pay", "lnbcrt1", "1.5")]
    [InlineData("pay-invoice", "lnbcrt1", "1000", "soon")]
    [InlineData("listinvoices", "0")]
    [InlineData("listpayments", "ten")]
    [InlineData("list-payments", "10", "-1")]
    [InlineData("listinvoices", "1001")]
    [InlineData("list-payments", "5000", "0")]
    [InlineData("createinvoice", "0")]
    [InlineData("pay", "lnbcrt1", "0")]
    [InlineData("payinvoice", "lnbcrt1", "any", "301")]
    [InlineData("closechannel")]
    [InlineData("close-channel", "abcd")]
    [InlineData("closechannel", "zz21212121212121212121212121212121212121212121212121212121212121")]
    [InlineData("closechannel", "2121212121212121212121212121212121212121212121212121212121212121", "fast")]
    [InlineData("closechannel", "2121212121212121212121212121212121212121212121212121212121212121", "0", "301")]
    [InlineData("closechannel", "2121212121212121212121212121212121212121212121212121212121212121", "0", "5", "bogus")]
    [InlineData("openchannel", "peer@host", "0")]
    [InlineData("openchannel", "peer@host", "lots")]
    [InlineData("openchannel", "peer@host", "50000", "50000")]
    [InlineData("open-channel", "peer@host", "50000", "-1")]
    [InlineData("openchannel", "peer@host", "99999999999999999999")]
    [InlineData("openchannel", "peer@host", "9223372036854775808")]
    [InlineData("openchannel", "peer@host", "2100000000000001")]
    public async Task GivenMissingCommandArguments_WhenRunAsync_ThenReturnsUsageError(
        string command, params string[] commandArgs)
    {
        // Arrange
        string[] args = ["--cookie", s_missingCookieDir, command, .. commandArgs];

        // Act
        var exitCode = await ClientApp.RunAsync(args, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ClientApp.UsageError, exitCode);
    }

    [Fact]
    public async Task GivenOneArgument_WhenOpenChannelHandleAsync_ThenThrowsArgumentException()
    {
        // Arrange
        await using var client = new NamedPipeIpcClient("nonexistent.ipc", "nonexistent.cookie");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => OpenChannelMessageHandler.HandleAsync(
                                                        ["peer@host"], client,
                                                        TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("getaddress")]
    [InlineData("info")]
    [InlineData("listchannels")]
    [InlineData("list-channels")]
    [InlineData("listinvoices")]
    [InlineData("list-invoices", "10")]
    [InlineData("listpayments", "10", "20")]
    [InlineData("createinvoice", "any")]
    [InlineData("addinvoice", "50000123", "two words", "600")]
    [InlineData("payinvoice", "lnbcrt1")]
    [InlineData("pay", "lnbcrt1", "any", "30")]
    [InlineData("pay-invoice", "lnbcrt1", "1000")]
    [InlineData("listinvoices", "1000")]
    [InlineData("pay", "lnbcrt1", "any", "300")]
    [InlineData("openchannel", "peer@host", "50000")]
    [InlineData("openchannel", "peer@host", "50000", "0")]
    [InlineData("open-channel", "peer@host", "50000", "20000")]
    [InlineData("openchannel", "peer@host", "2100000000000000", "2099999999999999")]
    public void GivenCommandWithOptionalArguments_WhenValidateArguments_ThenIsValid(string command,
        params string[] commandArgs)
    {
        // Act
        var error = ClientApp.ValidateArguments(command, commandArgs);

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData("any", null)]
    [InlineData("ANY", null)]
    [InlineData("50000123", 50_000_123UL)]
    public void GivenAmountArgument_WhenTryParseInvoiceAmount_ThenMsatOrAny(string value, ulong? expectedMsat)
    {
        // Act
        var parsed = ClientApp.TryParseInvoiceAmount(value, out var amount);

        // Assert
        Assert.True(parsed);
        Assert.Equal(expectedMsat, amount?.MilliSatoshi);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("12abc")]
    public void GivenZeroOrInvalidAmount_WhenTryParseInvoiceAmount_ThenRejected(string value)
    {
        // Act
        var parsed = ClientApp.TryParseInvoiceAmount(value, out var amount);

        // Assert: "0" is not a silent any-amount invoice; the user must write "any"
        Assert.False(parsed);
        Assert.Null(amount);
    }

    [Theory]
    [InlineData(new string[0], 100, 0)]
    [InlineData(new[] { "25" }, 25, 0)]
    [InlineData(new[] { "25", "50" }, 25, 50)]
    public void GivenListArguments_WhenParsePage_ThenTakeAndSkip(string[] commandArgs, int take, int skip)
    {
        // Act
        var page = ClientApp.ParsePage(commandArgs);

        // Assert
        Assert.Equal((take, skip), page);
    }

    [Fact]
    public void GivenCloseChannelOptions_WhenParsed_ThenFeerateWaitAndFeeRange()
    {
        // Arrange
        const string id = "2121212121212121212121212121212121212121212121212121212121212121";

        // Act
        var none = ClientApp.ParseCloseOptions([id]);
        var all = ClientApp.ParseCloseOptions([id, "5000", "0", "NoFeeRange"]);
        var defaultFeerate = ClientApp.ParseCloseOptions([id, "0", "60"]);

        // Assert
        Assert.Equal((null, null, false), none);
        Assert.Equal((5000U, 0U, true), all);
        Assert.Equal((null, 60U, false), defaultFeerate);
        Assert.True(ClientApp.TryParseChannelId(id, out var channelId));
        Assert.Equal(id, Convert.ToHexString((byte[])channelId).ToLowerInvariant());
    }
}