using System.Diagnostics;
using System.Net.Sockets;

namespace NLightning.Testing.Cluster.Tests.Live;

using Cluster.Chain;
using Cluster.Images;
using Cluster.Nodes.BitcoinCore;
using Cluster.Nodes.BitcoinCore.Rpc;
using Cluster.Run;

/// <summary>
/// bitcoind and the chain helpers against a real cluster (explicit, <c>Category=Cluster</c>; see
/// <see cref="ClusterSmokeTests"/> for how to run them). Each test owns a <c>nltg-spike-&lt;id&gt;</c> namespace and
/// deletes it; the output records the timings and whether the host routes to the pod.
/// </summary>
[Trait("Category", "Cluster")]
public class BitcoinCoreClusterTests
{
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan s_waitTimeout = TimeSpan.FromSeconds(60);

    private static TestRunOptions Options(string suite) =>
        TestRunOptions.FromEnvironment(suite) with
        {
            Quota = NamespaceQuota.Spike,
            Log = Log
        };

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static RegtestChainOptions ChainOptions => new() { Log = Log, DefaultTimeout = s_waitTimeout };

    [Theory(Explicit = true)]
    [InlineData("31.1")]
    public async Task Given_ACluster_When_BitcoindIsDeployedAndMines101Blocks_Then_TheHostReadsTheTip(string version)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var image = ImageVersions.BitcoinCore;
        var watch = Stopwatch.StartNew();
        await using var run = await TestRun.StartAsync(Options($"chain-{version.Replace('.', '-')}"), ct);

        // Act: deploy, then mine over the route the harness picks for the host
        var miner = await BitcoinCoreNode.DeployAsync(run, new BitcoinCoreOptions { Image = image }, s_readyTimeout, ct);
        var readyAfter = watch.Elapsed;
        var probe = await miner.ProbeHostRouteAsync(ct);
        var rpc = await miner.ConnectRpcAsync(RpcRoute.Auto, ct);
        var chain = new RegtestChain(rpc, ChainOptions);
        watch.Restart();
        var hashes = await chain.MineAsync(101, ct);
        var mineTime = watch.Elapsed;

        // Assert: the same tip over every route that works from here
        var exec = miner.CreateRpc(RpcRoute.Exec);
        var execTip = await exec.GetTipAsync(ct);
        Assert.Equal(101, execTip.Height);
        Assert.Equal(hashes[^1], execTip.Hash);
        Assert.Equal("regtest", (await exec.GetChainInfoAsync(ct)).Chain);
        Log($"{run.Namespace}: bitcoind {version} ready after {readyAfter.TotalSeconds:F1} s at {miner.Handle.PodIp}; "
          + $"route {rpc.Description}; mined 101 in {mineTime.TotalMilliseconds:F0} ms; host probe: {probe}");

        watch.Restart();
        var podIpTip = probe.PodIpReachable ? await miner.CreateRpc(RpcRoute.PodIp).GetTipAsync(ct) : null;
        var podIpReadTime = watch.Elapsed;
        var dnsTip = probe.ServiceDnsReachable ? await miner.CreateRpc(RpcRoute.ServiceDns).GetTipAsync(ct) : null;
        Log($"host read of the tip: pod IP {(podIpTip is null ? "not routed" : $"{podIpTip} in {podIpReadTime.TotalMilliseconds:F0} ms")}, "
          + $"service DNS {(dnsTip is null ? "not routed" : dnsTip.ToString())}");
        if (podIpTip is not null)
            Assert.Equal(execTip, podIpTip);
        if (dnsTip is not null)
            Assert.Equal(execTip, dnsTip);

        // The NBitcoin client (what the in-process node's RegtestBitcoinEndpoint takes) and the ZMQ feed by pod IP
        if (probe.PodIpReachable)
        {
            var nbitcoin = miner.CreateNBitcoinClient(RpcRoute.PodIp);
            Assert.Equal(101, await nbitcoin.GetBlockCountAsync(ct));
            var greeting = await ReadZmtpSignatureAsync(miner.Handle.PodIp!, BitcoinCorePorts.ZmqRawBlock, ct);
            Assert.Equal(0xFF, greeting[0]);
            Assert.Equal(0x7F, greeting[9]);
            Log($"ZMQ raw block feed at {miner.Handle.PodIp}:{BitcoinCorePorts.ZmqRawBlock} answers a ZMTP greeting");
        }

