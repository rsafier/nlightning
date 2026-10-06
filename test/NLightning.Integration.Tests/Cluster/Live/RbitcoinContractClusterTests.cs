using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;

namespace NLightning.Integration.Tests.Cluster.Live;

using Domain.Channels.Enums;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Options;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Models;
using Testing.Cluster.Images;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes;
using Testing.Cluster.Nodes.BitcoinCore;
using Testing.Cluster.Nodes.Cln;
using Testing.Cluster.Nodes.Rbitcoin;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>
/// The rbitcoin contract (NL-1095): every bitcoind RPC NLightning uses, called through our own
/// <see cref="BitcoinChainService"/> and <see cref="FeeService"/> against rbitcoin (a Rust full node with a
/// Core-compatible JSON-RPC subset, no ZMQ and no wallet), with Bitcoin Core 31.1 mining and funding and rbitcoin
/// following it over P2P. rbitcoin (and Core) run with a raised minimum relay fee (5 sat/vB) so the low-fee refusal and
/// the package path of a below-floor parent are exercised, and the package answer is compared with Core's.
/// </summary>
/// <remarks>
/// Explicit and <c>Category=Cluster</c>: <c>scripts/run-cluster.sh -n 1 -p integration --class
/// NLightning.Integration.Tests.Cluster.Live.RbitcoinContractClusterTests</c>. Needs the local image
/// <see cref="ImageVersions.Rbitcoin"/> (<c>docker build -t nltg-spike-rbitcoin:9dd7ef99 test/Docker/rbitcoin</c>).
/// Every observed answer is written to the test output, so a run doubles as the record of what rbitcoin returns.
/// </remarks>
[Trait("Category", "Cluster")]
public class RbitcoinContractClusterTests
{
    /// <summary>rbitcoin's minimum relay fee here: 5 sat/vB (Core's default is 1, rbitcoin's own 0.1).</summary>
    private const decimal MinRelayBtcPerKvB = 0.00005m;

    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan s_followTimeout = TimeSpan.FromMinutes(2);
    private static readonly Money s_coinValue = Money.Coins(0.01m);

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_RbitcoinFollowingCore_When_EveryRpcWeUseIsCalledThroughOurServices_Then_TheAnswersMatchCore()
    {
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("rbitcoin") with
        {
            Quota = NamespaceQuota.Spike,
            Log = Log
        }, ct);
        try
        {
            // Arrange: Core 31.1 mines and funds; rbitcoin follows it over P2P
            var miner = await BitcoinCoreNode.DeployAsync(run, new BitcoinCoreOptions
            {
                Image = ImageVersions.BitcoinCore31,
                Storage = NodeStorage.Ephemeral,
                // The same static floor as rbitcoin's, so the package case is compared with Core's answer
                ExtraArgs = [$"-minrelaytxfee={MinRelayBtcPerKvB.ToString(System.Globalization.CultureInfo.InvariantCulture)}"]
            }, s_readyTimeout, ct);
            await miner.ConnectRpcAsync(RpcRoute.PodIp, ct);
            var core = miner.CreateNBitcoinClient(RpcRoute.PodIp);
            var coreNode = miner.CreateNBitcoinClient(RpcRoute.PodIp, string.Empty);
            var minerAddress = await core.GetNewAddressAsync(ct);
            await core.GenerateToAddressAsync(101, minerAddress, ct);

            var rbitcoin = await RbitcoinNode.DeployAsync(run, new RbitcoinNodeOptions
            {
                Connect = $"{miner.Handle.PodIp}:{BitcoinCorePorts.P2p}",
                MinRelayTxFeeBtcPerKvB = MinRelayBtcPerKvB
            }, s_readyTimeout, ct);
            var chain = CreateChainService(rbitcoin);
            Log($"{run.Namespace}: Core and rbitcoin up in {watch.Elapsed.TotalSeconds:F1} s; rbitcoin at {rbitcoin.RpcUrl}");

            var key = new Key();
            var ours = key.PubKey.WitHash.GetAddress(Network.RegTest);
            var funding = new List<uint256>();
            for (var i = 0; i < 5; i++)
                funding.Add(await core.SendToAddressAsync(ours, s_coinValue, cancellationToken: ct));
            await core.GenerateToAddressAsync(1, minerAddress, ct);
            var tip = (uint)await coreNode.GetBlockCountAsync(ct);
            await WaitFollowsAsync(chain, coreNode, tip, ct);
            Log($"rbitcoin followed Core to {tip} in {watch.Elapsed.TotalSeconds:F1} s");

            // Act/Assert: blocks (getblockcount, getblockhash, getblock 0 and 1, getblockheader)
            Assert.Equal(tip, await chain.GetCurrentBlockHeightAsync());
            var tipHash = await coreNode.GetBlockHashAsync((int)tip, ct);
            Assert.Equal(tipHash, await chain.GetBlockHashAsync(tip));
            var coreBlock = await coreNode.GetBlockAsync(tipHash, ct);
            var byHeight = await chain.GetBlockAsync(tip);
            Assert.NotNull(byHeight);
            Assert.Equal(coreBlock.ToBytes(), byHeight.ToBytes());
            Assert.Equal(coreBlock.ToBytes(), (await chain.GetBlockAsync(tipHash))!.ToBytes());
            var txIds = await chain.GetBlockTxIdsAsync(tip);
            Assert.NotNull(txIds);
            Assert.Equal(tipHash, txIds.Value.BlockHash);
            Assert.Equal(coreBlock.Transactions.Select(t => t.GetHash()), txIds.Value.TxIds);
            var summary = await chain.GetBlockHeaderSummaryAsync(tipHash);
            Assert.Equal((coreBlock.Header.HashMerkleRoot, coreBlock.Transactions.Count), summary);
            Assert.Equal(coreBlock.Header.BlockTime, await chain.GetBlockTimeAsync(tip));
            Assert.Null(await chain.GetBlockTxIdsAsync(tip + 10));
            var aboveTip = await Assert.ThrowsAsync<RPCException>(() => chain.GetBlockHashAsync(tip + 10));
            Log($"getblockhash above the tip: {(int)aboveTip.RPCCode} {aboveTip.Message}");
            Assert.Equal(RPCErrorCode.RPC_INVALID_PARAMETER, aboveTip.RPCCode);

            // gettxout and getrawtransaction (raw and verbose)
            var coins = new List<Coin>();
            foreach (var txId in funding)
            {
                var transaction = await chain.GetTransactionAsync(txId);
                Assert.NotNull(transaction);
                Assert.Equal((await core.GetRawTransactionAsync(txId, true, ct)).ToBytes(), transaction.ToBytes());
                Assert.Equal(1u, await chain.GetTransactionConfirmationsAsync(txId));
                var index = transaction.Outputs.FindIndex(o => o.ScriptPubKey == ours.ScriptPubKey);
                coins.Add(new Coin(transaction, (uint)index));
            }

            var unspent = await chain.GetUnspentOutputAsync(coins[0].Outpoint);
            Assert.NotNull(unspent);
            Assert.Equal(s_coinValue, unspent.Value.Output.Value);
            Assert.Equal(ours.ScriptPubKey, unspent.Value.Output.ScriptPubKey);
            Assert.Equal(tip, unspent.Value.Height);
            Assert.NotNull(await chain.GetConfirmedUnspentOutputAsync(coins[0].Outpoint));
            var unknown = new uint256(RandomUtils.GetBytes(32));
            Assert.Null(await chain.GetTransactionAsync(unknown));
            Assert.Equal(0u, await chain.GetTransactionConfirmationsAsync(unknown));
            Assert.Null(await chain.GetUnspentOutputAsync(new OutPoint(unknown, 0)));

            // getmempoolinfo: 5 sat/vB = 1,250 sat/kw
            var minFee = await chain.GetMempoolMinFeeRatePerKwAsync();
            Log($"getmempoolinfo mempoolminfee: {minFee} sat/kw");
            Assert.Equal(1_250u, minFee);

            // sendrawtransaction: below the minimum relay fee
            var lowFee = await RefusedAsync(chain, Spend(coins[0], key, 2), "below the minimum relay fee");
            Assert.True(BroadcastRefusalRules.IsNodeRefusal(lowFee));
            Assert.False(BroadcastRefusalRules.IsPermanent(lowFee));
            Assert.True(Contains(lowFee, "min relay fee not met") || Contains(lowFee, "mempool min fee not met"),
                        lowFee.Message);

            // sendrawtransaction accepted; gettxout with and without the mempool; gettxspendingprevout
            var spend = Spend(coins[0], key, 10);
            Assert.Equal(spend.GetHash(), await chain.SendTransactionAsync(spend));
            Assert.Null(await chain.GetUnspentOutputAsync(coins[0].Outpoint));
            Assert.NotNull(await chain.GetConfirmedUnspentOutputAsync(coins[0].Outpoint));
            var spenders = await chain.GetMempoolSpendersAsync([coins[0].Outpoint, coins[1].Outpoint]);
            Assert.NotNull(spenders);
            Assert.Equal(spend.GetHash(), Assert.Single(spenders).Value);
            Assert.Equal(spend.ToBytes(), (await chain.GetTransactionAsync(spend.GetHash()))!.ToBytes());

            // The same transaction again
            try
            {
                await chain.SendTransactionAsync(spend);
                Log("sendrawtransaction of a transaction already in the mempool: accepted (returns its txid)");
            }
            catch (RPCException again)
            {
                Log($"sendrawtransaction of a transaction already in the mempool: {(int)again.RPCCode} {again.Message}");
                Assert.False(BroadcastRefusalRules.IsPermanent(again));
            }

            // RBF without a higher fee
            var replacement = await RefusedAsync(chain, Spend(coins[0], key, 10, new Key()), "an RBF without more fee");
            Assert.True(Contains(replacement, "insufficient fee"), replacement.Message);
            Assert.False(BroadcastRefusalRules.IsPermanent(replacement));

            // Missing inputs
            var missing = await RefusedAsync(chain, Spend(new Coin(new OutPoint(unknown, 0), coins[1].TxOut), key, 10),
                                             "missing inputs");
            Assert.True(BroadcastRefusalRules.IsMissingInputs(missing), missing.Message);
            Assert.True(BroadcastRefusalRules.IsPermanent(missing));
            Assert.True(BroadcastRefusalRules.MayBeConfirmed(missing));

            // Script failure (signed by another key)
            var badScript = await RefusedAsync(chain, Spend(coins[1], new Key(), 10), "a bad signature");
            Assert.True(BroadcastRefusalRules.IsPermanent(badScript), badScript.Message);

            // submitpackage: a parent below the static floor (1 sat/vB) with a child paying for both (30 sat/vB
            // together), never sent alone
            var parent = Spend(coins[3], key, 1, key);
            var package = await chain.SubmitPackageAsync(parent, Spend(new Coin(parent, 0u), key, 60));
            Log($"submitpackage of a below-floor parent: {package.Status}: {package.Describe()}");
            Assert.Equal(PackageSubmitStatus.Accepted, package.Status);
            Assert.Equal(2, package.Transactions.Count);
            Assert.NotNull(package.PackageFeerateBtcPerKvb);

            // ... and in the anchors CPFP order: the parent sent alone and refused for its fee, then the package
            var lonely = Spend(coins[2], key, 1, key);
            var lonelyRefused = await RefusedAsync(chain, lonely, "a below-floor parent alone");
            Assert.False(BroadcastRefusalRules.IsPermanent(lonelyRefused));
            var rescued = await chain.SubmitPackageAsync(lonely, Spend(new Coin(lonely, 0u), key, 60));
            Log($"submitpackage after the parent was refused alone: {rescued.Status}: {rescued.Describe()}");
            Assert.Equal(PackageSubmitStatus.Accepted, rescued.Status);

            // Core 31.1 with the same floor, for comparison (its own chain service, the coin not seen by rbitcoin's
            // mempool)
            var coreChain = new BitcoinChainService(
                Options.Create(new BitcoinOptions
                {
                    RpcEndpoint = $"http://{miner.Handle.PodIp}:{BitcoinCorePorts.Rpc}",
                    RpcUser = miner.Options.RpcUser,
                    RpcPassword = miner.Options.RpcPassword,
                    Notifications = ChainNotificationMode.Poll
                }), NullLogger<BitcoinChainService>.Instance,
                Options.Create(new NodeOptions { BitcoinNetwork = "regtest" }));
            var coreParent = Spend(coins[4], key, 1, key);
            var corePackage = await coreChain.SubmitPackageAsync(coreParent, Spend(new Coin(coreParent, 0u), key, 60));
            Log($"Core 31.1 submitpackage of the same kind: {corePackage.Status}: {corePackage.Describe()}");
            Assert.Equal(PackageSubmitStatus.Accepted, corePackage.Status);

            // A confirmed transaction sent again (Core mines what rbitcoin did not relay to it)
            await coreNode.SendRawTransactionAsync(spend, ct);
            await core.GenerateToAddressAsync(1, minerAddress, ct);
            await WaitFollowsAsync(chain, coreNode, tip + 1, ct);
            Assert.Equal(1u, await chain.GetTransactionConfirmationsAsync(spend.GetHash()));
            try
            {
                await chain.SendTransactionAsync(spend);
                Log("sendrawtransaction of a confirmed transaction: accepted");
            }
            catch (RPCException confirmed)
            {
                Log($"sendrawtransaction of a confirmed transaction: {(int)confirmed.RPCCode} {confirmed.Message}");
                Assert.True(BroadcastRefusalRules.MayBeConfirmed(confirmed), confirmed.Message);
            }

            Assert.Null(await chain.GetConfirmedUnspentOutputAsync(coins[0].Outpoint));

            // estimatesmartfee: the shape our fee service reads (rbitcoin's own model, so no value is compared)
            var rbitcoinRpc = new RPCClient(new RPCCredentialString { UserPassword = rbitcoin.Credentials },
                                            rbitcoin.RpcUrl, Network.RegTest);
            var estimate = await rbitcoinRpc.TryEstimateSmartFeeAsync(6, EstimateSmartFeeMode.Conservative, ct);
            Log($"estimatesmartfee 6 conservative: {estimate?.FeeRate?.ToString() ?? "no estimate"}");
            var fees = CreateFeeService(rbitcoin);
            var feeRate = await fees.GetFeeRatePerKwAsync(6, ct);
            Log($"FeeService (Source=Bitcoind) for 6 blocks: {feeRate.Satoshi} sat/kw");
            Assert.True(feeRate.Satoshi >= 253);

            Log($"{run.Namespace}: contract checked in {watch.Elapsed.TotalSeconds:F1} s");
        }
        finally
        {
            await run.DisposeAsync();
        }
    }

    /// <summary>
    /// End to end on rbitcoin (NL-1095): our node's only chain backend is rbitcoin in poll mode (RPC endpoint, no
    /// ZMQ, mempool polled), while Core mines, funds and backs CLN; rbitcoin follows Core over P2P and relays our
    /// transactions to it. We open a channel to CLN, pay it and close cooperatively; both ends see the close.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Given_OurNodeOnRbitcoinInPollMode_When_WeOpenPayAndCloseWithCln_Then_BothEndsAgree()
    {
        const long walletSat = 2_000_000;
        const long capacitySat = 500_000;
        const long paymentMsat = 20_000_000;
        var stepTimeout = TimeSpan.FromMinutes(2);
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("rbitcoin-flow") with
        {
            Quota = NamespaceQuota.Spike,
            Log = Log
        }, ct);
        try
        {
            // Arrange: rbitcoin first (it follows the topology's bitcoind by its alias once that exists), then
            // bitcoind, our node on rbitcoin and CLN
            var rbitcoin = await RbitcoinNode.DeployAsync(run, new RbitcoinNodeOptions
            {
                Connect = $"miner:{BitcoinCorePorts.P2p}"
            }, s_readyTimeout, ct);
            await using var inProcess = new InProcessNodeDeployer();
            using var topology = await new TopologyBuilder
            {
                Log = Log,
                ReadyTimeout = s_readyTimeout,
                StepTimeout = stepTimeout
            }
                                      .AddBitcoinCore("miner")
                                      .AddNLightning("nltg", $"Bitcoin:RpcEndpoint={rbitcoin.RpcUrl}",
                                                     $"Bitcoin:RpcUser={rbitcoin.Options.RpcUser}",
                                                     $"Bitcoin:RpcPassword={rbitcoin.Options.RpcPassword}",
                                                     "Bitcoin:Notifications=Poll", "Bitcoin:PollInterval=00:00:01",
                                                     "Bitcoin:WatchMempool=true")
                                      .AddCln("cln")
                                      .UseInProcessNodes(inProcess)
                                      .FundWallet("nltg", walletSat)
                                      .BuildAsync(run, ct);
            var nltg = topology.InProcessNode("nltg");
            var cln = topology.Node<ClnTestPeer>("cln");
            var bitcoin = nltg.TestNode.Services.GetRequiredService<IOptions<BitcoinOptions>>().Value;
            Assert.Equal(rbitcoin.RpcUrl, bitcoin.RpcEndpoint);
            Assert.Equal(ChainNotificationMode.Poll, bitcoin.Notifications);
            Log($"{run.Namespace}: topology on rbitcoin in {watch.Elapsed.TotalSeconds:F1} s");

            // Act: open, confirm, pay, close
            var clnAddress = await cln.GetAddressAsync(ct);
            await TopologyDeployer.ConnectAsync(nltg, clnAddress, stepTimeout, ct);
            var open = await nltg.OpenChannelAsync(new TestOpenChannelRequest(clnAddress.NodeId, capacitySat),
                                                   InProcessOpenMode.V1, ct);
            var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
            await chain.Chain.WaitForMempoolAsync(open.Open.FundingTxId, ct, stepTimeout);
            await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
            await TopologyDeployer.WaitChannelActiveAsync(nltg, clnAddress.NodeId, open.Open.FundingTxId,
                                                          stepTimeout, ct);
            await TopologyDeployer.WaitChannelActiveAsync(cln, nltg.TestNode.NodeIdHex, open.Open.FundingTxId,
                                                          stepTimeout, ct);
            var invoice = await cln.CreateInvoiceAsync(paymentMsat, "paid on rbitcoin", ct);
            var paid = await nltg.PayInvoiceAsync(invoice.Bolt11, ct);
            Assert.True(paid.Succeeded, paid.FailureReason);
            var closingTxId = await nltg.CloseChannelAsync(open.ChannelId, ct);
            await chain.Chain.WaitForMempoolAsync(closingTxId, ct, stepTimeout);
            await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);

            // Assert: closed on both ends
            await ClusterPoll.UntilDoneAsync(async c =>
            {
                var ours = await nltg.FindChannelAsync(open.Open.FundingTxId, c);
                return ours is null || ours.State == ChannelState.Closed ? null : $"ours {ours.State}";
            }, stepTimeout, "our channel closed", ct);
            await ClusterPoll.UntilDoneAsync(async c =>
            {
                var channels = await cln.Rpc.CallAsync("listpeerchannels", c);
                var channel = channels["channels"]?.AsArray()
                                                  .FirstOrDefault(x => x?["funding_txid"]?.GetValue<string>()
                                                                    == open.Open.FundingTxId);
                var state = channel?["state"]?.GetValue<string>();
                return channel is null || state is "ONCHAIN" or "CLOSED" ? null : $"CLN {state}";
            }, stepTimeout, "CLN sees the mutual close on chain", ct);
            Log($"{run.Namespace}: opened, paid and closed ({closingTxId}) on rbitcoin in "
              + $"{watch.Elapsed.TotalSeconds:F1} s");
        }
        finally
        {
            await run.DisposeAsync();
        }
    }

    private static BitcoinChainService CreateChainService(RbitcoinNode rbitcoin) =>
        new(Options.Create(CreateBitcoinOptions(rbitcoin)), NullLogger<BitcoinChainService>.Instance,
            Options.Create(new NodeOptions { BitcoinNetwork = "regtest" }));

    private static FeeService CreateFeeService(RbitcoinNode rbitcoin) =>
        new(Options.Create(new FeeEstimationOptions { Source = FeeEstimationOptions.SourceBitcoind }),
            new HttpClient(), NullLogger<FeeService>.Instance, Options.Create(CreateBitcoinOptions(rbitcoin)),
            Options.Create(new NodeOptions { BitcoinNetwork = "regtest" }));

    private static BitcoinOptions CreateBitcoinOptions(RbitcoinNode rbitcoin) => new()
    {
        RpcEndpoint = rbitcoin.RpcUrl,
        RpcUser = rbitcoin.Options.RpcUser,
        RpcPassword = rbitcoin.Options.RpcPassword,
        Notifications = ChainNotificationMode.Poll
    };

    /// <summary>Until rbitcoin's tip is Core's block at <paramref name="height"/>.</summary>
    private static Task WaitFollowsAsync(BitcoinChainService chain, RPCClient core, uint height, CancellationToken ct) =>
        ClusterPoll.UntilAsync(async c =>
                               {
                                   try
                                   {
                                       return await chain.GetCurrentBlockHeightAsync() >= height
                                           && await chain.GetBlockHashAsync(height)
                                           == await core.GetBlockHashAsync((int)height, c);
                                   }
                                   catch (Exception e) when (e is not OperationCanceledException)
                                   {
                                       return false;
                                   }
                               }, s_followTimeout, TimeSpan.FromMilliseconds(250), $"rbitcoin at Core's {height}", ct);

    /// <summary>Sends <paramref name="transaction"/>, expects a refusal and logs it.</summary>
    private static async Task<RPCException> RefusedAsync(BitcoinChainService chain, Transaction transaction,
                                                         string what)
    {
        var refusal = await Assert.ThrowsAsync<RPCException>(() => chain.SendTransactionAsync(transaction));
        Log($"sendrawtransaction of {what}: {(int)refusal.RPCCode} {refusal.Message}");
        return refusal;
    }

    /// <summary>
    /// A one-input one-output P2WPKH spend of <paramref name="coin"/> at about <paramref name="satPerVByte"/> (110 vB),
    /// signed by <paramref name="signer"/>, opting in to RBF, to a fresh key (or <paramref name="to"/>).
    /// </summary>
    private static Transaction Spend(Coin coin, Key signer, int satPerVByte, Key? to = null)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new TxIn(coin.Outpoint) { Sequence = 0xFFFFFFFD });
        transaction.Outputs.Add(coin.Amount - Money.Satoshis(110 * satPerVByte),
                                (to ?? new Key()).PubKey.WitHash.ScriptPubKey);
        transaction.Sign(signer.GetBitcoinSecret(Network.RegTest), coin);
        return transaction;
    }

    private static bool Contains(Exception e, string text) => e.Message.Contains(text, StringComparison.OrdinalIgnoreCase);
}