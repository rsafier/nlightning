using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using NLightning.Testing.Lnd.Walletrpc;

namespace NLightning.Integration.Tests.Docker;

using Domain.Bitcoin.Wallet.Interfaces;
using Infrastructure.Bitcoin.Wallet.Imports;
using LndGrpc.Macaroons;
using Utils;
using Transaction = NBitcoin.Transaction;

public partial class LndGrpcWave3FlowTests
{
    [Fact]
    public async Task Given_AnImportedTaprootAddress_When_DepositedSpentAndReorged_Then_LiveTransactionsKeepOwnershipAndAmounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        using var ours = LndNodeConnection.CreateWithoutNodeInfo(Settings(LndMacaroonFiles.AdminFileName));
        using var key = new Key();
        var keyPair = key.CreateTaprootKeyPair();
        var script = key.PubKey.GetTaprootFullPubKey().ScriptPubKey;
        var tracker = Node.Services.GetRequiredService<ImportedTapscriptTracker>();
        await tracker.ImportAsync(new ImportedTapscript(script.ToBytes(), key.PubKey.ToBytes()[1..], [1],
            Node.BlockchainMonitor.LastProcessedBlockHeight), ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stream = ours.LightningClient.SubscribeTransactions(new GetTransactionsRequest(), cancellationToken: stop.Token);
        var events = new ConcurrentQueue<Testing.Lnd.Lnrpc.Transaction>();
        var reader = Task.Run(async () =>
        {
            try
            {
                while (await stream.ResponseStream.MoveNext(stop.Token)) events.Enqueue(stream.ResponseStream.Current);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (RpcException e) when (stop.IsCancellationRequested && e.StatusCode == StatusCode.Cancelled) { }
        }, ct);
        try
        {
            await stream.ResponseHeadersAsync.WaitAsync(s_timeout, ct);
            var depositId = await _fixture.Bitcoin.SendToAddressAsync(script.GetDestinationAddress(Network.RegTest)!,
                Money.Satoshis(500_000), cancellationToken: ct);
            await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], _nodes, ct);
            await Poll.UntilAsync(() => Task.FromResult(events.Any(e => e.TxHash == depositId.ToString() && e.NumConfirmations > 0)),
                s_timeout, "imported deposit subscription", ct);
            var deposited = Assert.Single(events, e => e.TxHash == depositId.ToString() && e.NumConfirmations > 0);
            Assert.Equal(500_000, deposited.Amount);
            var depositBlock = await _fixture.Bitcoin.GetBlockAsync(
                await _fixture.Bitcoin.GetBlockHashAsync(deposited.BlockHeight, ct), ct);
            var deposit = Assert.Single(depositBlock.Transactions, t => t.GetHash() == depositId);
            var index = (uint)Array.FindIndex(deposit.Outputs.ToArray(), o => o.ScriptPubKey == script);
            Assert.True(deposited.OutputDetails.Single(o => o.OutputIndex == index).IsOurAddress);

            // A mixed transaction spends an imported input and deposits into the ordinary wallet.
            // Both sources observe it, but the client must receive one merged net amount.
            var ordinary = await ours.WalletKitClient.NextAddrAsync(new AddrRequest(), cancellationToken: ct);
            var spend = Transaction.Create(Network.RegTest);
            spend.Inputs.Add(new TxIn(new NBitcoin.OutPoint(depositId, index)));
            spend.Outputs.Add(Money.Satoshis(499_000), BitcoinAddress.Create(ordinary.Addr, Network.RegTest));
            var signatureHash = spend.GetSignatureHashTaproot([deposit.Outputs[(int)index]], new TaprootExecutionData(0));
            spend.Inputs[0].WitScript = new WitScript(NBitcoin.Op.GetPushOp(keyPair.SignTaprootKeySpend(signatureHash, TaprootSigHash.Default).ToBytes()));
            var spendId = await _fixture.Bitcoin.SendRawTransactionAsync(spend, ct);
            await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], _nodes, ct);
            await Poll.UntilAsync(() => Task.FromResult(events.Any(e => e.TxHash == spendId.ToString() && e.NumConfirmations > 0)),
                s_timeout, "imported mixed spend subscription", ct);
            var spent = Assert.Single(events, e => e.TxHash == spendId.ToString() && e.NumConfirmations > 0);
            Assert.Equal(-1_000, spent.Amount);
            Assert.Equal(1_000, spent.TotalFees);
            Assert.True(Assert.Single(spent.PreviousOutpoints).IsOurOutput);
            Assert.True(Assert.Single(spent.OutputDetails).IsOurAddress);

            await _fixture.Bitcoin.InvalidateBlockAsync(await _fixture.Bitcoin.GetBlockHashAsync(spent.BlockHeight, ct), ct);
            var minerAddress = await _fixture.Bitcoin.GetNewAddressAsync(ct);
            await _fixture.Bitcoin.SendCommandAsync("generateblock", ct, minerAddress.ToString(), Array.Empty<string>());
            await ChainSync.WaitAllAtTipAsync(_fixture, [alice], _nodes, ct, s_timeout);
            await Poll.UntilAsync(() => Task.FromResult(events.Any(e => e.TxHash == spendId.ToString() && e.NumConfirmations == 0)),
                s_timeout, "imported mixed spend unconfirmation", ct);
            await ChainSync.MineAndWaitAsync(_fixture, 1, [alice], _nodes, ct);
            await Poll.UntilAsync(() => Task.FromResult(events.Count(e => e.TxHash == spendId.ToString() && e.NumConfirmations > 0) == 2),
                s_timeout, "imported mixed spend reconfirmation", ct);
            var states = events.Where(e => e.TxHash == spendId.ToString()).SkipWhile(e => e.NumConfirmations == 0).ToArray();
            Assert.Equal([1, 0, 1], states.Select(e => e.NumConfirmations));
            Assert.All(states, e => Assert.Equal(-1_000, e.Amount));
            Assert.NotEqual(states[0].BlockHash, states[2].BlockHash);
            Console.WriteLine("WAVES_IMPORTED_REORG_OK: deposit, merged spend, rewind and reconfirmation");
        }
        finally
        {
            stop.Cancel();
            stream.Dispose();
            await reader.WaitAsync(s_timeout, ct);
        }
    }
}