namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Infrastructure.Bitcoin.Options;

/// <summary>
/// <see cref="BitcoinOptions"/> configures the chain service and the block monitor (Wallet); a Tests.Options namespace
/// would shadow the Options namespace other test folders import.
/// </summary>
public class BitcoinOptionsTests
{
    private static BitcoinOptions Valid() => new()
    {
        RpcEndpoint = "http://localhost:18443",
        RpcUser = "user",
        RpcPassword = "password",
        ZmqHost = "127.0.0.1",
        ZmqBlockPort = 28332,
        ZmqTxPort = 28333
    };

    [Fact]
    public void Given_EverySettingSet_When_Validated_Then_ThereIsNoError()
    {
        // Act
        var errors = Valid().GetValidationErrors();

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Given_NoTxPort_When_Validated_Then_ThereIsNoError()
    {
        // Arrange: files written before BOLT 5 O8 have no ZmqTxPort
        var options = Valid();
        options.ZmqTxPort = 0;

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Given_AnEmptySection_When_Validated_Then_EveryRequiredSettingIsReported()
    {
        // Act: what the binder leaves when the section is missing (NL-338: the members are no longer required)
        var errors = new BitcoinOptions().GetValidationErrors();

        // Assert
        Assert.Equal(5, errors.Count);
        Assert.Contains(errors, e => e.StartsWith("Bitcoin:RpcEndpoint", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Bitcoin:RpcUser", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Bitcoin:RpcPassword", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Bitcoin:ZmqHost", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Bitcoin:ZmqBlockPort", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("localhost:18443")]
    [InlineData("ftp://localhost:18443")]
    [InlineData("/rpc")]
    public void Given_AnEndpointThatIsNotAnHttpUrl_When_Validated_Then_ItIsReported(string endpoint)
    {
        // Arrange
        var options = Valid();
        options.RpcEndpoint = endpoint;

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Single(errors);
        Assert.StartsWith("Bitcoin:RpcEndpoint", errors[0]);
    }

    [Theory]
    [InlineData(0, 28333)]
    [InlineData(65536, 28333)]
    [InlineData(28332, -1)]
    [InlineData(28332, 65536)]
    public void Given_APortOutOfRange_When_Validated_Then_ItIsReported(int blockPort, int txPort)
    {
        // Arrange
        var options = Valid();
        options.ZmqBlockPort = blockPort;
        options.ZmqTxPort = txPort;

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Single(errors);
    }
}