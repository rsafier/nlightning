using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Onchain.Models;

public partial class BlockchainMonitorService
{
    public event EventHandler<WalletTransactionEventArgs>? OnWalletTransactionObserved;
    public event EventHandler<NewBlockEventArgs>? OnWalletTransactionsProcessed;
    public event EventHandler? OnWalletTransactionsProcessing;

    private readonly Lock _walletObservationLock = new();
    private readonly HashSet<uint256> _unconfirmedWalletTransactions = [];
    private readonly Queue<uint256> _unconfirmedWalletOrder = new();
    private readonly Dictionary<string, WalletTransactionEventArgs> _confirmedWalletObservations = new(StringComparer.Ordinal);
    private readonly Queue<(string Hash, uint Height)> _confirmedWalletOrder = new();

    private void ObserveUnconfirmedWalletTransaction(Transaction transaction, BroadcastTransactionModel? row = null)
    {
        if (OnWalletTransactionObserved is null)
            return;
        var observed = DescribeWalletTransaction(transaction, 0, "", row: row);
        if (observed is null)
            return;
        lock (_walletObservationLock)
        {
            if (!Remember(_unconfirmedWalletTransactions, _unconfirmedWalletOrder, transaction.GetHash()))
                return;
        }
        RaiseWalletTransaction(observed);
    }

