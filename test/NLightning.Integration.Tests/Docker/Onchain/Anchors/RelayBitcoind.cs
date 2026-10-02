using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json.Linq;
using NLightning.Testing.Lnd;
using Network = NBitcoin.Network;

namespace NLightning.Integration.Tests.Docker.Onchain.Anchors;

using Fixtures;
using Utils;

/// <summary>
/// A second bitcoind for the package relay proof (NL-380): it runs the miner's image on the miner's Docker network,
/// syncs from the miner (<c>-connect</c>, its only peer) and relays to it, but keeps a mempool of only
/// <see cref="MaxMempoolMb"/> MB. <see cref="FillMempoolAsync"/> fills that mempool with large transactions until
/// bitcoind trims it, which raises its dynamic minimum feerate (<c>mempoolminfee</c>) above a low commitment's feerate
/// while <c>minrelaytxfee</c> stays at 1 sat/vB: exactly the "fee spike after the last update_fee" case of BOLT 5
/// B5-FAIL-06, where the commitment alone is refused and only a parent+child package (<c>submitpackage</c>, Bitcoin
/// Core 28+) gets it in.
/// </summary>
/// <remarks>
/// <para>Why not raise <c>-minrelaytxfee</c>: since Bitcoin Core 28 a non-TRUC (version 2) transaction must pay
/// <c>minrelaytxfee</c> on its own even inside a package (<c>validation.cpp</c> PreChecks, "min relay fee not met"), and
/// Lightning commitments are version 2, so a commitment below <c>minrelaytxfee</c> can never enter a mempool. Only the
/// dynamic minimum (a full mempool) is bypassed by package feerates. Bitcoin Core 29 has no static
/// <c>-mempoolminfee</c> option, hence the fill.</para>
/// <para>The shared miner keeps its 300 MB mempool: its minimum stays at 1 sat/vB, so the fill transactions it gets
/// from the relay are mined over the next blocks (<see cref="DisposeAsync"/> mines them out) and never change what the
/// other proofs see.</para>
/// <para>RPC and the ZMQ raw block/tx feeds are published on <c>127.0.0.1</c> (as <c>ClnFixture</c> does), so the
/// in-process node reaches them from the host and from the <c>--network host</c> runner container.</para>
/// </remarks>
internal sealed class RelayBitcoind : IAsyncDisposable
{
    public const string ContainerName = "nltg-anchors-relay-bitcoind";

    /// <summary>The smallest <c>-maxmempool</c> bitcoind accepts (the default of <c>-blocksonly</c>).</summary>
    public const int MaxMempoolMb = 5;

    private const string RpcUser = "nltg";
    private const string RpcPassword = "nltg";
    private const int RpcPort = 18443;
    private const int ZmqBlockPort = 28334;
    private const int ZmqTxPort = 28335;
    private const int MinerP2PPort = 18444;

