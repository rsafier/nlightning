namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Protocol.ValueObjects;
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
    public void Given_ANegativeTipPollInterval_When_Validated_Then_ItIsReported()
    {
        // Arrange
        var options = Valid();
        options.TipPollInterval = TimeSpan.FromSeconds(-1);

        // Act
        var errors = options.GetValidationErrors();

        // Assert: zero turns the poll off and is valid; a negative value is an error
        Assert.StartsWith("Bitcoin:TipPollInterval", Assert.Single(errors), StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(30), new BitcoinOptions().TipPollInterval);
    }

    [Fact]
    public void Given_AnEmptySection_When_Validated_Then_EveryRequiredSettingIsReported()
    {
        // Act: what the binder leaves when the section is missing (NL-338: the members are no longer required)
        var errors = new BitcoinOptions().GetValidationErrors();

        // Assert: no RpcEndpoint means 127.0.0.1 on the network's RPC port, as NBitcoin's RPC client takes it (NL-740)
        Assert.Equal(4, errors.Count);
        Assert.DoesNotContain(errors, e => e.StartsWith("Bitcoin:RpcEndpoint", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Bitcoin:RpcUser", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Bitcoin:RpcPassword", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Bitcoin:ZmqHost", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Bitcoin:ZmqBlockPort", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("localhost:18443")]
    [InlineData("127.0.0.1:8332")]
    [InlineData("bitcoind")]
    [InlineData("http://127.0.0.1:8332/wallet/nltg")]
    [InlineData("")]
    public void Given_AnEndpointTheRpcClientAccepts_When_Validated_Then_ItIsNotReported(string endpoint)
    {
        // Arrange (NL-740): NBitcoin's RPCClient adds http:// to a host[:port] and takes empty as 127.0.0.1
        var options = Valid();
        options.RpcEndpoint = endpoint;

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("ftp://localhost:18443")]
    [InlineData("/rpc")]
    [InlineData("localhost:notaport")]
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

    [Fact]
    public void Given_PollModeWithoutZmq_When_Validated_Then_ThereIsNoError()
    {
        // Arrange (NL-1094): a node without ZMQ (rbitcoin)
        var options = Valid();
        options.Notifications = ChainNotificationMode.Poll;
        options.ZmqHost = null;
        options.ZmqBlockPort = 0;
        options.ZmqTxPort = 0;

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Given_ZmqModeWithoutZmq_When_Validated_Then_TheErrorPointsAtPollMode()
    {
        // Arrange
        var options = Valid();
        options.ZmqHost = null;

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains("Notifications", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(249)]
    [InlineData(60_001)]
    public void Given_APollIntervalOutOfRange_When_Validated_Then_ItIsReported(int milliseconds)
    {
        // Arrange
        var options = Valid();
        options.Notifications = ChainNotificationMode.Poll;
        options.PollInterval = TimeSpan.FromMilliseconds(milliseconds);

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.StartsWith("Bitcoin:PollInterval", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Given_AnUnknownNotificationMode_When_Validated_Then_ItIsReported()
    {
        // Arrange
        var options = Valid();
        options.Notifications = (ChainNotificationMode)7;

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.StartsWith("Bitcoin:Notifications", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Given_NoPollInterval_When_Read_Then_TheNetworkDefaultIsUsed()
    {
        // Arrange
        var options = Valid();

        // Act / Assert
        Assert.Equal(TimeSpan.FromSeconds(2), options.GetPollInterval(BitcoinNetwork.Regtest));
        Assert.Equal(TimeSpan.FromSeconds(5), options.GetPollInterval(BitcoinNetwork.Signet));
        Assert.Equal(TimeSpan.FromSeconds(5), options.GetPollInterval(BitcoinNetwork.Testnet4));
        Assert.Equal(TimeSpan.FromSeconds(10), options.GetPollInterval(BitcoinNetwork.Mainnet));
        options.PollInterval = TimeSpan.FromSeconds(3);
        Assert.Equal(TimeSpan.FromSeconds(3), options.GetPollInterval(BitcoinNetwork.Mainnet));
    }

    [Theory]
    [InlineData(ChainNotificationMode.Zmq, null, true)]
    [InlineData(ChainNotificationMode.Poll, null, false)]
    [InlineData(ChainNotificationMode.Poll, true, true)]
    [InlineData(ChainNotificationMode.Zmq, false, false)]
    public void Given_WatchMempool_When_Read_Then_UnsetFollowsTheMode(ChainNotificationMode mode, bool? watch,
                                                                      bool expected)
    {
        // Arrange
        var options = Valid();
        options.Notifications = mode;
        options.WatchMempool = watch;

        // Act / Assert
        Assert.Equal(expected, options.IsMempoolWatched);
    }
}