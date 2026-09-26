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
    [InlineData("payinvoice", "lnbcrt1", "--max-parts", "0")]
    [InlineData("payinvoice", "lnbcrt1", "--max-parts", "129")]
    [InlineData("payinvoice", "lnbcrt1", "--max-fee-msat", "-1")]
    [InlineData("payinvoice", "lnbcrt1", "--max-fee-msat")]
    [InlineData("payinvoice", "lnbcrt1", "--timeout=0")]
    [InlineData("payinvoice", "lnbcrt1", "any", "30", "--timeout", "30")]
    [InlineData("payinvoice", "lnbcrt1", "--fast")]
    [InlineData("payinvoice", "lnbcrt1", "any", "30", "extra")]
    [InlineData("payinvoice", "--max-parts", "2")]
    [InlineData("closechannel")]
    [InlineData("close-channel", "abcd")]
    [InlineData("closechannel", "zz21212121212121212121212121212121212121212121212121212121212121")]
    [InlineData("closechannel", "2121212121212121212121212121212121212121212121212121212121212121", "fast")]
    [InlineData("closechannel", "2121212121212121212121212121212121212121212121212121212121212121", "0", "301")]
    [InlineData("closechannel", "2121212121212121212121212121212121212121212121212121212121212121", "0", "5", "bogus")]
    [InlineData("forceclosechannel")]
    [InlineData("force-close-channel", "abcd")]
    [InlineData("pendingsweeps", "bogus")]
    [InlineData("pending-sweeps", "2121212121212121212121212121212121212121212121212121212121212121", "most")]
    [InlineData("openchannel", "peer@host", "0")]
    [InlineData("openchannel", "peer@host", "lots")]
    [InlineData("openchannel", "peer@host", "50000", "50000")]
    [InlineData("open-channel", "peer@host", "50000", "-1")]
    [InlineData("openchannel", "peer@host", "99999999999999999999")]
    [InlineData("openchannel", "peer@host", "9223372036854775808")]
    [InlineData("openchannel", "peer@host", "2100000000000001")]
    [InlineData("openchannel", "peer@host", "--public")]
    [InlineData("openchannel", "peer@host", "50000", "--private")]
    [InlineData("openchannel", "peer@host", "50000", "0", "1")]
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

    [Theory]
    [InlineData(new[] { "peer@host", "50000" }, false)]
    [InlineData(new[] { "peer@host", "50000", "--public" }, true)]
    [InlineData(new[] { "--public", "peer@host", "50000", "20000" }, true)]
    public void Given_OpenChannelArguments_When_Parsed_Then_PublicFlagAndPositionalArgumentsAreSplit(string[] args,
        bool expectedPublic)
    {
        // Act
        var positional = OpenChannelMessageHandler.ParseArguments(args, out var isPublic, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(expectedPublic, isPublic);
        Assert.Equal(args.Where(a => a != OpenChannelMessageHandler.PublicOption), positional);
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
    [InlineData("payinvoice", "lnbcrt1", "--max-fee-msat", "0")]
    [InlineData("payinvoice", "lnbcrt1", "any", "--max-parts=128", "--max-fee-msat=5000", "--timeout", "300")]
    [InlineData("payinvoice", "--max-parts", "1", "lnbcrt1", "1000")]
    [InlineData("openchannel", "peer@host", "50000")]
    [InlineData("openchannel", "peer@host", "50000", "0")]
    [InlineData("open-channel", "peer@host", "50000", "20000")]
    [InlineData("openchannel", "peer@host", "2100000000000000", "2099999999999999")]
    [InlineData("openchannel", "peer@host", "50000", "--public")]
    [InlineData("openchannel", "--public", "peer@host", "50000", "20000")]
    [InlineData("open-channel", "peer@host", "50000", "20000", "--PUBLIC")]
    public void GivenCommandWithOptionalArguments_WhenValidateArguments_ThenIsValid(string command,
        params string[] commandArgs)
    {
        // Act
        var error = ClientApp.ValidateArguments(command, commandArgs);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void GivenPayInvoiceOptions_WhenParsed_ThenPositionalArgumentsAndOptionsAreRead()
    {
        // Act
        var parsed = ClientApp.ParsePayInvoiceOptions(
            ["lnbcrt1", "--max-fee-msat", "2500", "any", "--max-parts=3", "--timeout", "45"], out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("lnbcrt1", parsed.Bolt11);
        Assert.Null(parsed.Amount);
        Assert.Equal(45U, parsed.TimeoutSeconds);
        Assert.Equal(2_500UL, parsed.MaxFeeMsat);
        Assert.Equal(3U, parsed.MaxParts);
    }

    [Fact]
    public void GivenOnlyPositionalPayInvoiceArguments_WhenParsed_ThenNoLimitsAreSet()
    {
        // Act
        var parsed = ClientApp.ParsePayInvoiceOptions(["lnbcrt1", "7000", "20"], out _);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(7_000UL, parsed.Amount!.MilliSatoshi);
        Assert.Equal(20U, parsed.TimeoutSeconds);
        Assert.Null(parsed.MaxFeeMsat);
        Assert.Null(parsed.MaxParts);
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

    [Fact]
    public void GivenPendingSweepsOptions_WhenParsed_ThenChannelAndIncludeClosedInAnyOrder()
    {
        // Arrange
        const string id = "2121212121212121212121212121212121212121212121212121212121212121";

        // Act
        var none = ClientApp.ParsePendingSweepsOptions([]);
        var (channelId, includeClosed) = ClientApp.ParsePendingSweepsOptions(["ALL", id]);
        var (onlyChannelId, onlyIncludeClosed) = ClientApp.ParsePendingSweepsOptions([id]);

        // Assert
        Assert.Equal((null, false), none);
        Assert.True(includeClosed);
        Assert.Equal(id, Convert.ToHexString((byte[])channelId!.Value).ToLowerInvariant());
        Assert.False(onlyIncludeClosed);
        Assert.NotNull(onlyChannelId);
        Assert.Null(ClientApp.ValidateArguments("pendingsweeps", []));
        Assert.Null(ClientApp.ValidateArguments("forceclosechannel", [id]));
    }

    [Fact]
    public void GivenGraphListingArguments_WhenValidatedAndParsed_ThenScidAndNodeInAnyOrder()
    {
        // Arrange
        const string node = "02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        // Act
        var (scid, nodeId) = ClientApp.ParseGraphChannelFilters([node, "110x1x0"]);
        var none = ClientApp.ParseGraphChannelFilters([]);

        // Assert
        Assert.Equal((110UL << 40) | (1UL << 16), scid);
        Assert.Equal(node, Convert.ToHexString((byte[])nodeId!.Value).ToLowerInvariant());
        Assert.Equal((null, null), none);
        Assert.Null(ClientApp.ValidateArguments("listnodes", []));
        Assert.Null(ClientApp.ValidateArguments("list-nodes", [node]));
        Assert.Null(ClientApp.ValidateArguments("listgraphchannels", []));
        Assert.Null(ClientApp.ValidateArguments("list-graph-channels", ["110x1x0", node]));
        Assert.NotNull(ClientApp.ValidateArguments("listnodes", ["04" + node[2..]]));
        Assert.NotNull(ClientApp.ValidateArguments("listnodes", [node, node]));
        Assert.NotNull(ClientApp.ValidateArguments("listgraphchannels", ["110x1"]));
        Assert.NotNull(ClientApp.ValidateArguments("listgraphchannels", ["16777216x0x0"]));
    }

    [Fact]
    public void GivenGetRouteArguments_WhenValidatedAndParsed_ThenNodeAmountAndOptions()
    {
        // Arrange (BOLT 7 G4-T4)
        const string node = "02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        // Act
        var full = ClientApp.ParseGetRouteOptions([node, "1000000", "--max-fee-msat", "5000", "--final-cltv=40"],
                                                  out var fullError);
        var plain = ClientApp.ParseGetRouteOptions([node, "1"], out _);

        // Assert
        Assert.Null(fullError);
        Assert.Equal(node, Convert.ToHexString((byte[])full!.NodeId).ToLowerInvariant());
        Assert.Equal((1_000_000UL, (ulong?)5_000, (ushort?)40),
                     (full.AmountMsat, full.MaxFeeMsat, full.FinalCltvDelta));
        Assert.Equal((1UL, (ulong?)null, (ushort?)null), (plain!.AmountMsat, plain.MaxFeeMsat, plain.FinalCltvDelta));
        Assert.Null(ClientApp.ValidateArguments("getroute", [node, "10"]));
        Assert.Null(ClientApp.ValidateArguments("get-route", ["--max-fee-msat=0", node, "10"]));
        Assert.NotNull(ClientApp.ValidateArguments("getroute", [node]));
        Assert.NotNull(ClientApp.ValidateArguments("getroute", [node, "0"]));
        Assert.NotNull(ClientApp.ValidateArguments("getroute", ["04" + node[2..], "10"]));
        Assert.NotNull(ClientApp.ValidateArguments("getroute", [node, "10", "extra"]));
        Assert.NotNull(ClientApp.ValidateArguments("getroute", [node, "10", "--final-cltv", "0"]));
        Assert.NotNull(ClientApp.ValidateArguments("getroute", [node, "10", "--max-parts", "2"]));
        Assert.NotNull(ClientApp.ValidateArguments("getroute", [node, "10", "--max-fee-msat"]));
    }
}