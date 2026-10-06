using Google.Protobuf;
using Grpc.Core;
using NBitcoin;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Chainrpc;

namespace NLightning.Integration.Tests.Cluster.Live;

using Docker.Utils;
using Fixtures;
using LndGrpc;

public partial class LoopClusterTests
{
    private static async Task AssertDepositReorgAsync(LightningRegtestNetworkFixture fixture, LndNodeConnection alice,
        NLightningTestNode node, LndGrpcHost host, string grpc, uint256 txid, CancellationToken ct)
    {
        using var channel = LndGrpcChannelFactory.Create(LndSettings.FromFiles($"https://127.0.0.1:{host.BoundPort}",
            Path.Combine(grpc, "tls.cert"), Path.Combine(grpc, "admin.macaroon")));
        var wallet = new NLightning.Testing.Lnd.Walletrpc.WalletKit.WalletKitClient(channel);
        var lightning = new NLightning.Testing.Lnd.Lnrpc.Lightning.LightningClient(channel);
        var height = await fixture.Bitcoin.GetBlockCountAsync(ct) - 5;
        async Task AssertDeposit(bool confirmed)
        {
            var unspent = await wallet.ListUnspentAsync(new NLightning.Testing.Lnd.Walletrpc.ListUnspentRequest
            { MinConfs = 1, MaxConfs = int.MaxValue }, cancellationToken: ct);
            Assert.Equal(confirmed, unspent.Utxos.Any(u => u.Outpoint.TxidStr == txid.ToString()));
            var history = await lightning.GetTransactionsAsync(new NLightning.Testing.Lnd.Lnrpc.GetTransactionsRequest
            { StartHeight = height, EndHeight = -1 }, cancellationToken: ct);
            Assert.Equal(confirmed, history.Transactions.Any(t => t.TxHash == txid.ToString()));
        }
        await AssertDeposit(true);
        await fixture.Bitcoin.InvalidateBlockAsync(await fixture.Bitcoin.GetBlockHashAsync(height, ct), ct);
        // Empty replacement blocks omit the returned-to-mempool deposit and trigger the monitor's rewind.
        var minerAddress = await fixture.Bitcoin.GetNewAddressAsync(ct);
        for (var i = 0; i < 6; i++)
            await fixture.Bitcoin.SendCommandAsync("generateblock", ct, minerAddress.ToString(), Array.Empty<string>());
        await ChainSync.WaitAllAtTipAsync(fixture, [alice], [node], ct, s_timeout);
        await AssertDeposit(false);
        await ChainSync.MineAndWaitAsync(fixture, 6, [alice], [node], ct);
        await AssertDeposit(true);
        Log("Live imported deposit reorg removed and reinstated both UTXO and transaction history");
    }

    private static async Task AssertNotifierParityAsync(LightningRegtestNetworkFixture fixture, LndNodeConnection alice,
        LndGrpcHost host, string grpc, uint256 txid, uint height, string address, CancellationToken ct)
    {
        using var channel = LndGrpcChannelFactory.Create(LndSettings.FromFiles($"https://127.0.0.1:{host.BoundPort}",
            Path.Combine(grpc, "tls.cert"), Path.Combine(grpc, "admin.macaroon")));
        var ours = new ChainNotifier.ChainNotifierClient(channel);
        var script = BitcoinAddress.Create(address, Network.RegTest).ScriptPubKey;
        var block = await fixture.Bitcoin.GetBlockAsync(await fixture.Bitcoin.GetBlockHashAsync((int)height, ct), ct);
        var tx = Assert.Single(block.Transactions, t => t.GetHash() == txid);
        var index = (uint)Array.FindIndex(tx.Outputs.ToArray(), o => o.ScriptPubKey == script);
        var confirmation = new ConfRequest
        {
            Txid = ByteString.CopyFrom(txid.ToBytes()),
            Script = ByteString.CopyFrom(script.ToBytes()),
            HeightHint = height,
            NumConfs = 1,
            IncludeBlock = true
        };
        using var lndConf = alice.ChainNotifierClient.RegisterConfirmationsNtfn(confirmation, cancellationToken: ct);
        using var ourConf = ours.RegisterConfirmationsNtfn(confirmation, cancellationToken: ct);
        Assert.Equal(await Next(lndConf, ct), await Next(ourConf, ct));

        var spend = new SpendRequest
        {
            Outpoint = new Outpoint { Hash = confirmation.Txid, Index = index },
            Script = confirmation.Script,
            HeightHint = height
        };
        using var lndSpend = alice.ChainNotifierClient.RegisterSpendNtfn(spend, cancellationToken: ct);
        using var ourSpend = ours.RegisterSpendNtfn(spend, cancellationToken: ct);
        Assert.Equal(await Next(lndSpend, ct), await Next(ourSpend, ct));
        using var lndEpoch = alice.ChainNotifierClient.RegisterBlockEpochNtfn(new BlockEpoch(), cancellationToken: ct);
        using var ourEpoch = ours.RegisterBlockEpochNtfn(new BlockEpoch(), cancellationToken: ct);
        Assert.Equal(await Next(lndEpoch, ct), await Next(ourEpoch, ct));
        Log("LND and NLightning historical confirmation, spend and current epoch RPCs match");
    }

    private static async Task<T> Next<T>(AsyncServerStreamingCall<T> call, CancellationToken ct)
    {
        Assert.True(await call.ResponseStream.MoveNext(ct).WaitAsync(s_timeout, ct));
        return call.ResponseStream.Current;
    }
}