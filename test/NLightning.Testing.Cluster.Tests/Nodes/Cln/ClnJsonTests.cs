using System.Text.Json.Nodes;

namespace NLightning.Testing.Cluster.Tests.Nodes.Cln;

using Cluster.Nodes;
using Cluster.Nodes.Cln;

public class ClnJsonTests
{
    private const string NormalChannel = """
        {
          "peer_id": "03ABCD", "peer_connected": true, "state": "CHANNELD_NORMAL",
          "short_channel_id": "108x1x0", "funding_txid": "AA11", "funding_outnum": 1,
          "total_msat": 1000000000, "to_us_msat": 850000000
        }
        """;

    [Theory]
    [InlineData("1234", 1234)]
    [InlineData("\"1234msat\"", 1234)]
    [InlineData("null", 0)]
    public void Given_AnAmount_When_ReadAsMsat_Then_BothFormatsAreRead(string json, long expected)
    {
        // Act
        var msat = ClnJson.ReadMsat(JsonNode.Parse(json));

        // Assert
        Assert.Equal(expected, msat);
    }

    [Fact]
    public void Given_ANonAmount_When_ReadAsMsat_Then_ItThrows()
    {
        // Act + Assert
        Assert.Throws<FormatException>(() => ClnJson.ReadMsat(JsonNode.Parse("[1]")));
    }

    [Fact]
    public void Given_ANormalConnectedChannel_When_Read_Then_ItIsActiveWithLowerCaseIds()
    {
        // Act
        var channel = ClnJson.ToTestChannel(JsonNode.Parse(NormalChannel)!);

        // Assert
        Assert.Equal(new TestChannel("03abcd", "aa11", 1, "108x1x0", 1_000_000, 850_000_000, true), channel);
    }

    [Theory]
    [InlineData("peer_connected", "false")]
    [InlineData("state", "\"CHANNELD_AWAITING_LOCKIN\"")]
    [InlineData("state", "\"ONCHAIN\"")]
    public void Given_AChannelNotUsable_When_Read_Then_ItIsInactive(string field, string value)
    {
        // Arrange
        var json = JsonNode.Parse(NormalChannel)!.AsObject();
        json[field] = JsonNode.Parse(value);

        // Act
        var channel = ClnJson.ToTestChannel(json);

        // Assert
        Assert.False(channel.Active);
    }

    [Fact]
    public void Given_AnUnconfirmedChannel_When_Read_Then_ItHasNoShortChannelId()
    {
        // Arrange
        var json = JsonNode.Parse(NormalChannel)!.AsObject();
        json.Remove("short_channel_id");

        // Act
        var channel = ClnJson.ToTestChannel(json);

        // Assert
        Assert.Null(channel.ShortChannelId);
    }

    [Fact]
    public void Given_ListPeerChannels_When_Read_Then_EveryChannelIsReturned()
    {
        // Arrange
        var json = JsonNode.Parse($"{{\"channels\":[{NormalChannel},{NormalChannel}]}}")!;

        // Act + Assert
        Assert.Equal(2, ClnJson.ToTestChannels(json).Count);
        Assert.Empty(ClnJson.ToTestChannels(JsonNode.Parse("{}")!));
    }

    [Fact]
    public void Given_ListFunds_When_TheConfirmedBalanceIsRead_Then_OnlyConfirmedUnreservedOutputsCount()
    {
        // Arrange
        var json = JsonNode.Parse("""
            {"outputs": [
              {"amount_msat": 1000000000, "status": "confirmed", "reserved": false},
              {"amount_msat": 500000000, "status": "confirmed", "reserved": true},
              {"amount_msat": 250000000, "status": "unconfirmed", "reserved": false},
              {"amount_msat": 2000000, "status": "confirmed"}
            ]}
            """)!;

        // Act
        var sat = ClnJson.ConfirmedFundsSat(json);

        // Assert
        Assert.Equal(1_002_000, sat);
    }

    [Theory]
    [InlineData("{\"payment_preimage\":\"AB\"}", true)]
    [InlineData("{\"payment_preimage\":\"AB\",\"status\":\"complete\"}", true)]
    [InlineData("{\"status\":\"pending\"}", false)]
    public void Given_APaymentResult_When_Read_Then_OnlyAPreimageMeansPaid(string json, bool succeeded)
    {
        // Act
        var result = ClnJson.ToPaymentResult(JsonNode.Parse(json)!);

        // Assert
        Assert.Equal(succeeded, result.Succeeded);
        Assert.Equal(succeeded ? "ab" : null, result.PreimageHex);
        Assert.Equal(succeeded, result.FailureReason is null);
    }
}