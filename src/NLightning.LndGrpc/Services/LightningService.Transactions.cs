using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.LndGrpc.Services;

using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Networks;
using Infrastructure.Bitcoin.Wallet.Imports;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Lnrpc;
using Transaction = Lnrpc.Transaction;

public sealed partial class LightningService
{
    /// <summary>
    /// LND's <c>GetTransactions</c> (NL-1185): the on-chain wallet's history, one entry per transaction that moved a
    /// wallet output. Confirmed ones come from the accounting feed (sealed first): every wallet output a block deposits
    /// (<c>WalletReceived</c>, keyed by the creating transaction) and every one it spends (<c>WalletOutputSpent</c>,
    /// keyed by its spender), less what a reorg reversed; <c>amount</c> is their difference (LND's net amount, fee
    /// included for a send). Our own broadcasts add the raw transaction, label and fee; unconfirmed ones are our pending
    /// broadcasts and unconfirmed deposits.
    /// </summary>
    /// <remarks>
    /// Gaps: history from before the accounting cutover (the opening balance) and with <c>Accounting:Enabled=false</c>
    /// is not listed; <c>block_hash</c> and the raw transactions that are not ours come from bitcoind (out of the block, so
    /// no <c>txindex</c> is needed) while it still has them; <c>total_fees</c> is our broadcast row's, or computed when
    /// every input was ours.
    /// </remarks>
    public override Task<TransactionDetails> GetTransactions(GetTransactionsRequest request,
                                                             ServerCallContext context)
    {
        if (request.Account.Length > 0 && request.Account != "default")
            throw NotFound($"account {request.Account} not found");

        return ListWalletTransactionsAsync(request, null, false, context.CancellationToken);
    }

