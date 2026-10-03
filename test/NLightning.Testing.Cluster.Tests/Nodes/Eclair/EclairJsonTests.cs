using System.Text.Json.Nodes;

namespace NLightning.Testing.Cluster.Tests.Nodes.Eclair;

using Cluster.Nodes;
using Cluster.Nodes.Eclair;

public class EclairJsonTests
{
    private const string TxId = "5d1fa2b7c1f8ab3d4e1e5d1fa2b7c1f8ab3d4e1e5d1fa2b7c1f8ab3d4e1e00aa";

    /// <summary>The parts of Eclair 0.14.3's <c>channels</c> answer the facade reads (shape of its JsonSerializers).</summary>
    private static JsonNode Channel(string state, bool confirmed) =>
        JsonNode.Parse($$"""
                         {
                           "nodeId": "02ABCDEF",
                           "channelId": "00ff",
                           "state": "{{state}}",
                           "data": {
                             "commitments": {
                               "active": [
                                 {
                                   "fundingTxIndex": 0,
                                   "fundingInput": "{{TxId.ToUpperInvariant()}}:1",
                                   "fundingAmount": 1000000,
                                   "localFunding": {{(confirmed ? "{ \"shortChannelId\": \"150x1x1\" }" : "{}")}},
                                   "localCommit": { "spec": { "toLocal": 400000000, "toRemote": 600000000 } }
                                 }
                               ]
                             }
                           }
                         }
                         """)!;

    [Fact]
    public void Given_ANormalConfirmedChannel_When_Read_Then_EveryFacadeFieldIsSet()
    {
        // Act
        var channel = EclairJson.ToTestChannel(Channel("NORMAL", confirmed: true));

        // Assert
        Assert.Equal(new TestChannel("02abcdef", TxId, 1, "150x1x1", 1_000_000, 400_000_000, true), channel);
    }

    [Theory]
    [InlineData("OFFLINE")]
    [InlineData("WAIT_FOR_DUAL_FUNDING_CONFIRMED")]
    public void Given_AChannelNotNormal_When_Read_Then_ItIsNotActive(string state)
    {
        // Act
        var channel = EclairJson.ToTestChannel(Channel(state, confirmed: false));

        // Assert
        Assert.False(channel.Active);
        Assert.Null(channel.ShortChannelId);
    }

    [Fact]
    public void Given_AChannelsAnswer_When_Read_Then_EachEntryIsAChannel()
    {
        // Arrange
        var answer = new JsonArray(Channel("NORMAL", true), Channel("OFFLINE", true));

        // Act + Assert
        Assert.Equal(2, EclairJson.ToTestChannels(answer).Count);
        Assert.Empty(EclairJson.ToTestChannels(null));
    }

    [Fact]
    public void Given_PaymentEvents_When_Read_Then_OnlyASentPaymentWithAPreimageSucceeded()
    {
        // Arrange
        var sent = JsonNode.Parse("""{ "type": "payment-sent", "paymentPreimage": "AB01" }""");
        var failed = JsonNode.Parse("""{ "type": "payment-failed", "failures": [] }""");

        // Act + Assert
        Assert.Equal(new TestPaymentResult(true, "ab01", null), EclairJson.ToPaymentResult(sent));
        Assert.False(EclairJson.ToPaymentResult(failed).Succeeded);
        Assert.StartsWith("payment-failed", EclairJson.ToPaymentResult(failed).FailureReason, StringComparison.Ordinal);
        Assert.False(EclairJson.ToPaymentResult(null).Succeeded);
    }

    [Fact]
    public void Given_AnOpenAnswer_When_Read_Then_TheFundingTxIdIsFound()
    {
        // Act + Assert
        Assert.Equal(TxId, EclairJson.FundingTxIdOfOpen(
                               $"created channel 00ff with fundingTxId={TxId.ToUpperInvariant()} and fees=720 sat"));
        Assert.Null(EclairJson.FundingTxIdOfOpen("open failed"));
        Assert.Null(EclairJson.FundingTxIdOfOpen("fundingTxId=abc"));
    }

    [Fact]
    public void Given_ApiArguments_When_Formatted_Then_TheyAreWhatEclairParses()
    {
        // Act + Assert
        Assert.Equal("true", EclairApi.FormatArgument(true));
        Assert.Equal("1000000", EclairApi.FormatArgument(1_000_000L));
        Assert.Equal("text", EclairApi.FormatArgument("text"));
        Assert.Equal(new Uri("http://10.0.0.7:8080/"), EclairApi.BuildBaseAddress("10.0.0.7"));
        Assert.Equal(new Uri("http://[fd00::7]:8080/"), EclairApi.BuildBaseAddress("fd00::7"));
        Assert.Equal("created", EclairApi.ParseBody("\"created\"")!.GetValue<string>());
        Assert.Equal("created channel x", EclairApi.ParseBody("created channel x")!.GetValue<string>());
        Assert.Null(EclairApi.ParseBody(" "));
    }

    [Fact]
    public void Given_AnOpenRequest_When_TheParametersAreBuilt_Then_APushIsSentOnlyWhenThereIsOne()
    {
        // Act
        var plain = EclairTestPeer.BuildOpenParameters(new TestOpenChannelRequest("02ab", 500_000));
        var pushed = EclairTestPeer.BuildOpenParameters(new TestOpenChannelRequest("02ab", 500_000, 1_000, true));

        // Assert
        Assert.Null(plain.Single(p => p.Name == "pushMsat").Value);
        Assert.Equal(1_000L, pushed.Single(p => p.Name == "pushMsat").Value);
        Assert.Equal(true, pushed.Single(p => p.Name == "announceChannel").Value);
        Assert.Equal("anchor_outputs_zero_fee_htlc_tx", plain.Single(p => p.Name == "channelType").Value);
    }
}