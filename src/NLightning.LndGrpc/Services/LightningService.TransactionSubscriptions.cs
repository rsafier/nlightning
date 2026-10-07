using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.LndGrpc.Services;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Infrastructure.Bitcoin.Networks;
using Infrastructure.Bitcoin.Wallet.Imports;
using Lnrpc;
using Transaction = Lnrpc.Transaction;

public sealed partial class LightningService
{
    /// <summary>Live wallet discoveries and committed confirmations/unconfirmations. No initial history replay.</summary>
    public override async Task SubscribeTransactions(GetTransactionsRequest request,
        IServerStreamWriter<Transaction> responseStream, ServerCallContext context)
    {
        if (request.Account.Length > 0 && request.Account != "default")
            throw NotFound($"account {request.Account} not found");
        if (_blockchainMonitor is null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "wallet chain monitor unavailable"));
        using var queue = new LiveEventQueue<WalletTransactionEventArgs>();
        void Observed(object? sender, WalletTransactionEventArgs observed) =>
            queue.Publish(observed);
        void Failed(Exception _) => queue.Fail(new RpcException(new Status(StatusCode.FailedPrecondition,
            "transaction subscription history unavailable; reconcile GetTransactions and reconnect")));
        await using var scope = CreateScope();
        var imported = scope.ServiceProvider.GetService<ImportedTapscriptTracker>();
        if (imported is not null)
        {
            // Initialize the incremental checkpoint before joining the live feed; historical rows are not replayed.
            imported.OnObservationFailed += Failed;
            try { await imported.SubscribeAsync(Observed, context.CancellationToken); }
            catch (Exception e)
            {
                imported.OnObservationFailed -= Failed;
                if (e is not InvalidOperationException) throw;
                throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
            }
        }
        else
            _blockchainMonitor.OnWalletTransactionObserved += Observed;
        try
        {
            await context.WriteResponseHeadersAsync(new Metadata());
            await queue.WriteToAsync(responseStream, observed => ValueTask.FromResult(ToTransactionEvent(observed)),
                                     context.CancellationToken);
        }
        finally
        {
            if (imported is not null)
            {
                imported.OnTransactionObserved -= Observed;
                imported.OnObservationFailed -= Failed;
            }
            else _blockchainMonitor.OnWalletTransactionObserved -= Observed;
        }
    }

    internal Transaction ToTransactionEvent(WalletTransactionEventArgs observed)
    {
        var network = _nodeOptions.BitcoinNetwork.ToNBitcoinNetwork();
        var transaction = NBitcoin.Transaction.Parse(observed.RawTransactionHex, network);
        var result = new Transaction
        {
            TxHash = transaction.GetHash().ToString(),
            Amount = observed.AmountSat,
            TotalFees = observed.FeeSat,
            RawTxHex = observed.RawTransactionHex,
            BlockHash = observed.BlockHash,
            BlockHeight = (int)observed.BlockHeight,
            // This immutable event describes the first committed confirmation, even if the node has
            // rewound by the time a slow stream consumer dequeues it.
            NumConfirmations = observed.BlockHeight > 0 ? 1 : 0,
            TimeStamp = observed.Timestamp.ToUnixTimeSeconds(),
            Label = observed.Label
        };
        for (var i = 0; i < transaction.Outputs.Count; i++)
        {
            var output = transaction.Outputs[i];
            result.OutputDetails.Add(new OutputDetail
            {
                OutputType = ScriptTypeOf(output.ScriptPubKey),
                Address = output.ScriptPubKey.GetDestinationAddress(network)?.ToString() ?? "",
                PkScript = output.ScriptPubKey.ToHex(),
                OutputIndex = i,
                Amount = output.Value.Satoshi,
                IsOurAddress = observed.OurOutputs.Contains((uint)i)
            });
        }
        for (var i = 0; i < transaction.Inputs.Count; i++)
        {
            var input = transaction.Inputs[i];
            result.PreviousOutpoints.Add(new PreviousOutPoint
            {
                Outpoint = $"{new TxId(input.PrevOut.Hash.ToBytes())}:{input.PrevOut.N}",
                IsOurOutput = observed.OurInputs.Contains((uint)i)
            });
        }
        return result;
    }
}