    /// <summary>
    /// <c>GetTransactions</c>' history (see there), limited to <paramref name="only"/> when given and to the
    /// unconfirmed transactions with <paramref name="unconfirmedOnly"/> (walletrpc <c>ListSweeps</c>' verbose answer,
    /// NL-1245, is this history filtered to the sweeps).
    /// </summary>
    internal async Task<TransactionDetails> ListWalletTransactionsAsync(GetTransactionsRequest request,
                                                                       IReadOnlySet<TxId>? only, bool unconfirmedOnly,
                                                                       CancellationToken cancellationToken)
    {
        var tip = _blockchainMonitor?.LastProcessedBlockHeight ?? 0;
        var includeUnconfirmed = request.EndHeight is -1 or 0;
        var endHeight = request.EndHeight <= 0 ? uint.MaxValue : (uint)request.EndHeight;
        var startHeight = request.StartHeight <= 0 ? 0u : (uint)request.StartHeight;

        await using var scope = CreateScope();
        var unitOfWork = UnitOfWork(scope);
        if (scope.ServiceProvider.GetService<IAccountingEventSealer>() is { } sealer)
        {
            try
            {
                await sealer.SealNowAsync(cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The sealed part is still a consistent history
            }
        }

        var entries = new Dictionary<TxId, HistoryEntry>();
        foreach (var accountingEvent in await ReadWalletEventsAsync(unitOfWork.AccountingEventDbRepository,
                                                                    startHeight, endHeight, cancellationToken))
        {
            switch (accountingEvent.Kind)
            {
                case AccountingEventKind.WalletReceived when accountingEvent.TxId is { } txId:
                    {
                        var entry = Entry(entries, txId, accountingEvent);
                        entry.AmountSat += accountingEvent.AmountMsat / 1_000;
                        entry.OurOutputs.Add((accountingEvent.OutputIndex ?? 0, accountingEvent.AmountMsat / 1_000,
                                              accountingEvent.Details.GetValueOrDefault("address")));
                        break;
                    }
                case AccountingEventKind.WalletOutputSpent
                    when accountingEvent.Details.GetValueOrDefault("spentBy") is { } spender
                      && TryParseTxId(spender, out var spenderId):
                    {
                        var entry = Entry(entries, spenderId, accountingEvent);
                        entry.AmountSat += accountingEvent.AmountMsat / 1_000;
                        entry.SpentOurs += -accountingEvent.AmountMsat / 1_000;
                        entry.PreviousOutpoints.Add($"{accountingEvent.TxId}:{accountingEvent.OutputIndex}");
                        break;
                    }
            }
        }

        // Our pending broadcasts: their wallet inputs are still in the UTXO set until a block holds them
        foreach (var pending in await unitOfWork.BroadcastTransactionDbRepository.GetPendingAsync())
        {
            if (entries.ContainsKey(pending.TransactionId) || !IsWalletMovement(pending))
                continue;

            var entry = new HistoryEntry(pending.TransactionId) { Time = pending.CreatedAt };
            if (TryLoad(pending.RawTransaction, out var tx))
            {
                foreach (var input in tx.Inputs)
                {
                    var prevTxId = new TxId(input.PrevOut.Hash.ToBytes());
                    if (_utxos?.TryGetUtxo(prevTxId, input.PrevOut.N, out var utxo) == true)
                    {
                        entry.AmountSat -= utxo.Amount.Satoshi;
                        entry.SpentOurs += utxo.Amount.Satoshi;
                        entry.PreviousOutpoints.Add($"{prevTxId}:{input.PrevOut.N}");
                    }
                }
            }

            if (entry.PreviousOutpoints.Count > 0)
                entries[pending.TransactionId] = entry;
        }

        // Unconfirmed deposits the wallet already holds
        foreach (var utxo in _utxos?.GetUnreservedUtxos().Where(u => u.BlockHeight == 0) ?? [])
        {
            if (!entries.TryGetValue(utxo.TxId, out var entry))
                entries[utxo.TxId] = entry = new HistoryEntry(utxo.TxId) { Time = _timeProvider.GetUtcNow() };
            if (entry.OurOutputs.Any(o => o.Index == utxo.Index))
                continue;

            entry.AmountSat += utxo.Amount.Satoshi;
            var address = utxo.WalletAddress?.Address;
            if (utxo.SilentPayment is { } silent)
                address = new Script(new byte[] { 0x51, 0x20 }.Concat(silent.OutputKey).ToArray())
                    .GetDestinationAddress(_nodeOptions.BitcoinNetwork.ToNBitcoinNetwork())?.ToString();
            entry.OurOutputs.Add((utxo.Index, utxo.Amount.Satoshi, address));
        }

        if (scope.ServiceProvider.GetService<ImportedTapscriptTracker>() is { } imported)
        {
            ImportedWatchSnapshot snapshot;
            try { snapshot = await imported.SnapshotAsync(cancellationToken); }
            catch (InvalidOperationException e)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
            }
            foreach (var watched in snapshot.Transactions)
            {
                var txid = new TxId(watched.Transaction.GetHash().ToBytes());
                if (!entries.TryGetValue(txid, out var entry))
                    entries[txid] = entry = new HistoryEntry(txid) { Height = watched.Height, Time = watched.Time };
                entry.AmountSat += watched.Amount;
                foreach (var index in watched.OurOutputs)
                {
                    var output = watched.Transaction.Outputs[(int)index];
                    entry.OurOutputs.Add((index, output.Value.Satoshi, output.ScriptPubKey.GetDestinationAddress(_nodeOptions.BitcoinNetwork.ToNBitcoinNetwork())!.ToString()));
                }
                foreach (var spent in watched.SpentOutputs)
                    entry.PreviousOutpoints.Add($"{spent.Hash}:{spent.N}");
            }
        }

        var selected = entries.Values
                              .Where(e => only is null || only.Contains(e.TxId))
                              .Where(e => e.Height is { } height
                                              ? !unconfirmedOnly && height >= startHeight && height <= endHeight
                                              : includeUnconfirmed)
                              .OrderBy(e => e.Height ?? uint.MaxValue).ThenBy(e => e.Time)
                              .ThenBy(e => e.TxId.ToString(), StringComparer.Ordinal)
                              .ToList();
        var offset = (int)Math.Min(request.IndexOffset, (uint)selected.Count);
        var page = request.MaxTransactions == 0
                       ? selected.Skip(offset).ToList()
                       : selected.Skip(offset).Take((int)Math.Min(request.MaxTransactions, int.MaxValue)).ToList();

        var response = new TransactionDetails { FirstIndex = (ulong)offset, LastIndex = (ulong)(offset + page.Count) };
        var network = _nodeOptions.BitcoinNetwork.ToNBitcoinNetwork();
        var chain = scope.ServiceProvider.GetService<IBitcoinChainService>();
        var blockHashes = new Dictionary<uint, string>();
        var blocks = new Dictionary<uint, Block?>();
        foreach (var entry in page)
        {
            var row = await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(entry.TxId);
            response.Transactions.Add(await ToRpcAsync(entry, row, tip, network, chain, blockHashes, blocks));
        }

        return response;
    }

