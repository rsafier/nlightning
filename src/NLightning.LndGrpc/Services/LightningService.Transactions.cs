using System.Globalization;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.LndGrpc.Services;

using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Networks;
using Infrastructure.Bitcoin.Wallet.Imports;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Lnrpc;
using Transaction = Lnrpc.Transaction;

public sealed partial class LightningService
{
    /// <summary>
    /// LND's <c>GetTransactions</c> (NL-1185, NL-1187): the on-chain wallet's history, one entry per transaction that
    /// moved a wallet output. The sources are merged by output and input identity, so each output and each spent
    /// outpoint counts once whatever source reports it (NL-1253): the wallet's durable history (the chain monitor's
    /// <c>WalletTransactions</c> rows, written in each block's save with the raw transaction and block, made unconfirmed
    /// by a reorg's save), the sealed accounting feed (<c>WalletReceived</c>/<c>WalletOutputSpent</c> less what a reorg
    /// reversed; the history from before the durable rows existed), the wallet outputs held since before the accounting
    /// cutover, our pending broadcasts, unconfirmed deposits and the imported tapscript history. <c>amount</c> is the
    /// wallet's outputs less its inputs (LND's net amount, fee included for a send).
    /// </summary>
    /// <remarks>
    /// <c>total_fees</c> follows btcwallet's rule: 0 unless every input is the wallet's (a dual-funded funding or a
    /// splice reports 0), then our broadcast row's fee or the inputs less the outputs. The durable history's height is
    /// authoritative for every transaction it stores (a reorg's unconfirmed verdict included), and a stored transaction
    /// a confirmed conflicting spend invalidated is removed by the chain monitor. An explicit bounded
    /// <c>wallethistory</c> backfill recovers older transactions within its retained-block and ownership-catalogue scope.
    /// Without that backfill, a transaction whose wallet outputs were all spent before the accounting cutover is not
    /// listed (spent outputs are deleted); a transaction
    /// that left a change output held since before the cutover is listed only when bitcoind can still return its parents
    /// (its inputs' values), a deposit held since then is listed with its outputs only; the raw transaction and block hash
    /// of a transaction known only to the accounting feed come from bitcoind (out of its block, so no <c>txindex</c> is
    /// needed) while it still has the block.
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

        var network = _nodeOptions.BitcoinNetwork.ToNBitcoinNetwork();
        var chain = scope.ServiceProvider.GetService<IBitcoinChainService>();
        var entries = new Dictionary<TxId, HistoryEntry>();

        // The wallet's durable history (NL-1187): the raw transaction, its block and the wallet's outputs and inputs.
        // Its height is authoritative for every transaction it stores, in or out of the range and null included: a
        // feed event, an imported or held output at a height a reorg disconnected never overrides it
        IReadOnlyDictionary<TxId, uint?> durableHeights = new Dictionary<TxId, uint?>();
        if (unitOfWork.WalletTransactionDbRepository is { } walletHistory)
        {
            using var historyLease = scope.ServiceProvider.GetService<Domain.Bitcoin.Wallet.Interfaces.IWalletHistoryGate>() is { } gate
                ? await gate.EnterAsync(cancellationToken) : null;
            const int historyPageSize = 500;
            for (var historyOffset = 0; ; historyOffset = checked(historyOffset + historyPageSize))
            {
                var batch = await walletHistory.GetHistoryPageAsync(startHeight, endHeight, includeUnconfirmed,
                    historyOffset, historyPageSize, cancellationToken);
                foreach (var record in batch) AddWalletRecord(entries, record, network);
                if (batch.Count < historyPageSize) break;
            }
            durableHeights = await walletHistory.GetHeightsAsync(cancellationToken);
        }

        // The sealed accounting feed: the history from before the durable rows existed
        foreach (var accountingEvent in await ReadWalletEventsAsync(unitOfWork.AccountingEventDbRepository,
                                                                    startHeight, endHeight, cancellationToken))
        {
            switch (accountingEvent.Kind)
            {
                case AccountingEventKind.WalletReceived when accountingEvent.TxId is { } txId:
                    {
                        var entry = Entry(entries, txId);
                        entry.FeedHeight ??= accountingEvent.BlockHeight;
                        entry.SetTime(accountingEvent.OccurredAt, TimeSource.Feed);
                        entry.AddOutput(accountingEvent.OutputIndex ?? 0, accountingEvent.AmountMsat / 1_000,
                                        accountingEvent.Details.GetValueOrDefault("address"));
                        break;
                    }
                case AccountingEventKind.WalletOutputSpent
                    when accountingEvent.Details.GetValueOrDefault("spentBy") is { } spender
                      && TryParseTxId(spender, out var spenderId):
                    {
                        var entry = Entry(entries, spenderId);
                        entry.FeedHeight ??= accountingEvent.BlockHeight;
                        entry.SetTime(accountingEvent.OccurredAt, TimeSource.Feed);
                        entry.AddInput($"{accountingEvent.TxId}:{accountingEvent.OutputIndex}",
                                       -accountingEvent.AmountMsat / 1_000);
                        break;
                    }
            }
        }

