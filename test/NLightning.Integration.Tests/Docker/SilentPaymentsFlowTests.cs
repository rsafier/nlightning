using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json.Linq;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Domain.Accounting.Books;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Client.Requests;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Managers;
using Persistence;
using Testing.Cluster.Images;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes;
using Testing.Cluster.Nodes.BitcoinCore;
using Testing.Cluster.Run;
using Utils;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>Real two-node silent payments, with an independent BIP reference wallet and destructive database restore.</summary>
[Trait("Category", "Cluster")]
public sealed class SilentPaymentsFlowTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    [Theory(Explicit = true)]
    [InlineData("Zmq")]
    [InlineData("Poll")]
    public async Task Given_TwoNodesAndReferenceWallet_When_ReceivingSpendingAndRestoring_Then_ChainAndBooksAgree(string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment($"sp-{mode.ToLowerInvariant()}"), ct);
        var miner = await BitcoinCoreNode.DeployAsync(run, new BitcoinCoreOptions
        {
            Image = ImageVersions.BitcoinCore31,
            Storage = NodeStorage.Ephemeral,
            ExtraArgs = ["-rest"]
        }, TimeSpan.FromMinutes(4), ct);
        await miner.ConnectRpcAsync(RpcRoute.PodIp, ct);
        var core = miner.CreateNBitcoinClient(RpcRoute.PodIp);
        var nodeRpc = miner.CreateNBitcoinClient(RpcRoute.PodIp, string.Empty);
        var mineAddress = await core.GetNewAddressAsync(ct);
        await core.GenerateToAddressAsync(101, mineAddress, ct);
        var birthday = (uint)await nodeRpc.GetBlockCountAsync(ct);
        var endpoint = new RegtestBitcoinEndpoint(core, miner.Handle.PodIp!, BitcoinCorePorts.ZmqRawBlock, BitcoinCorePorts.ZmqRawTx);
        using var keyFiles = new ProofKeyDirectory();
        const string proofPassword = "regtest-only-silent-payment-proof";
        var keyPathA = Path.Combine(keyFiles.Path, "a.json");
        var keyPathB = Path.Combine(keyFiles.Path, "b.json");
        using var keysA = SecureKeyManager.CreateNew(BitcoinNetwork.Regtest, keyPathA, birthday);
        using var keysB = SecureKeyManager.CreateNew(BitcoinNetwork.Regtest, keyPathB, birthday);
        keysA.SaveToFile(proofPassword);
        keysB.SaveToFile(proofPassword);
        await using var a = await NLightningTestNode.CreateAsync(endpoint, "sp-a", secureKeyManager: keysA);
        await using var originalB = await NLightningTestNode.CreateAsync(endpoint, "sp-b", secureKeyManager: keysB);
        var b = originalB;
        foreach (var wallet in new[] { a, b })
        {
            wallet.ChainNotifications = mode;
            wallet.ExtraConfiguration["SilentPayments:Enabled"] = "true";
            wallet.ExtraConfiguration["SilentPayments:Send"] = "true";
            wallet.ExtraConfiguration["SilentPayments:Receive"] = "true";
            wallet.ExtraConfiguration["SilentPayments:BirthdayHeight"] = birthday.ToString(System.Globalization.CultureInfo.InvariantCulture);
            wallet.ExtraConfiguration["Accounting:Profile"] = "Operational";
            await wallet.StartAsync(ct);
        }
        var oracle = await run.DeployAsync(new NodeWorkload("reference-wallet", NodeKind.Other,
            new ImageRef("nltg-spike-spwallet", "bip352-1.1.1", PullPolicy: ImagePullPolicy.Never)), TimeSpan.FromMinutes(2), ct);
        var addresses = b.Services.GetRequiredService<ISilentPaymentService>();
        var unlabeled = await addresses.GetAddressAsync(cancellationToken: ct);
        var labeled = await addresses.GetAddressAsync("independent-trial", ct);
        await a.FundWalletAsync(LightningMoney.Satoshis(5_000_000), Domain.Bitcoin.Enums.AddressType.P2Tr, ct);
        await WaitAtTipAsync(nodeRpc, [a, b], ct);
        var transfer = await a.Services.GetRequiredService<IWalletSpendService>().SendAsync(
            [new WalletRecipient(unlabeled.Address, LightningMoney.Satoshis(1_000_000)),
             new WalletRecipient(labeled.Address, LightningMoney.Satoshis(1_000_000))], cancellationToken: ct);
        Assert.True(transfer.Published);
        await MineAsync(core, nodeRpc, mineAddress, 4, [a, b], ct);
        var firstCoins = SilentCoins(b);
        Assert.Equal(2, firstCoins.Count);
        Assert.Contains(firstCoins, coin => coin.SilentPayment!.Label == labeled.Label);
        Assert.Contains(firstCoins, coin => coin.SilentPayment!.Label is null);
        Assert.Equal(2_000_000L, firstCoins.Sum(coin => coin.Amount.Satoshi));
        await ReconcileAsync(a, ct);
        await ReconcileAsync(b, ct);

        // The reference wallet sends a payment using its own BIP352 functions, on a real P2WPKH input.
        using var referenceInput = new Key(Convert.FromHexString(new string('0', 62) + "2d"));
        var parentId = await core.SendToAddressAsync(referenceInput.PubKey.WitHash.GetAddress(Network.RegTest), Money.Satoshis(600_000), cancellationToken: ct);
        var parent = await nodeRpc.GetRawTransactionAsync(parentId, false, ct);
        var parentIndex = parent.Outputs.FindIndex(output => output.ScriptPubKey == referenceInput.PubKey.WitHash.ScriptPubKey);
        await MineAsync(core, nodeRpc, mineAddress, 1, [a, b], ct);
        var send = await OracleAsync(oracle, new JObject
        {
            ["mode"] = "send",
            ["keys"] = new JArray(new JObject { ["secret"] = Convert.ToHexString(referenceInput.ToBytes()), ["xonly"] = false }),
            ["outpoints"] = new JArray(new JObject { ["txid"] = parentId.ToString(), ["vout"] = parentIndex }),
            ["recipients"] = new JArray(unlabeled.Address)
        }, ct);
        var independentOutput = (string)send["outputs"]![0]!;
        var independentTx = Network.RegTest.CreateTransactionBuilder().AddCoins(new Coin(parent, (uint)parentIndex)).AddKeys(referenceInput)
            .Send(new Script(Convert.FromHexString("5120" + independentOutput)), Money.Satoshis(500_000))
            .SetChange(referenceInput.PubKey.WitHash.GetAddress(Network.RegTest)).SendFees(Money.Satoshis(2_000)).BuildTransaction(true);
        await nodeRpc.SendRawTransactionAsync(independentTx, ct);
        await MineAsync(core, nodeRpc, mineAddress, 4, [a, b], ct);
        Assert.Contains(SilentCoins(b), coin => new uint256((byte[])coin.TxId) == independentTx.GetHash() && coin.Amount.Satoshi == 500_000);

        // Our sender pays the reference wallet, whose unmodified scanner identifies the mined output.
        var referenceAddress = (string)(await OracleAsync(oracle, new JObject { ["mode"] = "address" }, ct))["address"]!;
        var outgoing = await a.Services.GetRequiredService<IWalletSpendService>().SendAsync(
            [new WalletRecipient(referenceAddress, LightningMoney.Satoshis(250_000))], cancellationToken: ct);
        await MineAsync(core, nodeRpc, mineAddress, 4, [a, b], ct);
        var outgoingTx = await nodeRpc.GetRawTransactionAsync(new uint256((byte[])outgoing.TxId), false, ct);
        var inputPoints = new JArray();
        var inputPubKeys = new JArray();
        foreach (var input in outgoingTx.Inputs)
        {
            inputPoints.Add(new JObject { ["txid"] = input.PrevOut.Hash.ToString(), ["vout"] = input.PrevOut.N });
            var previous = await nodeRpc.GetRawTransactionAsync(input.PrevOut.Hash, false, ct);
            var script = previous.Outputs[(int)input.PrevOut.N].ScriptPubKey.ToBytes();
            Assert.True(script.Length == 34 && script[0] == 0x51);
            inputPubKeys.Add("02" + Convert.ToHexString(script[2..]));
        }
        var scanned = await OracleAsync(oracle, new JObject
        {
            ["mode"] = "receive",
            ["outpoints"] = inputPoints,
            ["input_pubkeys"] = inputPubKeys,
            ["outputs"] = new JArray(outgoingTx.Outputs.Where(output => output.ScriptPubKey.ToBytes() is { Length: 34 } script && script[0] == 0x51)
                .Select(output => Convert.ToHexString(output.ScriptPubKey.ToBytes()[2..])))
        }, ct);
        Assert.Single((JArray)scanned["found"]!);
        Log($"SP independent interoperability {mode}: reference -> NLightning and NLightning -> reference verified on chain.");

        // Spend a silent coin, rewind both the payment block and its successor, then reconfirm on a replacement branch.
        var beforeSpend = SilentCoins(b).Select(Outpoint).Order().ToArray();
        var ordinaryAddress = await core.GetNewAddressAsync(ct);
        var spendResult = await b.Services.GetRequiredService<IWalletSpendService>().WithdrawAsync(
            new WalletWithdrawRequest(ordinaryAddress.ToString(), LightningMoney.Satoshis(100_000), null), ct);
        Assert.True(spendResult.Published);
        var spentHeight = (uint)await nodeRpc.GetBlockCountAsync(ct) + 1;
        await MineAsync(core, nodeRpc, mineAddress, 2, [a, b], ct);
        Assert.True(SilentCoins(b).Count < beforeSpend.Length);
        await nodeRpc.InvalidateBlockAsync(await nodeRpc.GetBlockHashAsync((int)spentHeight, ct), ct);
        await WaitAtTipAsync(nodeRpc, [a, b], ct);
        Assert.Equal(beforeSpend, SilentCoins(b).Select(Outpoint).Order().ToArray());
        await ReconcileAsync(b, ct);
        await MineAsync(core, nodeRpc, mineAddress, 5, [a, b], ct);
        var recoveredSet = SilentCoins(b).Select(Outpoint).Order().ToArray();
        await b.StopAsync();
        await b.StartAsync(ct);
        await WaitAtTipAsync(nodeRpc, [a, b], ct);
        Assert.Equal(recoveredSet, SilentCoins(b).Select(Outpoint).Order().ToArray());
        await ReconcileAsync(b, ct);

        // Restore from the encrypted key file and an empty database; labels are recovered by bounded probing.
        await b.StopAsync();
        SqliteTestPools.Clear(b.DatabaseFilePath!);
        b.DeleteFiles();
        using var restoredKeys = SecureKeyManager.FromFilePath(keyPathB, BitcoinNetwork.Regtest, proofPassword);
        await using var restoredB = await NLightningTestNode.CreateAsync(endpoint, "sp-b-restored", secureKeyManager: restoredKeys);
        restoredB.ChainNotifications = mode;
        foreach (var entry in originalB.ExtraConfiguration)
            restoredB.ExtraConfiguration[entry.Key] = entry.Value;
        b = restoredB;
        await b.StartAsync(ct);
        await WaitAtTipAsync(nodeRpc, [a, b], ct);
        var restore = b.Services.GetRequiredService<ISilentPaymentService>();
        await restore.StartRescanAsync(birthday, recoveryLabels: labeled.Label, cancellationToken: ct);
        await ClusterPoll.UntilAsync(async token => !(await restore.GetStatusAsync(token)).IsRescanning,
            s_timeout, TimeSpan.FromMilliseconds(250), "silent payment restore finishes", ct);
        Assert.Equal(recoveredSet, SilentCoins(b).Select(Outpoint).Order().ToArray());
        await ReconcileAsync(b, ct);

        // Fund a real channel using the received silent coins, rather than topping up B with an ordinary deposit.
        var peer = await b.ConnectToAsync(a, ct);
        var channel = await b.OpenChannelAsync(new OpenChannelClientRequest(peer, LightningMoney.Satoshis(500_000))
        {
            FeeRatePerKw = LightningMoney.Satoshis(2_500)
        }, ct);
        await MineAsync(core, nodeRpc, mineAddress, 6, [a, b], ct);
        await ClusterPoll.UntilAsync(async token => (await b.GetChannelAsync(channel.ChannelId, token)).IsUsable(),
            s_timeout, TimeSpan.FromMilliseconds(250), "channel funded from silent coin becomes usable", ct);
        var fundingTx = await nodeRpc.GetRawTransactionAsync(new uint256((byte[])channel.TxId!.Value), false, ct);
        Assert.Contains(fundingTx.Inputs, input => recoveredSet.Contains($"{input.PrevOut.Hash}:{input.PrevOut.N}"));
        await ReconcileAsync(a, ct);
        await ReconcileAsync(b, ct);
        Log($"SP proof {mode}: labeled/unlabeled receive, spend, two-block reorg, restart, empty-database restore, channel funding, and clean accounting.");
    }

    private sealed class ProofKeyDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sp-proof-keys-{Guid.NewGuid():N}");
        public ProofKeyDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static List<UtxoModel> SilentCoins(NLightningTestNode node) => node.Services.GetRequiredService<IUtxoMemoryRepository>()
        .GetUnreservedUtxos().Where(coin => coin.SilentPayment is not null).ToList();

    private static string Outpoint(UtxoModel coin) => $"{new uint256((byte[])coin.TxId)}:{coin.Index}";

    private static async Task<JObject> OracleAsync(KubeNodeHandle oracle, JObject request, CancellationToken ct)
    {
        var result = await oracle.ExecAsync(["python3", "/oracle/oracle.py", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.ToString()))], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Independent reference wallet failed: {result.StdErrText}");
        return JObject.Parse(result.StdOutText);
    }

    private static async Task MineAsync(RPCClient core, RPCClient nodeRpc, BitcoinAddress address, int blocks,
                                       NLightningTestNode[] nodes, CancellationToken ct)
    {
        await core.GenerateToAddressAsync(blocks, address, ct);
        await WaitAtTipAsync(nodeRpc, nodes, ct);
    }

    private static Task WaitAtTipAsync(RPCClient core, NLightningTestNode[] nodes, CancellationToken ct) => ClusterPoll.UntilAsync(async token =>
    {
        var height = (uint)await core.GetBlockCountAsync(token);
        var hash = await core.GetBlockHashAsync((int)height, token);
        foreach (var node in nodes)
        {
            using var scope = node.Services.CreateScope();
            var state = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BlockchainStateDbRepository.GetStateAsync();
            if (state?.LastProcessedHeight != height || new uint256((byte[])state.LastProcessedBlockHash) != hash)
                return false;
        }
        return true;
    }, s_timeout, TimeSpan.FromMilliseconds(250), "both silent payment nodes commit the active chain tip", ct);

    private static async Task ReconcileAsync(NLightningTestNode node, CancellationToken ct)
    {
        var books = node.Services.GetRequiredService<IAccountingBooks>();
        AccountingReconcileResult? last = null;
        await ClusterPoll.UntilAsync(async token => (last = await books.ReconcileAsync(token)).IsClean,
            s_timeout, TimeSpan.FromMilliseconds(250), $"{node.Name} accounting has no drift", ct);
        Assert.True(last!.IsClean);
    }
}