        // settxfee: deprecated in newer Core; record whether this image still takes it
        string setTxFee;
        try
        {
            await chain.SetWalletFeeRateAsync(4m, ct);
            await chain.SetWalletFeeRateAsync(0m, ct);
            setTxFee = "accepted";
        }
        catch (NotSupportedException e)
        {
            setTxFee = $"refused ({((BitcoinRpcException)e.InnerException!).Code}: {e.InnerException!.Message})";
        }

        Log($"settxfee on bitcoind {version}: {setTxFee}");

        // A restart keeps the chain and the wallet (PVC, load_on_startup), and HTTP follows a new pod IP
        var oldIp = miner.Handle.PodIp;
        watch.Restart();
        await miner.Handle.RestartAsync(s_readyTimeout, ct);
        var restartTime = watch.Elapsed;
        Assert.Equal(execTip, await rpc.GetTipAsync(ct));
        Assert.True(await rpc.GetTrustedBalanceSatAsync(ct) > 0);
        Log($"restart {restartTime.TotalSeconds:F1} s, pod IP {oldIp} -> {miner.Handle.PodIp}, tip and wallet kept");
    }

    [Fact(Explicit = true)]
    public async Task Given_AMinerAndAFollower_When_TheChainMovesAndReorgs_Then_TheHelpersTrackIt()
    {
        // Arrange: a miner and a wallet-less follower peered with it
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        await using var run = await TestRun.StartAsync(Options("chain-follow"), ct);
        var minerTask = BitcoinCoreNode.DeployAsync(run, new BitcoinCoreOptions(), s_readyTimeout, ct);
        var followerTask = BitcoinCoreNode.DeployAsync(run, new BitcoinCoreOptions
        {
            Name = "follower",
            Wallet = null,
            AddNodes = ["miner"]
        }, s_readyTimeout, ct);
        var miner = await minerTask;
        var follower = await followerTask;
        await follower.ConnectToAsync(miner, s_waitTimeout, ct);
        var deployTime = watch.Elapsed;
        var chain = new RegtestChain(await miner.ConnectRpcAsync(RpcRoute.Auto, ct), ChainOptions);
        var followerRpc = await follower.ConnectRpcAsync(RpcRoute.Auto, ct);
        // Two followers with their own predicates: tip hash over RPC, and the height read by exec in the pod
        ChainFollower[] followers =
        [
            follower.AsFollower(followerRpc),
            ChainFollower.AtHeight("follower-cli", async c =>
                long.Parse((await follower.Handle.ExecAsync(
                                BitcoinCoreWorkload.CliCommand(follower.Options, null, "getblockcount"), c))
                          .EnsureSuccess("getblockcount").StdOutText.Trim(),
                           System.Globalization.CultureInfo.InvariantCulture))
        ];
        Log($"{run.Namespace}: miner + follower deployed and peered in {deployTime.TotalSeconds:F1} s; "
          + $"miner RPC {chain.Rpc.Description}, follower RPC {followerRpc.Description}");

        // Act + Assert: mine and wait at the barrier
        watch.Restart();
        var tip = await chain.MineAndWaitAsync(101, followers, ct);
        Assert.Equal(101, tip.Height);
        Log($"mine 101 + both followers at the tip: {watch.Elapsed.TotalMilliseconds:F0} ms");

        // A transaction: mempool, then confirmed in the tip block
        watch.Restart();
        var address = await chain.Rpc.GetNewAddressAsync(ct);
        var txId = await chain.SendAsync(address, 1_000_000, ct, feeRateSatPerVb: 3m);
        await chain.WaitForMempoolAsync(txId, ct);
        var confirmed = await chain.MineUntilConfirmedAsync(txId, 1, ct);
        Assert.Equal(102, confirmed.BlockHeight);
        await chain.WaitAllAtTipAsync(followers, ct);
        Log($"tx {txId}: mempool -> confirmed at {confirmed.BlockHeight} in {watch.Elapsed.TotalMilliseconds:F0} ms");

        // A 1-block reorg onto 2 empty blocks: the transaction goes back to the mempool, the followers follow. (A fork
        // below 101 would drop it instead: the wallet set its lock time to the tip at send, 101.)
        watch.Restart();
        var reorg = await chain.ReorgAsync(1, ct, new ReorgOptions { Transactions = ReorgTransactions.Drop });
        Assert.Equal(101, reorg.ForkHeight);
        Assert.Equal(103, reorg.NewTip.Height);
        Assert.Equal(TxState.InMempool, (await chain.Rpc.GetTransactionStatusAsync(txId, ct)).State);
        var afterReorg = await chain.WaitAllAtTipAsync(followers, ct);
        Assert.Equal(reorg.NewTip, afterReorg);
        Log($"reorg depth 1 -> 2 empty blocks, tx back in the mempool, followers on the new branch in "
          + $"{watch.Elapsed.TotalMilliseconds:F0} ms");

        // reconsiderblock of the shorter old branch leaves the new one active
        Assert.Equal(reorg.NewTip, await chain.ReconsiderAsync(reorg, ct));

        // A 2-block reorg that mines the mempool again: the reconsidered original 102 takes over when 102' goes, so it
        // is invalidated too, and the transaction confirms in the new branch's first block
        watch.Restart();
        var remined = await chain.ReorgAsync(2, ct);
        Assert.Equal(101, remined.ForkHeight);
        Assert.Equal([reorg.InvalidatedHash], remined.OtherInvalidatedHashes);
        Assert.Equal(104, remined.NewTip.Height);
        var reconfirmed = await chain.Rpc.GetTransactionStatusAsync(txId, ct);
        Assert.Equal(TxState.Confirmed, reconfirmed.State);
        Assert.Equal(remined.NewHashes[0], reconfirmed.BlockHash);
        Assert.Equal(102, reconfirmed.BlockHeight);
        Assert.Equal(remined.NewTip, await chain.WaitAllAtTipAsync(followers, ct));
        Log($"reorg depth 2 -> 3 blocks, tx confirmed again at 102 on the new branch, followers there in "
          + $"{watch.Elapsed.TotalMilliseconds:F0} ms");

        // Fee control: settxfee, then seed estimatesmartfee
        await chain.SetWalletFeeRateAsync(4m, ct);
        var feeTxId = await chain.SendAsync(address, 50_000, ct);
        var feeTx = await chain.Rpc.CallAsync("getmempoolentry",
                                              new Dictionary<string, object?> { ["txid"] = feeTxId }, ct);
        var paidSatPerVb = feeTx["fees"]!.Value<decimal>("base") * BitcoinCoreRpcClient.SatoshisPerBitcoin
                         / feeTx.Value<decimal>("vsize");
        Log($"settxfee 4 sat/vB: the next send paid {paidSatPerVb:F2} sat/vB");
        Assert.InRange(paidSatPerVb, 3.9m, 4.5m);
        await chain.SetWalletFeeRateAsync(0m, ct);
        Assert.Null(await chain.Rpc.EstimateSmartFeeAsync(2, FeeEstimateMode.Economical, ct));

        watch.Restart();
        var estimate = await chain.SeedFeeEstimatesAsync(12m, ct);
        Log($"estimatesmartfee seeded in {watch.Elapsed.TotalSeconds:F1} s: {estimate.SatPerVb} sat/vB at {estimate.Blocks}");
        Assert.InRange(estimate.SatPerVb, 11m, 14m);
        await chain.WaitAllAtTipAsync(followers, ct);
    }

    /// <summary>The 10-byte ZMTP signature a ZMQ socket sends on connect (0xFF, 8 bytes, 0x7F).</summary>
    private static async Task<byte[]> ReadZmtpSignatureAsync(string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        var stream = tcp.GetStream();
        var buffer = new byte[10];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }
}