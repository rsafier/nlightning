using Microsoft.Extensions.Options;

namespace NLightning.LndGrpc.Tests;

using Domain.Node.Options;
using Domain.Protocol.ValueObjects;

/// <summary>The <c>LndGrpc</c> listener rules (LND_GRPC_PLAN.md §2): TLS always, macaroons off loopback, mainnet
/// opt-in.</summary>
public class LndGrpcOptionsTests
{
    [Fact]
    public void Given_TheDefaults_When_Validated_Then_LoopbackWithMacaroonsIsFine()
    {
        Assert.Empty(new LndGrpcOptions().GetValidationErrors());
    }

    [Fact]
    public void Given_ANonLoopbackListenerWithMacaroons_When_Validated_Then_ItIsAllowedAsInLnd()
    {
        Assert.Empty(new LndGrpcOptions { ListenAddress = "0.0.0.0" }.GetValidationErrors());
    }

    [Theory]
    [InlineData("0.0.0.0", true, 1)]
    [InlineData("127.0.0.1", true, 0)]
    [InlineData("::1", true, 0)]
    [InlineData("localhost", false, 1)]
    public void Given_AllowNoMacaroons_When_Validated_Then_OnlyLoopbackPasses(string address, bool noMacaroons,
                                                                              int errors)
    {
        Assert.Equal(errors, new LndGrpcOptions { ListenAddress = address, AllowNoMacaroons = noMacaroons }
                             .GetValidationErrors().Count);
    }

    [Fact]
    public void Given_BadValues_When_Validated_Then_EachIsReported()
    {
        var options = new LndGrpcOptions
        {
            Port = 70_000,
            MaxConnections = 0,
            MaxDescribeGraphEdges = -1,
            TlsExtraIps = ["not-an-ip"],
            ClientCaPath = "/no/such/ca.pem"
        };

        Assert.Equal(5, options.GetValidationErrors().Count);
    }

    [Theory]
    [InlineData("mainnet", false, false)]
    [InlineData("mainnet", true, true)]
    [InlineData("regtest", false, true)]
    public void Given_ANetwork_When_TheValidatorRuns_Then_MainnetNeedsAllowMainnet(string network, bool allow,
                                                                                    bool valid)
    {
        // Arrange
        var validator = new LndGrpcOptionsValidator(Options.Create(new NodeOptions
        {
            BitcoinNetwork = new BitcoinNetwork(network)
        }));

        // Act
        var result = validator.Validate(null, new LndGrpcOptions { Enabled = true, AllowMainnet = allow });

        // Assert
        Assert.Equal(valid, result.Succeeded);
    }

    [Theory]
    [InlineData("regtest", "127.0.0.1", false, true)]
    [InlineData("regtest", "0.0.0.0", false, false)]
    [InlineData("mainnet", "127.0.0.1", false, false)]
    [InlineData("mainnet", "127.0.0.1", true, true)]
    public void Given_AnEnabledSigner_When_Validated_Then_RemoteTlsAndMainnetOptInAreRequired(string network, string address, bool mainnet, bool valid)
    {
        // Arrange
        var validator = new LndGrpcOptionsValidator(Options.Create(new NodeOptions { BitcoinNetwork = new BitcoinNetwork(network) }));
        // Act
        var result = validator.Validate(null, new LndGrpcOptions
        {
            Enabled = true,
            EnableSigner = true,
            AllowMainnet = true,
            AllowSignerOnMainnet = mainnet,
            ListenAddress = address
        });
        // Assert
        Assert.Equal(valid, result.Succeeded);
    }

    [Fact]
    public void Given_ADisabledServer_When_Validated_Then_NothingIsChecked()
    {
        var validator = new LndGrpcOptionsValidator(Options.Create(new NodeOptions()));

        Assert.True(validator.Validate(null, new LndGrpcOptions { Port = -1 }).Succeeded);
    }

    [Theory]
    [InlineData(null, "/cfg/lnd-grpc")]
    [InlineData("grpc", "/cfg/grpc")]
    [InlineData("/abs/dir", "/abs/dir")]
    public void Given_ADataDirectory_When_Resolved_Then_RelativeIsUnderTheConfigPath(string? configured,
                                                                                    string expected)
    {
        Assert.Equal(expected, new LndGrpcOptions { DataDirectory = configured }.ResolveDataDirectory("/cfg"));
    }
}