using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;
using Options;

public class BitcoinChainServiceTests
{
    [Fact]
    public void Given_UnknownNetwork_When_Constructed_Then_ItThrowsBeforeTalkingToBitcoind()
    {
        // Arrange: an endpoint nothing listens on; the network check must fail first
        var bitcoinOptions = new OptionsWrapper<BitcoinOptions>(new BitcoinOptions
        {
            RpcEndpoint = "http://127.0.0.1:1",
            RpcUser = "user",
            RpcPassword = "password",
            ZmqHost = "127.0.0.1",
            ZmqBlockPort = 1,
            ZmqTxPort = 1
        });
        var nodeOptions = new OptionsWrapper<NodeOptions>(new NodeOptions { BitcoinNetwork = new BitcoinNetwork("unknown-net") });

        // Act / Assert
        Assert.Throws<ArgumentException>(() => new BitcoinChainService(bitcoinOptions,
                                                                       NullLogger<BitcoinChainService>.Instance,
                                                                       nodeOptions));
    }
}