    private void RaiseWalletTransaction(WalletTransactionEventArgs observed)
    {
        if (observed.BlockHeight > 0)
        {
            lock (_walletObservationLock)
            {
                var hash = observed.TxHash;
                _confirmedWalletObservations[hash] = observed;
                _confirmedWalletOrder.Enqueue((hash, observed.BlockHeight));
                while (_confirmedWalletOrder.TryPeek(out var oldest) &&
                       (_confirmedWalletOrder.Count > MaxRememberedMempoolTransactions ||
                        (ulong)oldest.Height + (uint)HeaderRingSize < observed.BlockHeight))
                {
                    _confirmedWalletOrder.Dequeue();
                    if (_confirmedWalletObservations.TryGetValue(oldest.Hash, out var retained) &&
                        retained.BlockHeight == oldest.Height)
                        _confirmedWalletObservations.Remove(oldest.Hash);
                }
            }
        }
        // One failed consumer must not suppress another consumer's committed observation.
        if (OnWalletTransactionObserved is not { } handlers)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<WalletTransactionEventArgs>>())
            Raise(() => handler(this, observed), "wallet transaction");
    }

    private WalletTransactionEventArgs? DescribeWalletTransaction(
        Transaction transaction, uint height, string blockHash,
        IReadOnlyDictionary<OutPoint, UtxoModel>? staged = null, BroadcastTransactionModel? row = null,
        IReadOnlyDictionary<OutPoint, TxOut>? previous = null, DateTimeOffset? timestamp = null)
    {
        var utxos = _serviceProvider.GetService<IUtxoMemoryRepository>();
        var outputs = new List<uint>();
        var inputs = new List<uint>();
        var inputAmounts = new List<long>();
        long received = 0, spent = 0;
        for (var i = 0; i < transaction.Outputs.Count; i++)
        {
            var output = transaction.Outputs[i];
            if (output.ScriptPubKey.GetDestinationAddress(_network) is { } address &&
                _watchedAddresses.ContainsKey(address.ToString()))
            {
                outputs.Add((uint)i);
                received += output.Value.Satoshi;
            }
        }
        for (var i = 0; i < transaction.Inputs.Count; i++)
        {
            var outpoint = transaction.Inputs[i].PrevOut;
            long? amount = null;
            if (utxos?.TryGetUtxo(new TxId(outpoint.Hash.ToBytes()), outpoint.N, out var known) == true)
                amount = known.Amount.Satoshi;
            else if (staged?.TryGetValue(outpoint, out var deposit) == true)
                amount = deposit.Amount.Satoshi;
            else if (previous?.TryGetValue(outpoint, out var output) == true &&
                     output.ScriptPubKey.GetDestinationAddress(_network) is { } address &&
                     _watchedAddresses.ContainsKey(address.ToString()))
                amount = output.Value.Satoshi;
            if (amount is not { } value)
                continue;
            inputs.Add((uint)i);
            inputAmounts.Add(value);
            spent += value;
        }
        if (outputs.Count == 0 && inputs.Count == 0)
            return null;
        var transactionHash = transaction.GetHash();
        row ??= _pendingBroadcasts.GetValueOrDefault(transactionHash);
        var fee = row?.Fee?.Satoshi ?? (inputs.Count == transaction.Inputs.Count && !transaction.IsCoinBase
                      ? Math.Max(0, spent - transaction.Outputs.Sum(o => o.Value.Satoshi)) : 0);
        return new WalletTransactionEventArgs(WalletTransactionHex(transaction), received - spent, fee, height, blockHash,
                                              timestamp ?? row?.CreatedAt ?? _timeProvider.GetUtcNow(), row?.Label ?? "",
                                              outputs, inputs, transactionHash.ToString(),
                                              ourInputAmounts: inputAmounts);
    }

    private static string WalletTransactionHex(Transaction transaction)
    {
        if (transaction.Inputs.Count > 0)
            return transaction.ToHex();
        // The chain-monitor fixtures use zero-input deposits. NBitcoin's witness serializer rejects them;
        // serialize those synthetic objects without witness support, as GetHash/Clone already do.
        using var bytes = new MemoryStream();
        transaction.ReadWrite(new BitcoinStream(bytes, true) { TransactionOptions = TransactionOptions.None });
        return Convert.ToHexString(bytes.ToArray()).ToLowerInvariant();
    }

    private async Task<IReadOnlyList<WalletTransactionEventArgs>> DescribeDisconnectedWalletTransactionsAsync(
        IReadOnlyList<BlockHeaderModel> disconnected)
    {
        var blocks = disconnected.Select(h => (h.Height, new uint256((byte[])h.BlockHash).ToString())).ToHashSet();
        List<WalletTransactionEventArgs> cached;
        lock (_walletObservationLock)
            cached = _confirmedWalletObservations.Values
                .Where(o => blocks.Contains((o.BlockHeight, o.BlockHash)))
                .Select(o => new WalletTransactionEventArgs(o.RawTransactionHex, o.AmountSat, o.FeeSat,
                    0, "", o.Timestamp, o.Label, o.OurOutputs, o.OurInputs, o.TxHash, isReorg: true)).ToList();
        // Retain committed snapshots even when a subscriber joins while the rewind save is pending.
        if (OnWalletTransactionObserved is null)
            return cached;
        try
        {
            return await DescribeDisconnectedWalletTransactionsCoreAsync(disconnected, cached);
        }
        catch (Exception)
        {
            // Unavailable/pruned historical blocks must not erase already known wallet events or halt a rewind.
            return cached;
        }
    }

    private async Task<IReadOnlyList<WalletTransactionEventArgs>> DescribeDisconnectedWalletTransactionsCoreAsync(
        IReadOnlyList<BlockHeaderModel> disconnected, IReadOnlyList<WalletTransactionEventArgs> cached)
    {
        var result = new List<WalletTransactionEventArgs>(cached);
        var alreadyDescribed = cached.Select(o => o.TxHash).ToHashSet(StringComparer.Ordinal);
        var transactions = new Dictionary<uint256, Transaction>();
        foreach (var header in disconnected)
        {
            // Already needed for wallet rollback; no accounting-history scan or transaction index is required.
            Block? block;
            try { block = await _bitcoinChainService.GetBlockAsync(new uint256((byte[])header.BlockHash)); }
            catch (Exception) { continue; }
            if (block is not null)
                foreach (var transaction in block.Transactions)
                    transactions[transaction.GetHash()] = transaction;
        }
        var previous = new Dictionary<OutPoint, TxOut>();
        foreach (var transaction in transactions.Values.ToArray())
        {
            if (alreadyDescribed.Contains(transaction.GetHash().ToString()))
                continue;
            foreach (var input in transaction.Inputs)
            {
                if (transaction.IsCoinBase)
                    continue;
                var outpoint = input.PrevOut;
                if (!transactions.TryGetValue(outpoint.Hash, out var parent))
                {
                    // getrawtransaction can be unavailable for old/pruned parents. Deposits still unconfirm.
                    try { parent = await _bitcoinChainService.GetTransactionAsync(outpoint.Hash); }
                    catch (Exception) { parent = null; }
                }
                if (parent is not null && outpoint.N < parent.Outputs.Count)
                    previous[outpoint] = parent.Outputs[(int)outpoint.N];
            }
            if (DescribeWalletTransaction(transaction, 0, "", previous: previous) is { } observed)
            {
                result.Add(new WalletTransactionEventArgs(observed.RawTransactionHex, observed.AmountSat,
                    observed.FeeSat, 0, "", observed.Timestamp, observed.Label, observed.OurOutputs,
                    observed.OurInputs, observed.TxHash, isReorg: true));
            }
        }
        return result;
    }
}