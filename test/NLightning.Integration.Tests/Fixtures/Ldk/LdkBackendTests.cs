namespace NLightning.Integration.Tests.Fixtures.Ldk;

using Docker.Utils;
using Testing.Cluster.Nodes.Ldk;

public class LdkBackendTests
{
    [Fact]
    public void Given_TheFixturesLdk_When_TheDockerConfigIsBuilt_Then_ItIsTheConfigTheFixtureWroteBeforeTheSplit()
    {
        // Arrange: the config.toml LdkFixture copied into its container before the backend split
        const string expected = """
                                [node]
                                network = "regtest"
                                listening_addresses = ["0.0.0.0:9735"]
                                announcement_addresses = ["127.0.0.1:20123"]
                                alias = "nltg-ldk"

                                [storage.disk]
                                dir_path = "/data/ldk"

                                [log]
                                level = "Info"
                                log_to_file = false

                                [bitcoind]
                                rpc_address = "nltg-ldk-bitcoind:18443"
                                rpc_user = "nltg"
                                rpc_password = "nltg"
                                """;

        // Act
        var config = DockerLdkBackend.BuildConfig(20123);

        // Assert
        Assert.Equal(expected, config);
    }

    [Fact]
    public void Given_TheSameNode_When_TheClusterConfigIsBuilt_Then_OnlyTheBitcoindHostAndTheAnnouncedAddressDiffer()
    {
        // Arrange: the cluster's node as ClusterLdkBackend declares it (chain alias miner, its stable ClusterIP)
        var options = new LdkNodeOptions
        {
            BitcoindHost = "miner",
            BitcoindRpcUser = InteropChainHost.RpcUser,
            BitcoindRpcPassword = InteropChainHost.RpcPassword,
            Alias = LdkFixture.LdkAlias,
            AnnouncementAddresses = ["10.43.0.7:9735"]
        };

        // Act
        var cluster = LdkNode.BuildConfig(LdkFixture.LdkContainerName, options);
        var docker = DockerLdkBackend.BuildConfig(20123)
                                     .Replace("127.0.0.1:20123", "10.43.0.7:9735", StringComparison.Ordinal)
                                     .Replace($"{LdkFixture.BitcoinContainerName}:", "miner:", StringComparison.Ordinal);

        // Assert
        Assert.Equal(docker, cluster);
        Assert.Equal(LdkFixture.ConfigPath, LdkNode.ConfigPath);
    }

    [Fact]
    public async Task Given_AnExecDelegate_When_Called_Then_TheCliReadsTheGivenConfigAndTheJsonIsReturned()
    {
        // Arrange
        IReadOnlyList<string>? seen = null;
        var client = new LdkClient("nltg-ldk", "/data/config.toml", (command, _) =>
        {
            seen = command;
            return Task.FromResult(new LdkExecResult(0, """{"node_id":"02ab","current_best_block":{"height":7}}""",
                                                     string.Empty));
        });

        // Act
        var height = await client.GetBlockHeightAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(7u, height);
        Assert.Equal(["ldk-server-cli", "-c", "/data/config.toml", "get-node-info"], seen);
    }

    [Fact]
    public async Task Given_AFailedCall_When_Called_Then_LdkCliExceptionCarriesTheExitCodeOutputAndJson()
    {
        // Arrange
        var client = new LdkClient("nltg-ldk", "/data/config.toml", (_, _) => Task.FromResult(
                                       new LdkExecResult(2, """{"status":"FAILED"}""", "Error: route not found")));

        // Act
        var exception = await Assert.ThrowsAsync<LdkCliException>(
            () => client.PayAsync("lnbcrt1", 5, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(2, exception.ExitCode);
        Assert.Contains("route not found", exception.LdkMessage, StringComparison.Ordinal);
        Assert.Equal("FAILED", LdkClient.StatusOf(exception.Json));
    }
}