        ApplyDurableHeights(entries, durableHeights);

        // Our pending broadcasts: their wallet inputs are still in the UTXO set until a block holds them
        foreach (var pending in await unitOfWork.BroadcastTransactionDbRepository.GetPendingAsync())
        {
            if (!IsWalletMovement(pending) || !TryLoad(pending.RawTransaction, out var tx))
                continue;

            var known = entries.GetValueOrDefault(pending.TransactionId);
            if (known?.Height is not null)
                continue;

            var entry = known ?? new HistoryEntry(pending.TransactionId);
            var ours = false;
            foreach (var input in tx.Inputs)
            {
                var prevTxId = new TxId(input.PrevOut.Hash.ToBytes());
                if (_utxos is null || !_utxos.TryGetUtxo(prevTxId, input.PrevOut.N, out var utxo))
                    continue;

                entry.AddInput($"{prevTxId}:{input.PrevOut.N}", utxo.Amount.Satoshi);
                ours = true;
            }

            if (!ours && known is null)
                continue;

            entry.Tx ??= tx;
            entry.IsPendingBroadcast = true;
            entry.SetTime(pending.CreatedAt, TimeSource.Broadcast);
            entries[pending.TransactionId] = entry;
        }

        // Unconfirmed deposits the wallet already holds
        foreach (var utxo in _utxos?.GetUnreservedUtxos().Where(u => u.BlockHeight == 0) ?? [])
        {
            var entry = Entry(entries, utxo.TxId);
            entry.IsInMempool = true;
            entry.SetTime(_timeProvider.GetUtcNow(), TimeSource.Observed);
            entry.AddOutput(utxo.Index, utxo.Amount.Satoshi, WalletOutputAddress(utxo, network));
        }

        // Wallet outputs held since before the accounting cutover (or written while its gate was held): their creating
        // transactions are known to no other source
        await AddHeldOutputsAsync(entries, unitOfWork, chain, network, startHeight, endHeight, cancellationToken);

        if (scope.ServiceProvider.GetService<ImportedTapscriptTracker>() is { } imported)
        {
            ImportedWatchSnapshot snapshot;
            try { snapshot = await imported.SnapshotAsync(cancellationToken); }
            catch (InvalidOperationException e)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
            }