    private static async Task<IReadOnlyList<AccountingEventModel>> ReadWalletEventsAsync(
        IAccountingEventDbRepository repository, uint startHeight, uint endHeight,
        CancellationToken cancellationToken)
    {
        const int pageSize = 1_000;
        var all = new List<AccountingEventModel>();
        long after = 0;
        while (true)
        {
            var batch = await repository.GetWalletHistoryAsync(startHeight, endHeight, after, pageSize,
                                                               cancellationToken);
            all.AddRange(batch);
            if (batch.Count < pageSize || batch[^1].LedgerSeq is not { } last)
                break;

            after = last;
        }

        return all;
    }

    private async Task<Transaction> ToRpcAsync(HistoryEntry entry, BroadcastTransactionModel? row, uint tip,
                                               Network network, IBitcoinChainService? chain,
                                               Dictionary<uint, string> blockHashes,
                                               Dictionary<uint, Block?> blocks)
    {
        var rpc = new Transaction
        {
            TxHash = entry.TxId.ToString(),
            Amount = entry.AmountSat,
            BlockHeight = (int)(entry.Height ?? 0),
            NumConfirmations = entry.Height is { } height && height <= tip ? (int)(tip - height + 1) : 0,
            TimeStamp = entry.Time.ToUnixTimeSeconds(),
            Label = row?.Label ?? ""
        };
        if (entry.Height is { } confirmedAt)
            rpc.BlockHash = (row?.ConfirmedBlockHash is { } confirmedHash ? DisplayHex(confirmedHash) : null)
                         ?? await BlockHashAsync(confirmedAt, chain, blockHashes);

        NBitcoin.Transaction? tx = null;
        if (row is not null && TryLoad(row.RawTransaction, out var loaded))
            tx = loaded;
        else if (chain is not null)
            tx = await FetchTransactionAsync(entry, chain, blocks);
        if (tx is not null)
            rpc.RawTxHex = Convert.ToHexString(tx.ToBytes()).ToLowerInvariant();

        var outputsSat = tx?.Outputs.Sum(o => o.Value.Satoshi);
        rpc.TotalFees = row?.Fee?.Satoshi
                     ?? (tx is not null && entry.PreviousOutpoints.Count == tx.Inputs.Count
                             ? Math.Max(0, entry.SpentOurs - outputsSat!.Value)
                             : 0);

        if (tx is not null)
        {
            for (var i = 0; i < tx.Outputs.Count; i++)
            {
                var output = tx.Outputs[i];
                var ours = entry.OurOutputs.Any(o => o.Index == i);
                rpc.OutputDetails.Add(new OutputDetail
                {
                    OutputType = ScriptTypeOf(output.ScriptPubKey),
                    Address = output.ScriptPubKey.GetDestinationAddress(network)?.ToString() ?? "",
                    PkScript = Convert.ToHexString(output.ScriptPubKey.ToBytes()).ToLowerInvariant(),
                    OutputIndex = i,
                    Amount = output.Value.Satoshi,
                    IsOurAddress = ours
                });
            }

            foreach (var input in tx.Inputs)
            {
                var outpoint = $"{new TxId(input.PrevOut.Hash.ToBytes())}:{input.PrevOut.N}";
                rpc.PreviousOutpoints.Add(new PreviousOutPoint
                {
                    Outpoint = outpoint,
                    IsOurOutput = entry.PreviousOutpoints.Contains(outpoint)
                });
            }
        }
        else
        {
            foreach (var (index, amountSat, address) in entry.OurOutputs.OrderBy(o => o.Index))
            {
                Script? script = null;
                if (address is not null)
                    try { script = BitcoinAddress.Create(address, network).ScriptPubKey; }
                    catch (FormatException) { /* Keep historical ownership even when an address is unavailable. */ }
                rpc.OutputDetails.Add(new OutputDetail
                {
                    Address = address ?? "",
                    PkScript = script is null ? "" : Convert.ToHexStringLower(script.ToBytes()),
                    OutputIndex = index,
                    Amount = amountSat,
                    IsOurAddress = true,
                    OutputType = script is null ? OutputScriptType.ScriptTypeWitnessV0PubkeyHash : ScriptTypeOf(script)
                });
            }
            foreach (var outpoint in entry.PreviousOutpoints)
                rpc.PreviousOutpoints.Add(new PreviousOutPoint { Outpoint = outpoint, IsOurOutput = true });
        }

        return rpc;
    }