    /// <summary>Outputs per fill transaction: about 93 kvB, below the 100 kvB standardness limit.</summary>
    private const int FillOutputs = 3_000;

    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);
    private static readonly Money s_fillFunding = Money.Coins(0.02m);

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly List<uint256> _fillTxIds = [];

    private RegtestBitcoinEndpoint? _endpoint;

    private RelayBitcoind(LightningRegtestNetworkFixture fixture)
    {
        _fixture = fixture;
    }

    public RegtestBitcoinEndpoint Endpoint =>
        _endpoint ?? throw new InvalidOperationException("The relay bitcoind is not running");

    public RPCClient Rpc => Endpoint.Rpc;

    /// <summary>
    /// Starts the relay on the miner's network, waits until it has the miner's tip, and gives its wallet
    /// <paramref name="walletFunding"/> from the miner (so <see cref="NLightningTestNode.FundWalletAsync"/> and the
    /// node's funding mining work through it).
    /// </summary>
    public static async Task<RelayBitcoind> StartAsync(LightningRegtestNetworkFixture fixture, Money walletFunding,
                                                       CancellationToken ct)
    {
        var relay = new RelayBitcoind(fixture);
        try
        {
            await relay.StartContainerAsync(ct);
            await relay.Rpc.SendCommandAsync("createwallet", ct, "relay");
            var address = await relay.Rpc.GetNewAddressAsync(ct);
            await fixture.Bitcoin.SendToAddressAsync(address, walletFunding, cancellationToken: ct);
            await fixture.Bitcoin.GenerateToAddressAsync(1, await fixture.Bitcoin.GetNewAddressAsync(ct), ct);
            await relay.WaitSyncedAsync(ct);
            await Poll.UntilAsync(async () => await relay.Rpc.GetBalanceAsync(0, false) >= walletFunding,
                                  s_readyTimeout, "the relay wallet funded", ct);
            return relay;
        }
        catch
        {
            await relay.DisposeAsync();
            throw;
        }
    }

    /// <summary>Waits until the relay's tip is the miner's.</summary>
    public async Task WaitSyncedAsync(CancellationToken ct) =>
        await Poll.UntilAsync(async () => await Rpc.GetBestBlockHashAsync(ct)
                                       == await _fixture.Bitcoin.GetBestBlockHashAsync(ct),
                              s_readyTimeout, "the relay bitcoind at the miner's tip", ct);

    /// <summary>The relay's current dynamic minimum mempool feerate (<c>getmempoolinfo.mempoolminfee</c>) in sat/vB.</summary>
    public async Task<decimal> GetMempoolMinFeeSatPerVByteAsync(CancellationToken ct) =>
        BtcPerKvbToSatPerVByte((await GetMempoolInfoAsync(ct))["mempoolminfee"]!);

    /// <summary>The relay's static <c>minrelaytxfee</c> in sat/vB.</summary>
    public async Task<decimal> GetMinRelayFeeSatPerVByteAsync(CancellationToken ct) =>
        BtcPerKvbToSatPerVByte((await GetMempoolInfoAsync(ct))["minrelaytxfee"]!);

    public async Task<bool> IsInMempoolAsync(uint256 txId, CancellationToken ct) =>
        (await Rpc.GetRawMempoolAsync(ct)).Contains(txId);

    /// <summary>
    /// The relay's mempool transaction that spends <paramref name="outPoint"/>, or null.
    /// </summary>
    public async Task<Transaction?> FindMempoolSpenderAsync(OutPoint outPoint, CancellationToken ct)
    {
        foreach (var txId in await Rpc.GetRawMempoolAsync(ct))
        {
            if (_fillTxIds.Contains(txId))
                continue;

            Transaction tx;
            try
            {
                tx = await Rpc.GetRawTransactionAsync(txId, true, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue; // evicted or mined meanwhile
            }

            if (tx.Inputs.Any(i => i.PrevOut == outPoint))
                return tx;
        }

        return null;
    }

    /// <summary>
    /// Sends fill transactions of <paramref name="fillRateSatPerVByte"/> (about 93 kvB each, one wallet-free coin
    /// from the miner each) to the relay until its <c>mempoolminfee</c> exceeds <paramref name="targetSatPerVByte"/>;
    /// returns the minimum reached. Mines one block (the fill coins), so call it after the channel is open.
    /// </summary>
    /// <exception cref="InvalidOperationException">The minimum did not rise within <paramref name="maxFills"/>.</exception>
    public async Task<decimal> FillMempoolAsync(decimal fillRateSatPerVByte, decimal targetSatPerVByte,
                                                int maxFills, IEnumerable<LndNodeConnection> lndNodes,
                                                IEnumerable<NLightningTestNode> nodes, CancellationToken ct)
    {
        // One confirmed coin per fill transaction, from the miner's wallet to keys of this test
        var coins = new List<(Key Key, Coin Coin)>();
        var fundings = new List<(Key Key, uint256 TxId)>();
        for (var i = 0; i < maxFills; i++)
        {
            var key = new Key();
            var address = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
            fundings.Add((key, await _fixture.Bitcoin.SendToAddressAsync(address, s_fillFunding,
                                                                          cancellationToken: ct)));
        }

        await ChainSync.MineAndWaitAsync(_fixture, 1, lndNodes, nodes, ct);
        await WaitSyncedAsync(ct);
        foreach (var (key, txId) in fundings)
        {
            var funding = await _fixture.Bitcoin.GetRawTransactionAsync(txId, true, ct);
            var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit);
            var vout = funding.Outputs.FindIndex(o => o.ScriptPubKey == script);
            coins.Add((key, new Coin(funding, (uint)vout)));
        }

        var minFee = await GetMempoolMinFeeSatPerVByteAsync(ct);
        var sent = 0;
        foreach (var (key, coin) in coins)
        {
            if (minFee > targetSatPerVByte)
                break;

            var fill = BuildFill(key, coin, fillRateSatPerVByte);
            try
            {
                await Rpc.SendRawTransactionAsync(fill, ct);
                _fillTxIds.Add(fill.GetHash());
                sent++;
            }
            catch (RPCException e)
            {
                // The one that made bitcoind trim may itself be evicted ("mempool full")
                Console.WriteLine($"Relay refused fill {sent + 1}: {e.Message}");
                _fillTxIds.Add(fill.GetHash());
            }

            minFee = await GetMempoolMinFeeSatPerVByteAsync(ct);
        }

        var info = await GetMempoolInfoAsync(ct);
        Console.WriteLine($"Relay mempool after {sent} fills of {fillRateSatPerVByte} sat/vB: {info["size"]} txs, "
                        + $"{info["usage"]} bytes of {info["maxmempool"]}, mempoolminfee {minFee} sat/vB");
        return minFee > targetSatPerVByte
                   ? minFee
                   : throw new InvalidOperationException(
                         $"The relay's mempool minimum stayed at {minFee} sat/vB after {sent} fills");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (DockerDiagnostics.CurrentTestFailed)
                await DockerDiagnostics.DumpContainerLogsAsync([ContainerName]);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Could not dump the relay's log: {e.Message}");
        }

        await DockerContainerUtils.RemoveContainerAsync(_client, ContainerName);
        await MineOutFillsAsync();
        _client.Dispose();
    }

    private static decimal BtcPerKvbToSatPerVByte(JToken value) =>
        decimal.Parse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture) * 100_000m;

    private static Transaction BuildFill(Key key, Coin coin, decimal rateSatPerVByte)
    {
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit);
        var perOutput = Money.Satoshis(1_000);
        for (var pass = 0; pass < 2; pass++)
        {
            var tx = Network.RegTest.CreateTransaction();
            tx.Inputs.Add(new TxIn(coin.Outpoint));
            for (var i = 0; i < FillOutputs; i++)
                tx.Outputs.Add(new TxOut(perOutput, script));
            tx.Sign(key.GetBitcoinSecret(Network.RegTest), coin);
            if (pass == 1)
                return tx;

            // The size does not depend on the output values: fix them so the fee pays the rate
            var fee = (long)Math.Ceiling(rateSatPerVByte * tx.GetVirtualSize());
            perOutput = Money.Satoshis((coin.Amount.Satoshi - fee) / FillOutputs);
        }

        throw new InvalidOperationException("unreachable");
    }

    private async Task<JObject> GetMempoolInfoAsync(CancellationToken ct) =>
        (JObject)(await Rpc.SendCommandAsync("getmempoolinfo", ct)).Result;

    /// <summary>Mines on the miner until none of the fill transactions is left in its mempool (at most 10 blocks).</summary>
    private async Task MineOutFillsAsync()
    {
        if (_fillTxIds.Count == 0)
            return;

        try
        {
            for (var i = 0; i < 10; i++)
            {
                var mempool = await _fixture.Bitcoin.GetRawMempoolAsync();
                if (!mempool.Any(_fillTxIds.Contains))
                    return;

                await _fixture.Bitcoin.GenerateToAddressAsync(1, await _fixture.Bitcoin.GetNewAddressAsync());
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Could not mine the fill transactions out: {e.Message}");
        }
    }

    private async Task StartContainerAsync(CancellationToken ct)
    {
        // The miner's image and network, and its address there (LNUnit names the container "miner")
        var miner = await _client.Containers.InspectContainerAsync("miner", ct);
        var (networkName, minerEndpoint) = miner.NetworkSettings.Networks.First(n => !string.IsNullOrEmpty(
                                                                                         n.Value.IPAddress));
        Console.WriteLine($"Relay bitcoind: image {miner.Config.Image}, network {networkName}, miner at "
                        + $"{minerEndpoint.IPAddress}:{MinerP2PPort}");

        await DockerContainerUtils.RemoveContainerAsync(_client, ContainerName);
        int[] containerPorts = [RpcPort, ZmqBlockPort, ZmqTxPort];
        var container = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = miner.Config.Image,
            Name = ContainerName,
            Hostname = ContainerName,
            Cmd =
            [
                "bitcoind", "-regtest", "-server=1",
                $"-rpcuser={RpcUser}", $"-rpcpassword={RpcPassword}",
                "-rpcbind=0.0.0.0", "-rpcallowip=0.0.0.0/0", $"-rpcport={RpcPort}", "-rpcworkqueue=1024",
                $"-zmqpubrawblock=tcp://0.0.0.0:{ZmqBlockPort}", $"-zmqpubrawtx=tcp://0.0.0.0:{ZmqTxPort}",
                "-txindex=1", "-fallbackfee=0.0002", "-dnsseed=0", "-listen=0",
                $"-connect={minerEndpoint.IPAddress}:{MinerP2PPort}",
                $"-maxmempool={MaxMempoolMb}", "-printtoconsole"
            ],
            ExposedPorts = containerPorts.ToDictionary(p => $"{p}/tcp", _ => default(EmptyStruct)),
            HostConfig = new HostConfig
            {
                NetworkMode = networkName,
                PortBindings = containerPorts.ToDictionary(
                    p => $"{p}/tcp",
                    IList<PortBinding> (_) => [new PortBinding { HostIP = "127.0.0.1", HostPort = string.Empty }])
            }
        }, ct) ?? throw new InvalidOperationException($"Failed to create the {ContainerName} container");
        await _client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), ct);

        var hostPorts = await Poll.ForAsync(async () =>
        {
            var inspect = await _client.Containers.InspectContainerAsync(container.ID, ct);
            var published = new Dictionary<int, int>();
            foreach (var port in containerPorts)
            {
                if (inspect.NetworkSettings?.Ports is { } ports
                 && ports.TryGetValue($"{port}/tcp", out var bindings)
                 && bindings is { Count: > 0 }
                 && int.TryParse(bindings[0].HostPort, out var hostPort)
                 && hostPort > 0)
                    published[port] = hostPort;
            }

            return published.Count == containerPorts.Length ? published : null;
        }, s_readyTimeout, $"the ports of {ContainerName} published", ct);

        var rpc = new RPCClient($"{RpcUser}:{RpcPassword}", $"http://127.0.0.1:{hostPorts[RpcPort]}",
                                Network.RegTest);
        await DockerContainerUtils.WaitUntilReadyAsync(ContainerName,
                                                       async token => await rpc.GetBlockCountAsync(token),
                                                       s_readyTimeout);
        _endpoint = new RegtestBitcoinEndpoint(rpc, "127.0.0.1", hostPorts[ZmqBlockPort], hostPorts[ZmqTxPort]);
        await WaitSyncedAsync(ct);
    }
}