            AddImportedHistory(entries, snapshot, network);
        }

        ApplyDurableHeights(entries, durableHeights);

        // Pre-cutover transactions with a change output are our own sends: their inputs are needed for an honest amount
        var walletAddresses = new Lazy<HashSet<string>>(() => unitOfWork.WalletAddressesDbRepository.GetAllAddresses()
                                                                         .Select(a => a.Address)
                                                                         .ToHashSet(StringComparer.Ordinal));
        var blocks = new Dictionary<uint, Block?>();
        foreach (var entry in entries.Values.Where(e => e.IsHeldOutputOnly).ToList())
        {
            if (!await ResolveHeldOutputEntryAsync(entry, chain, walletAddresses, blocks, network))
                entries.Remove(entry.TxId);
        }

        var candidates = entries.Values
                                .Where(e => only is null || only.Contains(e.TxId))
                                .Where(e => e.Height is { } height
                                                ? !unconfirmedOnly && height >= startHeight && height <= endHeight
                                                : includeUnconfirmed)
                                .ToList();
        var selected = new List<HistoryEntry>(candidates.Count);
        foreach (var entry in candidates)
        {
            // A transaction a reorg disconnected stays listed (unconfirmed) while it is ours to send again or bitcoind's
            // mempool still holds it; one the new branch conflicted is gone
            if (entry.Height is null && !entry.IsPendingBroadcast && !entry.IsInMempool
             && !await IsInMempoolAsync(entry.TxId, chain))
                continue;

            selected.Add(entry);
        }

        selected = selected.OrderBy(e => e.Height ?? uint.MaxValue).ThenBy(e => e.Time)
                           .ThenBy(e => e.TxId.ToString(), StringComparer.Ordinal)
                           .ToList();
        var offset = (int)Math.Min(request.IndexOffset, (uint)selected.Count);
        var page = request.MaxTransactions == 0
                       ? selected.Skip(offset).ToList()
                       : selected.Skip(offset).Take((int)Math.Min(request.MaxTransactions, int.MaxValue)).ToList();

        var response = new TransactionDetails { FirstIndex = (ulong)offset, LastIndex = (ulong)(offset + page.Count) };
        var blockHashes = new Dictionary<uint, string>();
        foreach (var entry in page)
        {
            var row = await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(entry.TxId);
            if (entry.IsDurable && entry.Tx is null &&
                await unitOfWork.WalletTransactionDbRepository.GetByIdAsync(entry.TxId, cancellationToken) is { } stored)
                AddWalletRecord(entries, stored, network);
            var label = await unitOfWork.WalletTransactionDbRepository.GetLabelAsync(entry.TxId, cancellationToken);
            response.Transactions.Add(await ToRpcAsync(entry, row, tip, network, chain, blockHashes, blocks, label));
        }

        return response;
    }

    private void AddWalletRecord(Dictionary<TxId, HistoryEntry> entries, WalletTransactionRecord record,
                                 Network network)
    {
        if (record.OwnershipSummary is not null && record.RawTransaction.Length == 0)
        {
            AddWalletSummary(entries, record, network);
            return;
        }
        if (!TryLoad(record.RawTransaction, out var tx)) return;

        var entry = Entry(entries, record.TxId);
        entry.Tx = tx;
        entry.SetDurableHeight(record.BlockHeight);
        if (record.BlockHeight is not null)
        {
            if (record.BlockHash is { Length: 32 } hash)
                entry.BlockHash ??= new uint256(hash).ToString();
            entry.SetTime(record.Timestamp, TimeSource.Block);
        }
        else
        {
            entry.SetTime(record.Timestamp, TimeSource.Observed);
        }

        foreach (var index in record.OurOutputs)
        {
            if (index >= tx.Outputs.Count)
                continue;

            var output = tx.Outputs[(int)index];
            entry.AddOutput(index, output.Value.Satoshi,
                            output.ScriptPubKey.GetDestinationAddress(network)?.ToString());
        }

        foreach (var input in record.OurInputs)
        {
            if (input.InputIndex >= tx.Inputs.Count)
                continue;

            var prevOut = tx.Inputs[(int)input.InputIndex].PrevOut;
            entry.AddInput($"{new TxId(prevOut.Hash.ToBytes())}:{prevOut.N}", input.AmountSat);
        }
    }

    private static void AddWalletSummary(Dictionary<TxId, HistoryEntry> entries, WalletTransactionRecord record,
        Network network)
    {
        var entry = Entry(entries, record.TxId);
        entry.SetDurableHeight(record.BlockHeight);
        if (record.BlockHash is { Length: 32 } hash && record.BlockHeight is not null)
            entry.BlockHash = new uint256(hash).ToString();
        entry.SetTime(record.Timestamp, record.BlockHeight is null ? TimeSource.Observed : TimeSource.Block);
        foreach (var part in record.OwnershipSummary!.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var values = part.Split(':');
            var index = uint.Parse(values[1], CultureInfo.InvariantCulture);
            var amount = long.Parse(values[2], CultureInfo.InvariantCulture);
            if (values[0] == "o" && values.Length == 4)
                entry.AddOutput(index, amount, new Script(Convert.FromHexString(values[3])).GetDestinationAddress(network)?.ToString());
            else if (values[0] == "i" && values.Length == 5)
                entry.AddInput($"{new TxId(Convert.FromHexString(values[3]))}:{uint.Parse(values[4], CultureInfo.InvariantCulture)}", amount);
            else throw new InvalidDataException("Invalid durable wallet ownership projection.");
        }
    }

    private static void ApplyDurableHeights(Dictionary<TxId, HistoryEntry> entries,
                                            IReadOnlyDictionary<TxId, uint?> durableHeights)
    {
        if (durableHeights.Count == 0)
            return;

        foreach (var entry in entries.Values)
        {
            if (durableHeights.TryGetValue(entry.TxId, out var height))
                entry.SetDurableHeight(height);
        }
    }

    private static string? WalletOutputAddress(UtxoModel utxo, Network network) =>
        utxo.SilentPayment is { } silent
            ? new Script([0x51, 0x20, .. silent.OutputKey]).GetDestinationAddress(network)?.ToString()
            : utxo.WalletAddress?.Address;

    private static async Task AddHeldOutputsAsync(Dictionary<TxId, HistoryEntry> entries, IUnitOfWork unitOfWork,
                                                  IBitcoinChainService? chain, Network network, uint startHeight, uint endHeight,
                                                  CancellationToken cancellationToken)
    {
        IEnumerable<UtxoModel> held;
        try
        {
            held = await unitOfWork.UtxoDbRepository.GetUnspentAsync(includeWalletAddress: true) ?? [];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return;
        }

        var times = new Dictionary<uint, DateTimeOffset?>();
        foreach (var utxo in held)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (utxo.BlockHeight == 0 || utxo.BlockHeight < startHeight || utxo.BlockHeight > endHeight)
                continue;

            if (entries.TryGetValue(utxo.TxId, out var known))
            {
                // Another source knows the transaction: the held output can only complete it
                known.AddOutput(utxo.Index, utxo.Amount.Satoshi, WalletOutputAddress(utxo, network));
                continue;
            }

            var entry = new HistoryEntry(utxo.TxId)
            {
                HeldHeight = utxo.BlockHeight,
                IsHeldOutputOnly = true
            };
            if (!times.TryGetValue(utxo.BlockHeight, out var time))
                times[utxo.BlockHeight] = time = chain is null ? null : await BlockTimeAsync(chain, utxo.BlockHeight);
            entry.SetTime(time ?? DateTimeOffset.UnixEpoch, TimeSource.Block);
            entries[utxo.TxId] = entry;
            entry.AddOutput(utxo.Index, utxo.Amount.Satoshi, WalletOutputAddress(utxo, network));
            entry.HasChangeOutput |= utxo.IsAddressChange;
        }
    }

    private static async Task<DateTimeOffset?> BlockTimeAsync(IBitcoinChainService chain, uint height)
    {
        try
        {
            return await chain.GetBlockTimeAsync(height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The imported tapscript history joined with the wallet's: an output or spent outpoint both report counts once.
    /// </summary>
    private static void AddImportedHistory(Dictionary<TxId, HistoryEntry> entries, ImportedWatchSnapshot snapshot,
                                           Network network)
    {
        var importedValues = new Dictionary<NBitcoin.OutPoint, long>();
        foreach (var watched in snapshot.Transactions)
        {
            foreach (var index in watched.OurOutputs)
                importedValues[new NBitcoin.OutPoint(watched.Transaction.GetHash(), index)] =
                    watched.Transaction.Outputs[(int)index].Value.Satoshi;
        }

        foreach (var watched in snapshot.Transactions)
        {
            var txid = new TxId(watched.Transaction.GetHash().ToBytes());
            // The imported history knows none of the canonical wallet's inputs: a transaction otherwise known only by
            // held outputs still has its wallet inputs resolved (a pre-cutover send with an imported output)
            var entry = Entry(entries, txid, keepHeldOutputOnly: true);
            entry.Tx ??= watched.Transaction;
            entry.ImportedHeight = watched.Height;
            entry.BlockHash ??= watched.BlockHash.ToString();
            entry.SetTime(watched.Time, TimeSource.Block);
            foreach (var index in watched.OurOutputs)
            {
                var output = watched.Transaction.Outputs[(int)index];
                entry.AddOutput(index, output.Value.Satoshi,
                                output.ScriptPubKey.GetDestinationAddress(network)?.ToString());
            }

            foreach (var spent in watched.SpentOutputs)
            {
                if (importedValues.TryGetValue(spent, out var value))
                    entry.AddInput($"{new TxId(spent.Hash.ToBytes())}:{spent.N}", value);
            }
        }
    }

    /// <summary>
    /// Completes a transaction known only by wallet outputs held since before the accounting cutover: its raw
    /// transaction from its block and, for our own send (a change output), the wallet inputs from their parents. False
    /// when a send's inputs cannot be read (the amount would be a lie).
    /// </summary>
    private async Task<bool> ResolveHeldOutputEntryAsync(HistoryEntry entry, IBitcoinChainService? chain,
                                                         Lazy<HashSet<string>> walletAddresses,
                                                         Dictionary<uint, Block?> blocks, Network network)
    {
        if (!entry.HasChangeOutput)
            return true;

        if (chain is null)
            return false;

        var tx = entry.Tx ??= await FetchTransactionAsync(entry, chain, blocks);
        if (tx is null)
            return false;

        foreach (var input in tx.Inputs)
        {
            if (tx.IsCoinBase)
                break;

            var outpoint = $"{new TxId(input.PrevOut.Hash.ToBytes())}:{input.PrevOut.N}";
            if (entry.Inputs.ContainsKey(outpoint))
                continue;

            NBitcoin.Transaction? parent;
            try
            {
                parent = await chain.GetTransactionAsync(input.PrevOut.Hash);
            }
            catch (Exception)
            {
                parent = null;
            }

            if (parent is null || input.PrevOut.N >= parent.Outputs.Count)
                return false;

            var spent = parent.Outputs[(int)input.PrevOut.N];
            if (spent.ScriptPubKey.GetDestinationAddress(network)?.ToString() is { } address
             && walletAddresses.Value.Contains(address))
                entry.AddInput(outpoint, spent.Value.Satoshi);
        }

        return true;
    }

    private static async Task<bool> IsInMempoolAsync(TxId txId, IBitcoinChainService? chain)
    {
        if (chain is null)
            return false;

        try
        {
            return await chain.GetTransactionAsync(new uint256((byte[])txId)) is not null;
        }
        catch (Exception)
        {
            return false;
        }
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
                                               Dictionary<uint, Block?> blocks, string? transactionLabel = null)
    {
        var rpc = new Transaction
        {
            TxHash = entry.TxId.ToString(),
            Amount = entry.AmountSat,
            BlockHeight = (int)(entry.Height ?? 0),
            NumConfirmations = entry.Height is { } height && height <= tip ? (int)(tip - height + 1) : 0,
            TimeStamp = entry.Time.ToUnixTimeSeconds(),
            Label = transactionLabel ?? row?.Label ?? ""
        };
        if (entry.Height is { } confirmedAt)
            rpc.BlockHash = (entry.IndexHeight is not null || entry.ImportedHeight is not null
                                 ? entry.BlockHash
                                 : null)
                         ?? (row?.ConfirmedBlockHash is { } confirmedHash ? DisplayHex(confirmedHash) : null)
                         ?? await BlockHashAsync(confirmedAt, chain, blockHashes);

        var tx = entry.Tx;
        if (tx is null && row is not null && TryLoad(row.RawTransaction, out var loaded))
            tx = loaded;
        if (tx is null && chain is not null)
            tx = await FetchTransactionAsync(entry, chain, blocks);
        if (tx is not null)
            rpc.RawTxHex = Convert.ToHexString(tx.ToBytes()).ToLowerInvariant();

        // btcwallet's rule (makeTxSummary): a fee only when every input is a wallet debit, so a dual-funded funding, a
        // splice or anything else with a peer's input reports 0 even though our broadcast row knows its fee; when every
        // input is ours the row's fee is the same value
        if (tx is null)
            rpc.TotalFees = row?.Fee?.Satoshi ?? 0;
        else
            rpc.TotalFees = !tx.IsCoinBase && tx.Inputs.Count > 0
                         && tx.Inputs.All(i => entry.Inputs.ContainsKey(
                                                  $"{new TxId(i.PrevOut.Hash.ToBytes())}:{i.PrevOut.N}"))
                                ? row?.Fee?.Satoshi ?? Math.Max(0, entry.SpentSat - tx.Outputs.Sum(o => o.Value.Satoshi))
                                : 0;

        if (tx is not null)
        {
            for (var i = 0; i < tx.Outputs.Count; i++)
            {
                var output = tx.Outputs[i];
                rpc.OutputDetails.Add(new OutputDetail
                {
                    OutputType = ScriptTypeOf(output.ScriptPubKey),
                    Address = output.ScriptPubKey.GetDestinationAddress(network)?.ToString() ?? "",
                    PkScript = Convert.ToHexString(output.ScriptPubKey.ToBytes()).ToLowerInvariant(),
                    OutputIndex = i,
                    Amount = output.Value.Satoshi,
                    IsOurAddress = entry.Outputs.ContainsKey(i)
                });
            }

            foreach (var input in tx.Inputs)
            {
                var outpoint = $"{new TxId(input.PrevOut.Hash.ToBytes())}:{input.PrevOut.N}";
                rpc.PreviousOutpoints.Add(new PreviousOutPoint
                {
                    Outpoint = outpoint,
                    IsOurOutput = entry.Inputs.ContainsKey(outpoint)
                });
            }
        }
        else
        {
            foreach (var (index, (amountSat, address)) in entry.Outputs.OrderBy(o => o.Key))
            {
                Script? script = null;
                if (address is not null)
                    try { script = BitcoinAddress.Create(address, network).ScriptPubKey; }
                    catch (FormatException) { /* Retain ownership when a historical address cannot be decoded. */ }
                rpc.OutputDetails.Add(new OutputDetail
                {
                    Address = address ?? "",
                    OutputIndex = index,
                    Amount = amountSat,
                    IsOurAddress = true,
                    PkScript = script is null ? "" : Convert.ToHexStringLower(script.ToBytes()),
                    OutputType = script is null ? OutputScriptType.ScriptTypeNonStandard : ScriptTypeOf(script)
                });
            }
            foreach (var outpoint in entry.Inputs.Keys)
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
                    or BroadcastPurpose.HtlcTransaction or BroadcastPurpose.Splice or BroadcastPurpose.Unspecified
                    or BroadcastPurpose.WalletCollaborative;

    private static HistoryEntry Entry(Dictionary<TxId, HistoryEntry> entries, TxId txId,
                                      bool keepHeldOutputOnly = false)
    {
        if (!entries.TryGetValue(txId, out var entry))
            entries[txId] = entry = new HistoryEntry(txId);
        else if (!keepHeldOutputOnly)
            entry.IsHeldOutputOnly = false;
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

    /// <summary>Where an entry's time came from, most trusted last: a block's time wins over the others.</summary>
    private enum TimeSource
    {
        None,
        Observed,
        Broadcast,
        Feed,
        Block
    }

    /// <summary>
    /// One wallet transaction being assembled from every source, keyed by output index and spent outpoint so a source
    /// that repeats another's output or input adds nothing (NL-1253).
    /// </summary>
    private sealed class HistoryEntry(TxId txId)
    {
        private TimeSource _timeSource;

        public TxId TxId { get; } = txId;

        /// <summary>The height the wallet's durable history confirmed it at (null: unconfirmed, or not stored).</summary>
        public uint? IndexHeight { get; private set; }

        /// <summary>The wallet's durable history stores it: its height is the only one that counts.</summary>
        public bool IsDurable { get; private set; }

        /// <summary>The height the accounting feed recorded.</summary>
        public uint? FeedHeight { get; set; }

        /// <summary>The height the imported tapscript history found it at.</summary>
        public uint? ImportedHeight { get; set; }

        /// <summary>The height of a wallet output held since before the cutover.</summary>
        public uint? HeldHeight { get; set; }

        /// <summary>
        /// The confirmation height: the durable history's whenever it stores the transaction (a reorg's unconfirmed
        /// verdict included), else the first other source's.
        /// </summary>
        public uint? Height => IsDurable ? IndexHeight : FeedHeight ?? ImportedHeight ?? HeldHeight;

        /// <summary>The display hash of the block that holds it, when a source stored it.</summary>
        public string? BlockHash { get; set; }

        public DateTimeOffset Time { get; private set; }

        public NBitcoin.Transaction? Tx { get; set; }

        public bool IsPendingBroadcast { get; set; }

        public bool IsInMempool { get; set; }

        /// <summary>Known only from wallet outputs held since before the accounting cutover.</summary>
        public bool IsHeldOutputOnly { get; set; }

        /// <summary>One of those outputs pays a change address (the transaction was our own send).</summary>
        public bool HasChangeOutput { get; set; }

        public Dictionary<long, (long AmountSat, string? Address)> Outputs { get; } = [];

        public Dictionary<string, long> Inputs { get; } = new(StringComparer.Ordinal);

        public long SpentSat => Inputs.Values.Sum();

        public long AmountSat => Outputs.Values.Sum(o => o.AmountSat) - SpentSat;

        public void AddOutput(long index, long amountSat, string? address)
        {
            if (Outputs.TryGetValue(index, out var known))
            {
                if (known.Address is null && address is not null)
                    Outputs[index] = (known.AmountSat, address);
                return;
            }

            Outputs[index] = (amountSat, address);
        }

        public void AddInput(string outpoint, long amountSat) => Inputs.TryAdd(outpoint, amountSat);

        public void SetDurableHeight(uint? height)
        {
            IsDurable = true;
            IndexHeight = height;
        }

        public void SetTime(DateTimeOffset time, TimeSource source)
        {
            if (source < _timeSource || (source == _timeSource && time >= Time && _timeSource != TimeSource.None))
                return;

            Time = time;
            _timeSource = source;
        }
    }
}