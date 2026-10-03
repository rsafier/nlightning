namespace NLightning.Daemon.Tests.Client;

using Domain.Client.Enums;
using Domain.Payments.Enums;
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
    [InlineData(new[] { "peer@host", "50000" }, false, false, false, false)]
    [InlineData(new[] { "peer@host", "50000", "--public" }, true, false, false, false)]
    [InlineData(new[] { "--public", "peer@host", "50000", "20000" }, true, false, false, false)]
    [InlineData(new[] { "peer@host", "50000", "--dual-fund" }, false, true, false, false)]
    [InlineData(new[] { "--dual-fund", "peer@host", "50000", "--public" }, true, true, false, false)]
    [InlineData(new[] { "peer@host", "--no-wait", "50000", "--dual-fund" }, false, true, false, true)]
    [InlineData(new[] { "peer@host", "50000", "--v1" }, false, false, true, false)]
    [InlineData(new[] { "--V1", "peer@host", "50000", "--public", "--no-wait" }, true, false, true, true)]
    public void Given_OpenChannelArguments_When_Parsed_Then_FlagsAndPositionalArgumentsAreSplit(string[] args,
        bool expectedPublic, bool expectedDualFund, bool expectedV1, bool expectedNoWait)
    {
        // Act
        var positional = OpenChannelMessageHandler.ParseArguments(args, out var isPublic, out var isDualFunded,
                                                                  out var forceV1, out var noWait, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(expectedPublic, isPublic);
        Assert.Equal(expectedDualFund, isDualFunded);
        Assert.Equal(expectedV1, forceV1);
        Assert.Equal(expectedNoWait, noWait);
        Assert.Equal(args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)), positional);
        Assert.Null(ClientApp.ValidateArguments("openchannel", args));
    }

    [Fact]
    public void Given_DualFundAndV1_When_OpenChannelArgumentsAreParsed_Then_ItIsAUsageError()
    {
        // Arrange (NL-551)
        string[] args = ["peer@host", "50000", "--dual-fund", "--v1"];

        // Act
        OpenChannelMessageHandler.ParseArguments(args, out _, out _, out _, out _, out var error);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("--v1", error);
        Assert.Equal(error, ClientApp.ValidateArguments("openchannel", args));
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
    [InlineData("shutdown")]
    [InlineData("stop")]
    public void GivenCommandWithOptionalArguments_WhenValidateArguments_ThenIsValid(string command,
        params string[] commandArgs)
    {
        // Act
        var error = ClientApp.ValidateArguments(command, commandArgs);

        // Assert
        Assert.Null(error);
    }

    [Theory]
    [InlineData("shutdown", "--wait")]
    [InlineData("shutdown", "--wait", "--timeout", "30")]
    [InlineData("shutdown", "--timeout=30", "--wait")]
    [InlineData("shutdown", "--force")]
    [InlineData("shutdown", "--wait", "--timeout", "30", "--force")]
    [InlineData("stop", "--wait", "--force")]
    public void GivenShutdownWithOptions_WhenValidateArguments_ThenIsValid(string command,
        params string[] commandArgs)
    {
        // Act - NL-592
        var error = ClientApp.ValidateArguments(command, commandArgs);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void GivenShutdownWithAnArgument_WhenValidateArguments_ThenUsageError()
    {
        // Act - NL-591: shutdown takes no positional argument
        var error = ClientApp.ValidateArguments("shutdown", ["--force"]);

        // Assert: --force is an option, not an argument; an unknown word is
        Assert.Null(error);
    }

    [Fact]
    public void GivenShutdownWithAnUnknownWord_WhenValidateArguments_ThenUsageError()
    {
        // Act
        var error = ClientApp.ValidateArguments("shutdown", ["extra"]);

        // Assert
        Assert.Contains("Unknown option 'extra'", error);
        Assert.Contains("Usage: shutdown", error);
    }

    [Fact]
    public void GivenTimeoutWithoutWait_WhenValidateArguments_ThenUsageError()
    {
        // Act - NL-592: --timeout only tunes --wait
        var error = ClientApp.ValidateArguments("shutdown", ["--timeout", "30"]);

        // Assert
        Assert.Contains("--timeout needs --wait", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("90000")]
    [InlineData("abc")]
    public void GivenAnInvalidTimeout_WhenValidateArguments_ThenUsageError(string value)
    {
        // Act - NL-592: the wait is always bounded (1 s to a day)
        var error = ClientApp.ValidateArguments("shutdown", ["--wait", "--timeout", value]);

        // Assert
        Assert.Contains("Invalid timeout", error);
    }

    [Theory]
    [InlineData(ShutdownOutcome.Stopped, 0)]
    [InlineData(ShutdownOutcome.Forced, 0)]
    [InlineData(ShutdownOutcome.TimedOut, 1)]
    public void GivenAnShutdownOutcome_WhenMappedToAnExitCode_ThenOnlyStoppingExitsZero(ShutdownOutcome outcome,
        int expected)
    {
        // Act - NL-594: a script must tell an accepted shutdown from a timed-out wait; a refusal throws in
        // RunAsync and exits 1 before this mapping
        var exitCode = ClientApp.ExitCodeFor(outcome);

        // Assert
        Assert.Equal(expected, exitCode);
    }

    [Fact]
    public void GivenShutdownOptions_WhenParsed_ThenTheFlagsAreRead()
    {
        // Act
        var parsed = ClientApp.ParseShutdownOptions(["--wait", "--timeout", "30", "--force"], out var error);

        // Assert
        Assert.Null(error);
        Assert.True(parsed!.Value.Wait);
        Assert.Equal(30, parsed.Value.TimeoutSeconds);
        Assert.True(parsed.Value.Force);
    }

    [Fact]
    public void GivenNoShutdownOptions_WhenParsed_ThenTheFirstPassDefaultsHold()
    {
        // Act - NL-592: an older client's bare `shutdown` keeps the first-pass behavior
        var parsed = ClientApp.ParseShutdownOptions([], out var error);

        // Assert
        Assert.Null(error);
        Assert.False(parsed!.Value.Wait);
        Assert.Equal(0, parsed.Value.TimeoutSeconds);
        Assert.False(parsed.Value.Force);
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
    public void GivenPayInvoiceChannelPins_WhenParsed_ThenOutAndInAreRead()
    {
        // Act - NL-609: --out by short channel id, --in by channel id
        var channelId = new string('a', 64);
        var parsed = ClientApp.ParsePayInvoiceOptions(["lnbcrt1", "--out", "500x1x0", $"--in={channelId}"],
                                                      out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(("500x1x0", channelId), (parsed!.OutgoingChannel, parsed.IncomingChannel));
    }

    [Theory]
    [InlineData("--out", "nope", "Invalid channel")]
    [InlineData("--in", "12x", "Invalid channel")]
    public void GivenAMalformedPayInvoicePin_WhenParsed_ThenAnError(string option, string value, string expected)
    {
        // Act
        var parsed = ClientApp.ParsePayInvoiceOptions(["lnbcrt1", option, value], out var error);

        // Assert
        Assert.Null(parsed);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void GivenATrampolineNode_WhenPayInvoiceParsed_ThenItIsRead()
    {
        // Act - NL-875: --trampoline <node_id>
        var nodeId = "02" + new string('1', 64);
        var parsed = ClientApp.ParsePayInvoiceOptions(["lnbcrt1", "--trampoline", nodeId], out var error);
        var inline = ClientApp.ParsePayInvoiceOptions(["lnbcrt1", $"--trampoline={nodeId}"], out _);

        // Assert
        Assert.Null(error);
        Assert.Equal(nodeId, Convert.ToHexStringLower(parsed!.TrampolineNode!.Value));
        Assert.Equal(parsed.TrampolineNode, inline!.TrampolineNode);
        Assert.Null(ClientApp.ParsePayInvoiceOptions(["lnbcrt1"], out _)!.TrampolineNode);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("021111")]
    public void GivenAMalformedTrampolineNode_WhenPayInvoiceParsed_ThenAnError(string value)
    {
        // Act
        var parsed = ClientApp.ParsePayInvoiceOptions(["lnbcrt1", "--trampoline", value], out var error);

        // Assert
        Assert.Null(parsed);
        Assert.Contains("Invalid trampoline node", error);
    }

    [Fact]
    public void GivenTheSameChannelOutAndIn_WhenParsed_ThenAnError()
    {
        // Act
        var parsed = ClientApp.ParsePayInvoiceOptions(["lnbcrt1", "--out", "500x1x0", "--in", "500x1x0"],
                                                      out var error);

        // Assert
        Assert.Null(parsed);
        Assert.Contains("same channel", error);
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
        Assert.Null(parsed.OutgoingChannel);
        Assert.Null(parsed.IncomingChannel);
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
    public void GivenDisconnectArguments_WhenValidatedAndParsed_ThenNodeAndForceInAnyOrder()
    {
        // Arrange (NL-152)
        const string node = "02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        // Act
        var plain = ClientApp.ParseDisconnectOptions([node], out var plainError);
        var forced = ClientApp.ParseDisconnectOptions(["--FORCE", node], out _);

        // Assert
        Assert.Null(plainError);
        Assert.Equal(node, Convert.ToHexString((byte[])plain!.Value.NodeId).ToLowerInvariant());
        Assert.False(plain.Value.Force);
        Assert.True(forced!.Value.Force);
        Assert.Null(ClientApp.ValidateArguments("disconnect", [node]));
        Assert.Null(ClientApp.ValidateArguments("disconnect-peer", [node, "--force"]));
        Assert.NotNull(ClientApp.ValidateArguments("disconnect", []));
        Assert.NotNull(ClientApp.ValidateArguments("disconnect", ["--force"]));
        Assert.NotNull(ClientApp.ValidateArguments("disconnect", ["peer@host:9735"]));
        Assert.NotNull(ClientApp.ValidateArguments("disconnect", [node, node]));
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

    [Fact]
    public void GivenDescribeGraphArguments_WhenValidatedAndParsed_ThenFlagsAndPage()
    {
        // Arrange (BOLT 7 G5-T4)
        string[] full = ["--channels", "--limit", "25", "--offset=50"];

        // Act
        var parsed = ClientApp.ParseDescribeGraphOptions(full, out var error);
        var plain = ClientApp.ParseDescribeGraphOptions([], out _);

        // Assert
        Assert.Null(error);
        Assert.Equal(new DescribeGraphArguments(true, false, 50, 25), parsed);
        Assert.Equal(new DescribeGraphArguments(false, false, 0, 100), plain);
        Assert.Null(ClientApp.ValidateArguments("describegraph", []));
        Assert.Null(ClientApp.ValidateArguments("describe-graph", ["--limit=1000", "--channels"]));
        Assert.NotNull(ClientApp.ValidateArguments("describegraph", ["--limit", "0"]));
        Assert.NotNull(ClientApp.ValidateArguments("describegraph", ["--limit", "1001"]));
        Assert.NotNull(ClientApp.ValidateArguments("describegraph", ["--offset", "-1"]));
        Assert.NotNull(ClientApp.ValidateArguments("describegraph", ["--offset"]));
        Assert.NotNull(ClientApp.ValidateArguments("describegraph", ["channels"]));
        Assert.NotNull(ClientApp.ValidateArguments("describegraph", ["--channels=yes"]));
        // One offset cannot page both listings (they end at different offsets)
        Assert.Null(ClientApp.ValidateArguments("describegraph", ["--channels", "--nodes", "--limit", "5"]));
        Assert.Null(ClientApp.ValidateArguments("describegraph", ["--nodes", "--offset", "5"]));
        Assert.NotNull(ClientApp.ValidateArguments("describegraph", ["--channels", "--nodes", "--offset", "5"]));
    }

    [Fact]
    public void Given_NoListForwardsArguments_When_Parsed_Then_TheDefaultsHold()
    {
        // Act - NL-597: a plain listforwards shows the newest page
        var parsed = ClientApp.ParseListForwardsOptions([], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(new ListForwardsArguments(0, 100, null, null, null, null), parsed);
    }

    [Fact]
    public void Given_ACountAndSkip_When_Parsed_Then_ThePageIsRead()
    {
        // Act - NL-597
        var count = ClientApp.ParseListForwardsOptions(["20"], out _);
        var countAndSkip = ClientApp.ParseListForwardsOptions(["20", "5"], out _);

        // Assert
        Assert.Equal((0, 20), (count!.Skip, count.Take));
        Assert.Equal((5, 20), (countAndSkip!.Skip, countAndSkip.Take));
    }

    [Fact]
    public void Given_AStatusOption_When_Parsed_Then_TheStatusIsRead()
    {
        // Act - NL-597
        var parsed = ClientApp.ParseListForwardsOptions(["--status", "fulfilled"], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal((byte)ForwardCircuitStatus.Fulfilled, parsed!.Status);
    }

    [Fact]
    public void Given_ASinceOption_When_Parsed_Then_TheUnixSecondsAreRead()
    {
        // Act - NL-597
        var parsed = ClientApp.ParseListForwardsOptions(["--since", "1760000000"], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(1_760_000_000, parsed!.Since);
    }

    [Fact]
    public void Given_AnIsoSinceOption_When_Parsed_Then_TheInstantBecomesUnixSeconds()
    {
        // Act - NL-597
        var parsed = ClientApp.ParseListForwardsOptions(["--since=2026-10-01T12:00:00Z"], out var error);
        var expected = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        // Assert
        Assert.Null(error);
        Assert.Equal(expected, parsed!.Since);
    }

    [Fact]
    public void Given_AChannelOption_When_Parsed_Then_TheChannelIsCarried()
    {
        // Arrange
        const string hexChannelId = "2121212121212121212121212121212121212121212121212121212121212121";

        // Act - NL-597: a short_channel_id or a 64-hex channel id, with = or a following value
        var scid = ClientApp.ParseListForwardsOptions(["--channel", "800000x12x0"], out _);
        var hex = ClientApp.ParseListForwardsOptions(["--channel=" + hexChannelId], out _);

        // Assert
        Assert.Equal("800000x12x0", scid!.Channel);
        Assert.Equal(hexChannelId, hex!.Channel);
    }

    [Fact]
    public void Given_CombinedListForwardsOptions_When_Parsed_Then_AllAreReadInAnyOrder()
    {
        // Act - NL-597
        var parsed = ClientApp.ParseListForwardsOptions(
            ["--status=failed", "10", "--channel", "800000x12x0", "--since", "100"], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(new ListForwardsArguments(0, 10, 100, null, (byte)ForwardCircuitStatus.Failed,
                                               "800000x12x0"), parsed);
    }

    [Fact]
    public void Given_BadListForwardsArguments_When_Validated_Then_UsageErrors()
    {
        // Act - NL-597
        var countError = ClientApp.ValidateArguments("listforwards", ["0"]);
        var countWordError = ClientApp.ValidateArguments("listforwards", ["abc"]);
        var statusError = ClientApp.ValidateArguments("listforwards", ["--status", "bogus"]);
        var missingChannelError = ClientApp.ValidateArguments("listforwards", ["--channel"]);
        var unknownOptionError = ClientApp.ValidateArguments("listforwards", ["--bogus=1"]);
        var sinceError = ClientApp.ValidateArguments("listforwards", ["--since", "abc"]);

        // Assert
        Assert.Contains("Invalid count", countError);
        Assert.Contains("Invalid count", countWordError);
        Assert.NotNull(statusError);
        Assert.Contains("bogus", statusError);
        Assert.Contains("Missing value for --channel", missingChannelError);
        Assert.Contains("Unknown option '--bogus", unknownOptionError);
        Assert.NotNull(sinceError);
        Assert.Contains("abc", sinceError);
    }

    [Fact]
    public void Given_ValidListForwardsArguments_When_Validated_Then_NoError()
    {
        // Act - NL-597
        var none = ClientApp.ValidateArguments("listforwards", []);
        var page = ClientApp.ValidateArguments("listforwards", ["20"]);
        var pageAndSkip = ClientApp.ValidateArguments("listforwards", ["20", "5"]);
        var status = ClientApp.ValidateArguments("listforwards", ["--status=fulfilled"]);
        var filters = ClientApp.ValidateArguments("listforwards", ["--since=1760000000", "--channel=800000x12x0"]);
        var mixed = ClientApp.ValidateArguments("listforwards", ["10", "--channel", "800000x12x0"]);
        var alias = ClientApp.ValidateArguments("list-forwards", ["--status=fulfilled"]);

        // Assert
        Assert.Null(none);
        Assert.Null(page);
        Assert.Null(pageAndSkip);
        Assert.Null(status);
        Assert.Null(filters);
        Assert.Null(mixed);
        Assert.Null(alias);
    }
}