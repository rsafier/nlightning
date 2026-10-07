using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Cluster.Live;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Options;
using Infrastructure.Bitcoin.Taproot;
using Infrastructure.Bitcoin.Wallet.SilentPayments;
using Testing.Cluster.Images;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes.BitcoinCore;
using Testing.Cluster.Run;

/// <summary>Explicit Core 31.1 undo-data contract and byte capture. Run only through scripts/run-cluster.sh.</summary>
[Trait("Category", "Cluster")]
public sealed class SilentPaymentPrevoutClusterTests
{
    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    [Fact(Explicit = true)]
    public async Task Given_AllSixSpentOutputTypes_When_CorePrevoutSourcesReadThem_Then_AllSourcesAgree()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("sp-prevouts"), ct);
        var miner = await BitcoinCoreNode.DeployAsync(run, new BitcoinCoreOptions
        {
            Image = ImageVersions.BitcoinCore31,
            Storage = NodeStorage.Ephemeral,
            ExtraArgs = ["-rest"]
        }, TimeSpan.FromMinutes(4), ct);
        await miner.ConnectRpcAsync(RpcRoute.PodIp, ct);
        var core = miner.CreateNBitcoinClient(RpcRoute.PodIp);
        var node = miner.CreateNBitcoinClient(RpcRoute.PodIp, string.Empty);
        var address = await core.GetNewAddressAsync(ct);
        await core.GenerateToAddressAsync(101, address, ct);

        using var key = new Key();
        using var destination = new Key();
        var destinationPair = destination.CreateTaprootKeyPair();
        var leafScript = new Script(OpcodeType.OP_TRUE);
        var tree = TapscriptTree.Create(key.PubKey, leafScript);
        var witnessScript = new Script(Op.GetPushOp(key.PubKey.ToBytes()), OpcodeType.OP_CHECKSIG);
        var scripts = new[]
        {
            key.PubKey.Hash.ScriptPubKey,
            key.PubKey.WitHash.ScriptPubKey.Hash.ScriptPubKey,
            key.PubKey.WitHash.ScriptPubKey,
            key.CreateTaprootKeyPair().PubKey.ScriptPubKey,
            tree.ScriptPubKey,
            witnessScript.WitHash.ScriptPubKey
        };
        var funding = new List<Transaction>();
        foreach (var script in scripts)
        {
            var id = await core.SendToAddressAsync(script.GetDestinationAddress(Network.RegTest)!, Money.Satoshis(200_000), cancellationToken: ct);
            funding.Add(await node.GetRawTransactionAsync(id, false, ct));
        }
        await core.GenerateToAddressAsync(1, address, ct);
        var spends = new List<Transaction>();
        for (var i = 0; i < scripts.Length; i++)
        {
            var index = funding[i].Outputs.FindIndex(output => output.ScriptPubKey == scripts[i]);
            var coin = new Coin(funding[i], (uint)index);
            Transaction spend;
            if (i is 3 or 4)
            {
                spend = Network.RegTest.CreateTransaction();
                spend.Inputs.Add(new TxIn(coin.Outpoint));
                spend.Outputs.Add(Money.Satoshis(198_000), destinationPair.PubKey.ScriptPubKey);
                if (i == 3)
                {
                    var hash = spend.GetSignatureHashTaproot([coin.TxOut], new TaprootExecutionData(0));
                    var signature = key.CreateTaprootKeyPair().SignTaprootKeySpend(hash, TaprootSigHash.Default);
                    spend.Inputs[0].WitScript = new WitScript(Op.GetPushOp(signature.ToBytes()));
                }
                else
                    spend.Inputs[0].WitScript = new WitScript(new[] { leafScript.ToBytes(), tree.GetControlBlock(tree.Leaves[0]).ToBytes() });
            }
            else
            {
                ICoin signingCoin = i switch
                {
                    1 => coin.ToScriptCoin(key.PubKey.WitHash.ScriptPubKey),
                    5 => coin.ToScriptCoin(witnessScript),
                    _ => coin
                };
                spend = Network.RegTest.CreateTransactionBuilder().AddCoins(signingCoin).AddKeys(key)
                    .Send(destinationPair.PubKey.ScriptPubKey, Money.Satoshis(198_000))
                    .SendFees(Money.Satoshis(2_000)).BuildTransaction(true);
            }
            await node.SendRawTransactionAsync(spend, ct);
            spends.Add(spend);
        }
        // A seventh candidate spends an earlier candidate in the same block.
        var parentCoin = new Coin(spends[0], 0);
        var child = Network.RegTest.CreateTransaction();
        child.Inputs.Add(new TxIn(parentCoin.Outpoint));
        child.Outputs.Add(Money.Satoshis(196_000), destinationPair.PubKey.ScriptPubKey);
        var childHash = child.GetSignatureHashTaproot([parentCoin.TxOut], new TaprootExecutionData(0));
        child.Inputs[0].WitScript = new WitScript(Op.GetPushOp(destinationPair.SignTaprootKeySpend(childHash, TaprootSigHash.Default).ToBytes()));
        await node.SendRawTransactionAsync(child, ct);
        var blockHash = (await core.GenerateToAddressAsync(1, address, ct)).Single();
        var block = await node.GetBlockAsync(blockHash, ct);
        var height = (uint)await node.GetBlockCountAsync(ct);
        var domain = new BitcoinBlock(block.ToBytes(), new Hash(blockHash.ToBytes()), block.Transactions.Count);
        IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>>? expected = null;
        foreach (var kind in new[] { SilentPaymentPrevoutSource.GetBlock, SilentPaymentPrevoutSource.Rest, SilentPaymentPrevoutSource.GetRawTransaction })
        {
            var source = CreateSource(miner, kind);
            var actual = await source.GetPrevoutsAsync(domain, height, ct);
            Assert.Equal(7, actual.Count);
            if (expected is not null)
                foreach (var entry in expected)
                    Assert.Equal(entry.Value, actual[entry.Key]);
            expected = actual;
            Log($"SOURCE {kind}: {actual.Count} candidate transactions; every input identical.");
        }
        // REST and getblock 3 read undo data, independently of txindex.
        var follower = await BitcoinCoreNode.DeployAsync(run, new BitcoinCoreOptions
        {
            Name = "no-index",
            Image = ImageVersions.BitcoinCore31,
            Storage = NodeStorage.Ephemeral,
            TxIndex = false,
            Wallet = null,
            AddNodes = [$"{miner.Handle.PodIp}:{BitcoinCorePorts.P2p}"],
            ExtraArgs = ["-rest"]
        }, TimeSpan.FromMinutes(4), ct);
        await follower.ConnectRpcAsync(RpcRoute.PodIp, ct);
        var followerRpc = follower.CreateNBitcoinClient(RpcRoute.PodIp, string.Empty);
        await Testing.Cluster.Poll.UntilAsync(async token => await followerRpc.GetBlockCountAsync(token) >= height,
            TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(250), "non-txindex follower reaches source block", ct);
        foreach (var kind in new[] { SilentPaymentPrevoutSource.GetBlock, SilentPaymentPrevoutSource.Rest })
        {
            var actual = await CreateSource(follower, kind).GetPrevoutsAsync(domain, height, ct);
            foreach (var entry in expected!)
                Assert.Equal(entry.Value, actual[entry.Key]);
            Log($"SOURCE {kind} without txindex: every input identical.");
        }
        var verbose = await node.SendCommandAsync(ct, "getblock", blockHash.ToString(), 3);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(node.Address, $"/rest/spenttxouts/{blockHash}.bin"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{miner.Options.RpcUser}:{miner.Options.RpcPassword}")));
        using var response = await node.HttpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        Log($"CAPTURE core31-block {block.ToHex()}");
        Log($"CAPTURE core31-getblock3 {Convert.ToBase64String(Encoding.UTF8.GetBytes(new Newtonsoft.Json.Linq.JObject { ["result"] = verbose.Result, ["error"] = null }.ToString()))}");
        Log($"CAPTURE core31-rest {Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync(ct))}");
    }

    [Fact(Explicit = true)]
    public async Task Given_PrunedCore_When_RecoveryStartsBelowPruneFloor_Then_ItRefusesBeforeScanning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("sp-pruned"), ct);
        var miner = await BitcoinCoreNode.DeployAsync(run, new BitcoinCoreOptions
        {
            Image = ImageVersions.BitcoinCore31,
            Storage = NodeStorage.Ephemeral,
            TxIndex = false,
            // Core's regtest-only fast-prune option uses small block files, so this needs no 550 MB fixture.
            ExtraArgs = ["-rest", "-prune=550", "-fastprune=1"]
        }, TimeSpan.FromMinutes(4), ct);
        await miner.ConnectRpcAsync(RpcRoute.PodIp, ct);
        var core = miner.CreateNBitcoinClient(RpcRoute.PodIp);
        var node = miner.CreateNBitcoinClient(RpcRoute.PodIp, string.Empty);
        var address = await core.GetNewAddressAsync(ct);
        await core.GenerateToAddressAsync(1_001, address, ct);
        var oldHash = await node.GetBlockHashAsync(10, ct);
        await node.SendCommandAsync(ct, "pruneblockchain", 700);
        var info = (await node.SendCommandAsync(ct, "getblockchaininfo")).Result;
        Assert.True((bool)info["pruned"]!);
        Assert.True((uint)info["pruneheight"]! > 10);
        var unavailable = await Assert.ThrowsAsync<NBitcoin.RPC.RPCException>(() => node.GetBlockAsync(oldHash, ct));
        Assert.Contains("pruned", unavailable.Message, StringComparison.OrdinalIgnoreCase);
        var source = CreateSource(miner, SilentPaymentPrevoutSource.Auto);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => source.ValidateHeightAsync(10, ct));
        Assert.Contains("pruned", refusal.Message);
        Log($"SOURCE pruned refusal: pruneheight {info["pruneheight"]}, old block 10 rejected before recovery writes.");
    }

    private static BlockPrevoutSource CreateSource(BitcoinCoreNode node, SilentPaymentPrevoutSource kind) => new(
        new OptionsWrapper<BitcoinOptions>(new BitcoinOptions
        {
            RpcEndpoint = $"http://{node.Handle.PodIp}:{BitcoinCorePorts.Rpc}",
            RpcUser = node.Options.RpcUser,
            RpcPassword = node.Options.RpcPassword
        }),
        new OptionsWrapper<NodeOptions>(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }), kind);
}