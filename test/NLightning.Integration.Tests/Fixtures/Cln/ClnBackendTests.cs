using System.Text.Json.Nodes;

namespace NLightning.Integration.Tests.Fixtures.Cln;

using Docker.Utils;
using ClnNode = Testing.Cluster.Nodes.Cln.ClnNode;
using ClnNodeOptions = Testing.Cluster.Nodes.Cln.ClnNodeOptions;

public class ClnBackendTests
{
    [Fact]
    public void Given_TheFixturesCln_When_ArgsBuilt_Then_TheyAreTheFixturesCommandLine()
    {
        // Arrange: the command line the fixture's CLN always ran with, on the run's chain (alias miner)
        string[] expected =
        [
            "--bitcoin-rpcconnect=miner", "--bitcoin-rpcport=18443", "--bitcoin-rpcuser=nltg",
            "--bitcoin-rpcpassword=nltg", "--bind-addr=0.0.0.0:9735", "--alias=nltg-cln", "--log-level=debug",
            "--developer", "--dev-bitcoind-poll=1", "--ignore-fee-limits=false"
        ];

        // Act
        var args = ClnNode.BuildArgs(ClnFixture.ClnContainerName, Options(ClnFixture.ClnContainerName, true, []));

        // Assert
        Assert.Equal(expected, args);
    }

    [Fact]
    public void Given_ADualFundingSpec_When_ArgsBuilt_Then_TheyAreTheDualFundClassesCommandLine()
    {
        // Arrange: ClnDualFundTests' own node (no fee-limit flag, its options last)
        string[] extra =
        [
            "--experimental-dual-fund", "--funding-confirms=3", "--funder-lease-requests-only=false",
            "--funder-policy=match", "--funder-policy-mod=100", "--funder-min-their-funding=10000sat"
        ];
        string[] expected =
        [
            "--bitcoin-rpcconnect=miner", "--bitcoin-rpcport=18443", "--bitcoin-rpcuser=nltg",
            "--bitcoin-rpcpassword=nltg", "--bind-addr=0.0.0.0:9735", "--alias=nltg-cln-df", "--log-level=debug",
            "--developer", "--dev-bitcoind-poll=1", .. extra
        ];

        // Act
        var args = ClnNode.BuildArgs("nltg-cln-df", Options("nltg-cln-df", false, extra));

        // Assert
        Assert.Equal(expected, args);
    }

    [Fact]
    public async Task Given_ResultAfterNotificationLines_When_Called_Then_TheJsonIsReturnedAndTheCommandLineIsCliStyle()
    {
        // Arrange
        IReadOnlyList<string>? seen = null;
        var client = new ClnClient("nltg-cln", (command, _) =>
        {
            seen = command;
            return Task.FromResult(new ClnExecResult(0, "# progress\n{\"id\":\"02ab\"}", string.Empty));
        });

        // Act
        var result = await client.CallAsync("connect", TestContext.Current.CancellationToken, ("id", "02ab"),
                                            ("port", 9735), ("announce", true), ("ids", new JsonArray("a", "b")));

        // Assert
        Assert.Equal("02ab", result["id"]!.GetValue<string>());
        Assert.Equal(["lightning-cli", "--network=regtest", "--notifications=none", "-k", "connect", "id=02ab",
                      "port=9735", "announce=true", "ids=[\"a\",\"b\"]"], seen);
    }

    [Fact]
    public async Task Given_AClnErrorObject_When_Called_Then_ClnRpcExceptionCarriesItsCodeAndMessage()
    {
        // Arrange
        var client = new ClnClient("nltg-cln", (_, _) => Task.FromResult(
                                       new ClnExecResult(1, "{\"code\":-32602,\"message\":\"bad id\"}", string.Empty)));

        // Act
        var exception = await Assert.ThrowsAsync<ClnRpcException>(
            () => client.CallAsync("connect", TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(-32602, exception.Code);
        Assert.Equal("bad id", exception.ClnMessage);
    }

    [Fact]
    public async Task Given_AFailedRunWithoutJson_When_Called_Then_ClnRpcExceptionCarriesTheExitCodeAndOutput()
    {
        // Arrange
        var client = new ClnClient("nltg-cln", (_, _) => Task.FromResult(
                                       new ClnExecResult(126, string.Empty, "container not running")));

        // Act
        var exception = await Assert.ThrowsAsync<ClnRpcException>(
            () => client.CallAsync("getinfo", TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(126, exception.Code);
        Assert.Contains("container not running", exception.ClnMessage);
    }

    [Fact]
    public void Given_AnExtraNodeSpec_When_Defaulted_Then_ItEnforcesFeeLimitsAndIsReachableButNotRestartable()
    {
        // Arrange
        var spec = new ClnNodeSpec("nltg-cln2");

        // Act (defaults)

        // Assert
        Assert.True(spec.EnforceFeeLimits);
        Assert.True(spec.ReachableFromTests);
        Assert.False(spec.Restartable);
        Assert.Empty(spec.ExtraArgs);
    }

    private static ClnNodeOptions Options(string alias, bool enforceFeeLimits, IReadOnlyList<string> extraArgs) =>
        new()
        {
            BitcoindHost = "miner",
            BitcoindRpcUser = "nltg",
            BitcoindRpcPassword = "nltg",
            Alias = alias,
            EnforceFeeLimits = enforceFeeLimits,
            ExtraArgs = extraArgs
        };
}