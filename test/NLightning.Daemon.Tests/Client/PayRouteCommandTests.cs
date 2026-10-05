namespace NLightning.Daemon.Tests.Client;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.Onion.Enums;
using NLightning.Client;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of <c>payroute</c> (ClientCommand 48, NL-1082): argument parsing, the <c>--routes</c> JSON and the
/// printed result.
/// </summary>
public class PayRouteCommandTests
{
    private const string HashHex = "abababababababababababababababababababababababababababababababab";
    private const string SecretHex = "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd";
    private const string PeerId = "0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c";
    private const string PayeeId = "02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string RouteJson =
        """
        [{"firstHopChannel":"812345x12x0","firstHopAmountMsat":1001000,"firstHopCltv":812,
          "hops":[{"nodeId":"0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c",
                   "outgoingShortChannelId":70123456789,"amountToForwardMsat":1000000,"outgoingCltvValue":808},
                  {"nodeId":"02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                   "amountToForwardMsat":1000000,"outgoingCltvValue":800}]}]
        """;

    [Fact]
    public void Given_TheInvoiceFormWithEveryOption_When_Parsed_Then_AllAreKept()
    {
        // Arrange
        string[] args = ["lnbcrt1", "--routes=routes.json", "--max-fee-msat", "2500", "--timeout", "45"];

        // Act
        var parsed = ClientApp.ParsePayRouteOptions(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(new PayRouteArguments("lnbcrt1", null, null, null, "routes.json", 45, 2500), parsed);
        Assert.Null(ClientApp.ValidateArguments("payroute", args));
        Assert.Null(ClientApp.ValidateArguments("pay-route", args));
    }

    [Fact]
    public void Given_TheRawFormWithEveryOption_When_Parsed_Then_AllAreKept()
    {
        // Arrange
        string[] args =
        [
            "--payment-hash", HashHex, "--payment-secret=" + SecretHex, "--total-msat", "2000000",
            "--routes", "-", "--max-fee-msat=500"
        ];

        // Act
        var parsed = ClientApp.ParsePayRouteOptions(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(new PayRouteArguments(null, HashHex, SecretHex, 2_000_000, "-", null, 500), parsed);
        Assert.Null(ClientApp.ValidateArguments("payroute", args));
    }

    public static TheoryData<string[]> InvalidArguments => new(
        ["--routes", "-"], // no identity
        ["lnbcrt1"], // no routes
        ["lnbcrt1", "--routes"], // missing value
        ["lnbcrt1", "--payment-hash", HashHex, "--routes", "-"], // both identity forms
        ["--payment-hash", "zz", "--routes", "-"], // bad hash hex
        ["--payment-hash", HashHex, "--payment-secret", "no-hex", "--routes", "-"], // bad secret hex
        ["--payment-secret", SecretHex, "--routes", "-"], // a secret without the raw form
        ["--payment-hash", HashHex, "--total-msat", "0", "--routes", "-"], // zero total
        ["--payment-hash", HashHex, "--routes", "-", "--timeout", "301"], // bad timeout
        ["--payment-hash", HashHex, "--routes", "-", "--max-fee-msat", "0"], // zero fee limit
        ["--payment-hash", HashHex, "--routes", "-", "--max-parts", "2"], // payroute never re-plans
        ["lnbcrt1", "--routes", "routes.json", "extra"]); // too many positional

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Given_InvalidArguments_When_Validated_Then_UsageError(string[] args)
    {
        // Act
        var error = ClientApp.ValidateArguments("payroute", args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains("Usage: payroute", error);
    }

    [Fact]
    public void Given_ValidRoutesJson_When_Parsed_Then_EveryFieldIsMapped()
    {
        // Act
        var routes = PayRouteRoutesJson.Parse(RouteJson, out var error);

        // Assert
        Assert.Null(error);
        var route = Assert.Single(routes!);
        Assert.Equal("812345x12x0", route.FirstHopChannel);
        Assert.Equal((1_001_000UL, 812U), (route.FirstHopAmountMsat, route.FirstHopCltv));
        Assert.Equal(2, route.Hops.Count);
        Assert.Equal(PeerId, Convert.ToHexStringLower(route.Hops[0].NodeId));
        Assert.Equal(70_123_456_789UL, route.Hops[0].OutgoingShortChannelId);
        Assert.Equal((1_000_000UL, 808U), (route.Hops[0].AmountToForwardMsat, route.Hops[0].OutgoingCltvValue));
        Assert.Equal(PayeeId, Convert.ToHexStringLower(route.Hops[1].NodeId));
        Assert.Null(route.Hops[1].OutgoingShortChannelId); // the payee's final hop omits it
        Assert.Equal((1_000_000UL, 800U), (route.Hops[1].AmountToForwardMsat, route.Hops[1].OutgoingCltvValue));
    }

    [Fact]
    public void Given_PascalCaseKeysAndTwoRoutes_When_Parsed_Then_CaseInsensitiveAndBothKept()
    {
        // Arrange: camelCase is the documented form, but the keys read case-insensitively; the second route's single
        // hop is the payee's final one, so it omits the short channel id
        const string json =
            """
            [{"FirstHopChannel":"812345x12x0","FirstHopAmountMsat":1000,"FirstHopCltv":800,
              "Hops":[{"NodeId":"0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c",
                       "OutgoingShortChannelId":1,"AmountToForwardMsat":1000,"OutgoingCltvValue":700},
                      {"NodeId":"02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                       "AmountToForwardMsat":1000,"OutgoingCltvValue":600}]},
             {"firstHopChannel":"2121212121212121212121212121212121212121212121212121212121212121",
              "firstHopAmountMsat":2000,"firstHopCltv":810,
              "hops":[{"nodeId":"02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                       "amountToForwardMsat":2000,"outgoingCltvValue":700}]}]
            """;

        // Act
        var routes = PayRouteRoutesJson.Parse(json, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(2, routes!.Count);
        Assert.Equal(1UL, routes[0].Hops[0].OutgoingShortChannelId);
        Assert.Null(routes[0].Hops[1].OutgoingShortChannelId);
        Assert.Null(routes[1].Hops[0].OutgoingShortChannelId);
    }

    public static TheoryData<string, string> BadRoutes => new()
    {
        // The JSON itself
        { "not json at all", "not valid JSON" },
        { "[]", "empty set" },
        { "[" + string.Join(",", Enumerable.Repeat(
            """{"firstHopChannel":"812345x12x0","firstHopAmountMsat":1,"firstHopCltv":1,"hops":[{"nodeId":"0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c","amountToForwardMsat":1,"outgoingCltvValue":1}]}""",
            129)) + "]", "at most 128" },
        // The route's own fields, named with their index
        { """[{"firstHopChannel":"nope","firstHopAmountMsat":1,"firstHopCltv":1,"hops":[{"nodeId":"0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c","amountToForwardMsat":1,"outgoingCltvValue":1}]}]""",
          "route 1: firstHopChannel" },
        { """[{"firstHopChannel":"812345x12x0","firstHopAmountMsat":0,"firstHopCltv":1,"hops":[{"nodeId":"0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c","amountToForwardMsat":1,"outgoingCltvValue":1}]}]""",
          "route 1: firstHopAmountMsat" },
        { """[{"firstHopChannel":"812345x12x0","firstHopAmountMsat":1,"firstHopCltv":0,"hops":[{"nodeId":"0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c","amountToForwardMsat":1,"outgoingCltvValue":1}]}]""",
          "route 1: firstHopCltv" },
        { """[{"firstHopChannel":"812345x12x0","firstHopAmountMsat":1,"firstHopCltv":1}]""", "route 1: hops" },
        { """[{"firstHopChannel":"812345x12x0","firstHopAmountMsat":1,"firstHopCltv":1,"hops":[]}]""",
          "route 1: hops" },
        // The hops', named with both indexes (a route's last hop is the payee's and omits the short channel id)
        { """[{"firstHopChannel":"812345x12x0","firstHopAmountMsat":1,"firstHopCltv":1,"hops":[{"nodeId":"02bad","amountToForwardMsat":1,"outgoingCltvValue":1}]}]""",
          "route 1, hop 1: nodeId" },
        { """[{"firstHopChannel":"812345x12x0","firstHopAmountMsat":1,"firstHopCltv":1,"hops":[{"nodeId":"0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c","amountToForwardMsat":1,"outgoingCltvValue":1},{"nodeId":"02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","amountToForwardMsat":1,"outgoingCltvValue":1}]}]""",
          "route 1, hop 1: outgoingShortChannelId is missing" },
        { """[{"firstHopChannel":"812345x12x0","firstHopAmountMsat":1,"firstHopCltv":1,"hops":[{"nodeId":"0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c","outgoingShortChannelId":1,"amountToForwardMsat":1,"outgoingCltvValue":1},{"nodeId":"02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","outgoingShortChannelId":2,"amountToForwardMsat":1,"outgoingCltvValue":1}]}]""",
          "route 1, hop 2 is the payee's final hop" },
        { """[{"firstHopChannel":"812345x12x0","firstHopAmountMsat":1,"firstHopCltv":1,"hops":[{"nodeId":"0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c","amountToForwardMsat":1,"outgoingCltvValue":1}]},{"firstHopChannel":"812345x12x0","firstHopAmountMsat":1,"firstHopCltv":1,"hops":[{"nodeId":"02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","amountToForwardMsat":0,"outgoingCltvValue":1}]}]""",
          "route 2, hop 1: amountToForwardMsat" },
        { """[{"firstHopChannel":"812345x12x0","firstHopAmountMsat":1,"firstHopCltv":1,"hops":[{"nodeId":"0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c","amountToForwardMsat":1,"outgoingCltvValue":0}]}]""",
          "route 1, hop 1: outgoingCltvValue" }
    };

    [Theory]
    [MemberData(nameof(BadRoutes))]
    public void Given_BadRoutesJson_When_Parsed_Then_TheErrorNamesTheRouteHopAndField(string json, string expected)
    {
        // Act
        var routes = PayRouteRoutesJson.Parse(json, out var error);

        // Assert
        Assert.Null(routes);
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public async Task Given_TheRoutesOnStandardInputOrInAFile_When_Read_Then_BothAreParsed()
    {
        // Arrange: "-" is standard input
        var stdin = new StringReader(RouteJson);
        var path = Path.Combine(Path.GetTempPath(), $"nltg-payroute-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, RouteJson, TestContext.Current.CancellationToken);
        try
        {
            // Act
            var ct = TestContext.Current.CancellationToken;
            var fromStdin = await PayRouteRoutesJson.ReadAsync("-", stdin, ct);
            var fromFile = await PayRouteRoutesJson.ReadAsync(path, Console.In, ct);

            // Assert: the same route, field by field (a record over IReadOnlyList compares by reference)
            var stdinRoute = Assert.Single(fromStdin);
            var fileRoute = Assert.Single(fromFile);
            Assert.Equal((fileRoute.FirstHopChannel, fileRoute.FirstHopAmountMsat, fileRoute.FirstHopCltv),
                         (stdinRoute.FirstHopChannel, stdinRoute.FirstHopAmountMsat, stdinRoute.FirstHopCltv));
            Assert.Equal(fileRoute.Hops, stdinRoute.Hops);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Given_BadRoutesInAFile_When_Read_Then_TheErrorIsThrown()
    {
        // Act / Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => PayRouteRoutesJson.ReadAsync("-", new StringReader("[]"), TestContext.Current.CancellationToken));
        Assert.Contains("empty set", exception.Message);
    }

    [Fact]
    public void Given_AFailedAndASucceededRoute_When_Printed_Then_EachOutcomeIsListed()
    {
        // Arrange
        var output = new StringWriter();
        var response = new PayRouteIpcResponse
        {
            Payment = new PaymentInfoIpcResponse
            {
                PaymentHash = new Hash(Convert.FromHexString(HashHex)),
                PayeeNodeId = new CompactPubKey(Convert.FromHexString(PayeeId)),
                Amount = LightningMoney.MilliSatoshis(1_000_000),
                Fee = LightningMoney.MilliSatoshis(1_000),
                Status = PaymentStatus.Failed,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1_759_000_000)
            },
            RouteOutcomes =
            [
                new RouteOutcomeIpcInfo { Index = 0, Status = PaymentPartState.Succeeded, HtlcId = 3 },
                new RouteOutcomeIpcInfo
                {
                    Index = 1, Status = PaymentPartState.Failed, HtlcId = 4,
                    FailureCode = FailureCode.TemporaryChannelFailure, FailureSourceIndex = 1,
                    FailureReason = "channel disabled"
                }
            ]
        };

        // Act
        new PayRoutePrinter(output).Print(response);

        // Assert: the payment as payinvoice reports it, then the per-route table
        var text = output.ToString();
        Assert.StartsWith("Payment:", text);
        Assert.Contains($"Payment Hash:       {HashHex}", text);
        Assert.Contains("Route 0:            Succeeded   HTLC 3", text);
        Assert.Contains("Route 1:            Failed   HTLC 4", text);
        Assert.Contains("Failure:          TemporaryChannelFailure (0x1007) at hop 1", text);
        Assert.Contains("Failure Reason:   channel disabled", text);
    }
}