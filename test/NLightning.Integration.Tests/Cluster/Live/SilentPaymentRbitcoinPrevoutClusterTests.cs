using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Cluster.Live;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Options;
using Infrastructure.Bitcoin.Wallet.SilentPayments;
using Testing.Cluster.Images;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes.BitcoinCore;
using Testing.Cluster.Nodes.Rbitcoin;
using Testing.Cluster.Run;

/// <summary>Optional explicit backend contract, requiring the existing pinned rbitcoin image to be built first.</summary>
[Trait("Category", "Cluster")]
public sealed class SilentPaymentRbitcoinPrevoutClusterTests
{
    [Fact(Explicit = true)]
    public async Task Given_RbitcoinFollowingCore_When_AutoLoadsPrevouts_Then_RawLookupMatchesCoreUndoData()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("sp-rbitcoin"), ct);
        var miner = await BitcoinCoreNode.DeployAsync(run, new BitcoinCoreOptions
        {
            Image = ImageVersions.BitcoinCore31,
            Storage = NodeStorage.Ephemeral
        }, TimeSpan.FromMinutes(4), ct);
        await miner.ConnectRpcAsync(RpcRoute.PodIp, ct);
        var core = miner.CreateNBitcoinClient(RpcRoute.PodIp);
        var coreNode = miner.CreateNBitcoinClient(RpcRoute.PodIp, string.Empty);
        var mineAddress = await core.GetNewAddressAsync(ct);
        await core.GenerateToAddressAsync(101, mineAddress, ct);
        using var key = new Key();
        await core.SendToAddressAsync(key.CreateTaprootKeyPair().PubKey.ScriptPubKey.GetDestinationAddress(Network.RegTest)!,
            Money.Satoshis(100_000), cancellationToken: ct);
        var blockHash = (await core.GenerateToAddressAsync(1, mineAddress, ct)).Single();
        var block = await coreNode.GetBlockAsync(blockHash, ct);
        var rbitcoin = await RbitcoinNode.DeployAsync(run, new RbitcoinNodeOptions
        {
            Connect = $"{miner.Handle.PodIp}:{BitcoinCorePorts.P2p}"
        }, TimeSpan.FromMinutes(4), ct);
        var rpc = new NBitcoin.RPC.RPCClient(new NBitcoin.RPC.RPCCredentialString
        {
            UserPassword = new System.Net.NetworkCredential(rbitcoin.Options.RpcUser, rbitcoin.Options.RpcPassword)
        }, rbitcoin.RpcUrl, Network.RegTest);
        await Testing.Cluster.Poll.UntilAsync(async token => await rpc.GetBestBlockHashAsync(token) == blockHash,
            TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(250), "rbitcoin reaches silent payment candidate block", ct);
        var domain = new BitcoinBlock(block.ToBytes(), new Hash(blockHash.ToBytes()), block.Transactions.Count);
        var options = new OptionsWrapper<NodeOptions>(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest });
        var fallback = new BlockPrevoutSource(new OptionsWrapper<BitcoinOptions>(new BitcoinOptions
        {
            RpcEndpoint = rbitcoin.RpcUrl, RpcUser = rbitcoin.Options.RpcUser, RpcPassword = rbitcoin.Options.RpcPassword
        }), options);
        var undo = new BlockPrevoutSource(new OptionsWrapper<BitcoinOptions>(new BitcoinOptions
        {
            RpcEndpoint = $"http://{miner.Handle.PodIp}:{BitcoinCorePorts.Rpc}", RpcUser = miner.Options.RpcUser, RpcPassword = miner.Options.RpcPassword
        }), options, SilentPaymentPrevoutSource.GetBlock);
        var actual = await fallback.GetPrevoutsAsync(domain, 102, ct);
        var expected = await undo.GetPrevoutsAsync(domain, 102, ct);
        Assert.Equal(SilentPaymentPrevoutSource.GetRawTransaction, fallback.Source);
        Assert.NotEmpty(expected);
        foreach (var entry in expected)
            Assert.Equal(entry.Value, actual[entry.Key]);
        TestContext.Current.TestOutputHelper?.WriteLine("SOURCE rbitcoin Auto: confirmed getrawtransaction fallback matches Core undo data without txindex.");
    }
}