    /// <summary>
    /// A transaction that is not ours from bitcoind: out of its block when it is confirmed (no <c>txindex</c> needed),
    /// from the mempool otherwise; null when bitcoind does not have it (pruned) or cannot be reached.
    /// </summary>
    private static async Task<NBitcoin.Transaction?> FetchTransactionAsync(HistoryEntry entry,
                                                                          IBitcoinChainService chain,
                                                                          Dictionary<uint, Block?> blocks)
    {
        var hash = new uint256((byte[])entry.TxId);
        try
        {
            if (entry.Height is not { } height)
                return await chain.GetTransactionAsync(hash);

            if (!blocks.TryGetValue(height, out var block))
                blocks[height] = block = await chain.GetBlockAsync(height);
            return block?.Transactions.FirstOrDefault(t => t.GetHash() == hash);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<string> BlockHashAsync(uint height, IBitcoinChainService? chain,
                                                     Dictionary<uint, string> cache)
    {
        if (cache.TryGetValue(height, out var known))
            return known;

        var hash = "";
        if (chain is not null)
        {
            try
            {
                hash = (await chain.GetBlockHashAsync(height)).ToString();
            }
            catch (Exception)
            {
                // bitcoind unreachable: the hash stays empty
            }
        }

        cache[height] = hash;
        return hash;
    }

    /// <summary>Transactions that can move wallet outputs: our sends, fundings, CPFP children and sweeps.</summary>
    private static bool IsWalletMovement(BroadcastTransactionModel row) =>
        row.Purpose is BroadcastPurpose.WalletSend or BroadcastPurpose.Funding or BroadcastPurpose.AnchorCpfp
                    or BroadcastPurpose.HtlcTransaction or BroadcastPurpose.Splice or BroadcastPurpose.Unspecified;

    private static HistoryEntry Entry(Dictionary<TxId, HistoryEntry> entries, TxId txId,
                                      AccountingEventModel accountingEvent)
    {
        if (!entries.TryGetValue(txId, out var entry))
            entries[txId] = entry = new HistoryEntry(txId) { Time = accountingEvent.OccurredAt };
        entry.Height ??= accountingEvent.BlockHeight;
        if (accountingEvent.OccurredAt < entry.Time)
            entry.Time = accountingEvent.OccurredAt;
        return entry;
    }

    private static bool TryParseTxId(string text, out TxId txId)
    {
        txId = default;
        if (!uint256.TryParse(text, out var hash))
            return false;

        txId = new TxId(hash.ToBytes());
        return true;
    }

    private bool TryLoad(byte[] raw, out NBitcoin.Transaction tx)
    {
        try
        {
            tx = NBitcoin.Transaction.Load(raw, _nodeOptions.BitcoinNetwork.ToNBitcoinNetwork());
            return true;
        }
        catch (Exception)
        {
            tx = null!;
            return false;
        }
    }

    private static OutputScriptType ScriptTypeOf(Script script)
    {
        if (script.IsScriptType(ScriptType.P2WPKH))
            return OutputScriptType.ScriptTypeWitnessV0PubkeyHash;
        if (script.IsScriptType(ScriptType.P2WSH))
            return OutputScriptType.ScriptTypeWitnessV0ScriptHash;
        if (script.IsScriptType(ScriptType.Taproot))
            return OutputScriptType.ScriptTypeWitnessV1Taproot;
        if (script.IsScriptType(ScriptType.P2PKH))
            return OutputScriptType.ScriptTypePubkeyHash;
        if (script.IsScriptType(ScriptType.P2SH))
            return OutputScriptType.ScriptTypeScriptHash;
        if (script.IsUnspendable)
            return OutputScriptType.ScriptTypeNulldata;
        return OutputScriptType.ScriptTypeNonStandard;
    }

    /// <summary>One wallet transaction being assembled.</summary>
    private sealed class HistoryEntry(TxId txId)
    {
        public TxId TxId { get; } = txId;

        public uint? Height { get; set; }

        public DateTimeOffset Time { get; set; }

        public long AmountSat { get; set; }

        public long SpentOurs { get; set; }

        public List<(long Index, long AmountSat, string? Address)> OurOutputs { get; } = [];

        public HashSet<string> PreviousOutpoints { get; } = new(StringComparer.Ordinal);
    }
}