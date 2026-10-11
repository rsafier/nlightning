namespace NLightning.Testing.Cluster.Tests.Nodes.Bark;

using Cluster.Images;
using Cluster.Nodes;
using Cluster.Nodes.Bark;

public class BarkNodesTests
{
    [Fact]
    public void Given_TheWalletWorkload_When_Built_Then_TheCaptaindImageIdlesForExecWithAScratchDataVolume()
    {
        // Act
        var workload = BarkWalletNode.Workload("bw-wallet");

        // Assert: the image that carries bark (built with captaind from one commit), idling for kubectl exec
        Assert.Equal("bw-wallet", workload.Name);
        Assert.Equal(NodeKind.Other, workload.Kind);
        Assert.Equal(ImageVersions.Captaind, workload.Image);
        Assert.Equal(["sleep", "infinity"], workload.Command);
        Assert.Equal(BarkWalletNode.DataPath, workload.ScratchVolumes["wallet"]);
        Assert.Null(workload.ReadinessProbe);
    }

    [Fact]
    public void Given_WalletArguments_When_TheCommandIsBuilt_Then_ItIsQuietOnTheWalletsDataDirectory()
    {
        // Act
        var command = BarkWalletNode.Command(["balance"]);

        // Assert: --quiet keeps stdout the command's JSON alone
        Assert.Equal(["bark", "--quiet", "--datadir", "/wallet/bark", "balance"], command);
    }

    [Fact]
    public void Given_CaptaindAndItsChain_When_TheCreateArgumentsAreBuilt_Then_ARegtestWalletOnBothIsCreated()
    {
        // Act
        var arguments = BarkWalletNode.CreateArguments("http://captaind:3535", "http://bitcoind:18443", "u", "p");

        // Assert
        Assert.Equal(["create", "--regtest", "--ark", "http://captaind:3535", "--bitcoind", "http://bitcoind:18443",
                      "--bitcoind-user", "u", "--bitcoind-pass", "p"], arguments);
    }

    [Fact]
    public void Given_ACommandWithoutOutput_When_ItsJsonIsParsed_Then_ItIsAWalletError()
    {
        // Act
        var error = Assert.Throws<BarkWalletException>(() => BarkWalletNode.ParseJson(["balance"], "  \n"));

        // Assert
        Assert.Contains("printed no JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Given_TheTemplate_When_TheConfigIsBuilt_Then_PaymentReconciliationRunsAtTestCadence()
    {
        // Act
        var config = CaptaindNode.BuildConfig(new CaptaindNodeOptions());

        // Assert: an xpay left in flight is reconciled within seconds (base 1 s, at most 5 s apart), a waiting
        // CheckLightningPayment polls every 2 s
        Assert.Contains("invoice_check_base_delay = \"1s\"", config, StringComparison.Ordinal);
        Assert.Contains("max_invoice_check_delay = \"5s\"", config, StringComparison.Ordinal);
        Assert.Contains("invoice_poll_interval = \"2s\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("invoice_check_base_delay = \"10s\"", config, StringComparison.Ordinal);
    }
}