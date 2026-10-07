using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet.Imports;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Persistence.Interfaces;
using Interfaces;

/// <summary>
/// Durable confirmed-only imported history, separate from spendable wallet UTXOs and accounting.
/// The checkpoint and raw relevant transactions are saved atomically. New blocks extend the index;
/// reorgs rewind to its last surviving relevant block, then replay the active chain.
/// </summary>
public sealed class ImportedTapscriptTracker : IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IBitcoinChainService _chain;
    private readonly IBlockchainMonitor _monitor;
    private readonly ILogger<ImportedTapscriptTracker> _logger;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _lifetime;
    private int _requested;
    private int _worker;
    private CachedWatch? _cached;
    private readonly Lock _observationsLock = new();
    private readonly List<WalletTransactionEventArgs> _pendingCanonical = [];
    private readonly List<WalletTransactionEventArgs> _pendingImported = [];
    private (uint Height, string Hash)? _completedTip;
    private readonly Dictionary<string, (uint Height, string Hash)> _published = new(StringComparer.Ordinal);
    private readonly Queue<string> _publishedOrder = new();
    private HashSet<string> _importedScripts = new(StringComparer.Ordinal);
    private Dictionary<OutPoint, long> _importedOutputs = [];

    /// <summary>Immutable confirmations and unconfirmations, published only after the index commit. No replay.</summary>
    public event EventHandler<WalletTransactionEventArgs>? OnTransactionObserved;
    public event Action<Exception>? OnObservationFailed;

    public ImportedTapscriptTracker(IServiceScopeFactory scopes, IBitcoinChainService chain,
                                    IBlockchainMonitor monitor, ILogger<ImportedTapscriptTracker>? logger = null)
    {
        _scopes = scopes;
        _chain = chain;
        _monitor = monitor;
        _logger = logger ?? NullLogger<ImportedTapscriptTracker>.Instance;
        _lifetime = _stop.Token;
        monitor.OnWalletTransactionObserved += OnCanonicalTransaction;
        monitor.OnWalletTransactionsProcessed += OnWalletTransactionsProcessed;
        monitor.OnWalletTransactionsProcessing += OnWalletTransactionsProcessing;
    }

    public void Dispose()
    {
        _monitor.OnWalletTransactionObserved -= OnCanonicalTransaction;
        _monitor.OnWalletTransactionsProcessed -= OnWalletTransactionsProcessed;
        _monitor.OnWalletTransactionsProcessing -= OnWalletTransactionsProcessing;
        _stop.Cancel();
        // The gate remains valid until any in-flight RPC has unwound its finally block.
        _stop.Dispose();
    }

    public async Task ImportAsync(ImportedTapscript script, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (await uow.ImportedTapscriptDbRepository.GetAsync(script.Script) is not null)
                return;
            if ((await uow.ImportedTapscriptDbRepository.ListAsync()).Count >= 1000)
                throw new InvalidOperationException("imported tapscript limit reached");
            uow.ImportedTapscriptDbRepository.Add(script);
            ct.ThrowIfCancellationRequested();
            await uow.SaveChangesAsync();
            _cached = null;
            lock (_observationsLock)
                _importedScripts.Add(Convert.ToHexString(script.Script));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Initializes durable ownership then joins the live source under the checkpoint gate.</summary>
    public async Task SubscribeAsync(EventHandler<WalletTransactionEventArgs> handler, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await SnapshotCoreAsync(ct);
            lock (_observationsLock)
            {
                if (OnTransactionObserved is null)
                {
                    // A first/reconnected reader starts at this initialized checkpoint. Events queued by
                    // earlier RPC catch-up belong to its baseline, not to the live subscription.
                    _pendingImported.Clear();
                    _pendingCanonical.Clear();
                }
                OnTransactionObserved += handler;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<ImportedWatchSnapshot> SnapshotAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await SnapshotCoreAsync(ct); }
        catch (NBitcoin.RPC.RPCException e) when (e.RPCCode == NBitcoin.RPC.RPCErrorCode.RPC_INVALID_PARAMETER
            || e.Message.Contains("pruned", StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("Imported history is unavailable while the chain changes or historical blocks are pruned; retry.", e); }
        finally { _gate.Release(); }
    }

    private async Task<ImportedWatchSnapshot> SnapshotCoreAsync(CancellationToken ct)
    {
        if (_monitor.IsChainProcessingHalted)
            throw new InvalidOperationException("chain processing halted");
        await using var scope = _scopes.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var scripts = await uow.ImportedTapscriptDbRepository.ListAsync();
        var tip = _monitor.LastProcessedBlockHeight;
        if (scripts.Count == 0)
        {
            _cached = null;
            PublishCommitted(tip, null);
            return new ImportedWatchSnapshot(tip, [], []);
        }
        ct.ThrowIfCancellationRequested();
        var anchor = await _chain.GetBlockHashAsync(tip).WaitAsync(ct);
        var scriptSet = string.Join("|", scripts.OrderBy(s => Convert.ToHexString(s.Script), StringComparer.Ordinal)
            .Select(s => Convert.ToHexString(s.Script) + ":" + s.CreatedHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (_cached is { } cached && cached.Snapshot.Tip == tip && cached.Hash == anchor && cached.ScriptSet == scriptSet)
        {
            PublishCommitted(tip, anchor.ToString());
            return Copy(cached.Snapshot);
        }

        var start = scripts.Min(s => s.CreatedHeight);
        var saved = await uow.ImportedTapscriptDbRepository.GetIndexAsync();
        var original = saved is not null ? ReadHistory(saved.History, ct) : [];
        var transactions = saved is not null && saved.ScriptSet == scriptSet ? original.ToList() : [];
        ulong scanFrom = start;
        uint256? previous = null;
        if (saved is not null && saved.ScriptSet == scriptSet)
        {
            if (saved.Height <= tip && new uint256(saved.BlockHash) == await _chain.GetBlockHashAsync(saved.Height).WaitAsync(ct))
            {
                scanFrom = Math.Max((ulong)start, (ulong)saved.Height + 1);
                previous = new uint256(saved.BlockHash);
            }
            else
            {
                transactions.RemoveAll(t => t.Height > tip);
                while (transactions.Count > 0)
                {
                    var last = transactions[^1];
                    if (last.BlockHash == await _chain.GetBlockHashAsync(last.Height).WaitAsync(ct))
                    {
                        scanFrom = (ulong)last.Height + 1;
                        previous = last.BlockHash;
                        break;
                    }
                    transactions.RemoveAll(t => t.Height >= last.Height);
                }
            }
        }
        if ((ulong)tip >= scanFrom && (ulong)tip - scanFrom > 1_000_000)
            throw new InvalidOperationException("imported tapscript scan exceeds one million blocks");
        var tracked = scripts.ToDictionary(s => Convert.ToHexString(s.Script), StringComparer.Ordinal);
        var outputs = new Dictionary<OutPoint, ImportedWatchOutput>();
        var history = new List<ImportedWatchTransaction>();
        foreach (var transaction in transactions)
        {
            ct.ThrowIfCancellationRequested();
            Apply(transaction, tracked, outputs, history);
        }

        for (var height = scanFrom; height <= tip; height++)
        {
            var block = await _chain.GetBlockAsync((uint)height).WaitAsync(ct)
                     ?? throw new InvalidOperationException($"imported tapscript historical block {height} unavailable (possibly pruned)");
            if (previous is not null && block.Header.HashPrevBlock != previous)
                throw new InvalidOperationException("chain changed during imported tapscript scan; retry");
            previous = block.GetHash();
            foreach (var tx in block.Transactions)
            {
                var transaction = new IndexedTransaction((uint)height, previous, block.Header.BlockTime, tx);
                if (Apply(transaction, tracked, outputs, history))
                    transactions.Add(transaction);
            }
        }
        // Verify the same active tip after all reads and before staging any checkpoint writes.
        if ((previous is not null && scanFrom <= tip && previous != anchor)
         || await _chain.GetBlockHashAsync(tip).WaitAsync(ct) != anchor
         || _monitor.IsChainProcessingHalted)
            throw new InvalidOperationException("chain changed during imported tapscript scan; retry");
        // Build notifications before saving: serialization or mapping failures cannot escape a partial commit.
        var oldHistory = new List<ImportedWatchTransaction>();
        var oldOutputs = new Dictionary<OutPoint, ImportedWatchOutput>();
        foreach (var transaction in original)
            Apply(transaction, tracked, oldOutputs, oldHistory);
        var changes = saved is null ? [] : DescribeChanges(oldHistory, history);
        await uow.ImportedTapscriptDbRepository.SetIndexAsync(new ImportedWatchIndex(tip, anchor.ToBytes(), scriptSet, WriteHistory(transactions, ct)));
        ct.ThrowIfCancellationRequested();
        await uow.SaveChangesAsync();
        var snapshot = new ImportedWatchSnapshot(tip, outputs.Values.ToList(), history);
        _cached = new CachedWatch(anchor, scriptSet, Copy(snapshot));
        lock (_observationsLock)
        {
            _importedScripts = tracked.Keys.ToHashSet(StringComparer.Ordinal);
            // Retain disconnected parents until the next checkpoint so a rewind notice can keep ownership.
            _importedOutputs = original.Concat(transactions).SelectMany(t => t.Transaction.Outputs
                .Select((output, i) => (Output: output, Point: new OutPoint(t.Transaction.GetHash(), (uint)i))))
                .Where(o => _importedScripts.Contains(Convert.ToHexString(o.Output.ScriptPubKey.ToBytes())))
                .GroupBy(o => o.Point).ToDictionary(g => g.Key, g => g.First().Output.Value.Satoshi);
        }
        lock (_observationsLock)
        {
            foreach (var observed in changes)
            {
                if (observed.BlockHeight == 0)
                    _pendingImported.RemoveAll(t => t.TxHash == observed.TxHash && t.BlockHeight > 0);
                if (_pendingImported.Count >= 8192)
                {
                    _pendingImported.Clear();
                    FailObservers(new InvalidOperationException("imported transaction observation backlog exceeded 8192 events"));
                }
                _pendingImported.Add(observed);
            }
        }
        PublishCommitted(tip, anchor.ToString());
        return Copy(snapshot);
    }

    private static List<WalletTransactionEventArgs> DescribeChanges(
        IReadOnlyList<ImportedWatchTransaction> previous, IReadOnlyList<ImportedWatchTransaction> current)
    {
        static string Identity(ImportedWatchTransaction t) => $"{t.Transaction.GetHash()}:{t.Height}:{t.BlockHash}";
        var before = previous.Select(Identity).ToHashSet(StringComparer.Ordinal);
        var after = current.Select(Identity).ToHashSet(StringComparer.Ordinal);
        var changes = new List<WalletTransactionEventArgs>();
        foreach (var transaction in previous.Where(t => !after.Contains(Identity(t))))
            changes.Add(Describe(transaction, false));
        foreach (var transaction in current.Where(t => !before.Contains(Identity(t))))
            changes.Add(Describe(transaction, true));
        return changes;
    }

    private static WalletTransactionEventArgs Describe(ImportedWatchTransaction observed, bool confirmed)
    {
        var tx = observed.Transaction;
        var spent = observed.SpentOutputs.ToHashSet();
        var inputs = Enumerable.Range(0, tx.Inputs.Count).Where(i => spent.Contains(tx.Inputs[i].PrevOut))
            .Select(i => (uint)i).ToArray();
        var fee = inputs.Length == tx.Inputs.Count && !tx.IsCoinBase
            ? Math.Max(0, observed.OurOutputs.Sum(i => tx.Outputs[(int)i].Value.Satoshi)
                          - observed.Amount - tx.Outputs.Sum(o => o.Value.Satoshi)) : 0;
        return new WalletTransactionEventArgs(tx.ToHex(), observed.Amount, fee,
            confirmed ? observed.Height : 0, confirmed ? observed.BlockHash.ToString() : "",
            observed.Time, "", observed.OurOutputs, inputs, tx.GetHash().ToString(), isReorg: !confirmed);
    }

    private void Publish(WalletTransactionEventArgs observed)
    {
        lock (_observationsLock)
        {
            var state = (observed.BlockHeight, observed.BlockHash);
            if (_published.TryGetValue(observed.TxHash, out var previous) && previous == state) return;
            if (!_published.ContainsKey(observed.TxHash)) _publishedOrder.Enqueue(observed.TxHash);
            _published[observed.TxHash] = state;
            while (_publishedOrder.Count > 8192) _published.Remove(_publishedOrder.Dequeue());
        }
        if (OnTransactionObserved is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<WalletTransactionEventArgs>>())
        {
            try { handler(this, observed); }
            catch (Exception e) { _logger.LogWarning(e, "Imported transaction subscriber failed"); }
        }
    }

    private static bool Apply(IndexedTransaction indexed, Dictionary<string, ImportedTapscript> tracked,
                               Dictionary<OutPoint, ImportedWatchOutput> outputs, List<ImportedWatchTransaction> history)
    {
        var tx = indexed.Transaction;
        long amount = 0;
        var ours = new List<uint>();
        var spent = new List<OutPoint>();
        foreach (var input in tx.Inputs)
        {
            if (!outputs.Remove(input.PrevOut, out var output)) continue;
            amount -= output.Output.Value.Satoshi;
            spent.Add(input.PrevOut);
        }
        for (var i = 0; i < tx.Outputs.Count; i++)
        {
            var output = tx.Outputs[i];
            if (!tracked.TryGetValue(Convert.ToHexString(output.ScriptPubKey.ToBytes()), out var script)
             || indexed.Height < script.CreatedHeight) continue;
            var outpoint = new OutPoint(tx.GetHash(), (uint)i);
            outputs[outpoint] = new ImportedWatchOutput(outpoint, output, indexed.Height);
            amount += output.Value.Satoshi;
            ours.Add((uint)i);
        }
        if (ours.Count == 0 && spent.Count == 0) return false;
        history.Add(new ImportedWatchTransaction(tx, indexed.Height, indexed.BlockHash, indexed.Time, amount, ours, spent));
        return true;
    }

    private static byte[] WriteHistory(List<IndexedTransaction> history, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1); // durable encoding version
        writer.Write(history.Count);
        foreach (var entry in history)
        {
            ct.ThrowIfCancellationRequested();
            writer.Write(entry.Height);
            writer.Write(entry.BlockHash.ToBytes());
            writer.Write(entry.Time.ToUnixTimeSeconds());
            var raw = entry.Transaction.ToBytes();
            writer.Write(raw.Length);
            writer.Write(raw);
        }
        return stream.ToArray();
    }

    private static List<IndexedTransaction> ReadHistory(byte[] raw, CancellationToken ct)
    {
        using var stream = new MemoryStream(raw, writable: false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 1) throw new InvalidOperationException("unsupported imported history index version");
        var count = reader.ReadInt32();
        var entries = new List<IndexedTransaction>();
        if (count < 0 || count > raw.Length / 48) throw new InvalidDataException("invalid imported history count");
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var height = reader.ReadUInt32();
            var hash = new uint256(reader.ReadBytes(32));
            var time = DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64());
            var length = reader.ReadInt32();
            if (length < 0 || length > stream.Length - stream.Position) throw new InvalidDataException("invalid imported transaction length");
            entries.Add(new IndexedTransaction(height, hash, time, Transaction.Load(reader.ReadBytes(length), Network.Main)));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("trailing imported history bytes");
        return entries;
    }

    private void OnWalletTransactionsProcessing(object? sender, EventArgs args)
    {
        lock (_observationsLock) _completedTip = null;
    }

    private void OnWalletTransactionsProcessed(object? sender, NewBlockEventArgs args)
    {
        lock (_observationsLock)
            _completedTip = (args.Height, new uint256((byte[])args.BlockHash).ToString());
        ScheduleUpdate();
    }

    private void OnCanonicalTransaction(object? sender, WalletTransactionEventArgs observed)
    {
        // Mempool observations are operational; confirmations and rewinds wait for the committed index join.
        if (observed.BlockHeight == 0 && observed.BlockHash.Length == 0)
        {
            // Rewind notices arrive before the processed marker; a previously confirmed tx must join the diff.
            lock (_observationsLock)
            {
                if (observed.IsReorg || _pendingCanonical.Any(t => t.TxHash == observed.TxHash && t.BlockHeight > 0)
                    || (_published.TryGetValue(observed.TxHash, out var previous) && previous.Height > 0))
                {
                    // A rewind supersedes a confirmation not yet published by a lagging index worker.
                    _pendingCanonical.RemoveAll(t => t.TxHash == observed.TxHash && t.BlockHeight > 0);
                    QueueCanonical(observed);
                    return;
                }
            }
            Publish(Enrich(observed));
            return;
        }
        lock (_observationsLock)
            QueueCanonical(observed);
    }

    private void QueueCanonical(WalletTransactionEventArgs observed)
    {
        if (_pendingCanonical.Count >= 8192)
        {
            _pendingCanonical.Clear();
            FailObservers(new InvalidOperationException("imported transaction observation backlog exceeded 8192 events"));
        }
        _pendingCanonical.Add(observed);
    }

    private void FailObservers(Exception exception)
    {
        if (OnObservationFailed is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<Exception>>())
        {
            try { handler(exception); }
            catch (Exception e) { _logger.LogWarning(e, "Imported transaction failure subscriber failed"); }
        }
    }

    private void PublishCommitted(uint tip, string? anchor)
    {
        lock (_observationsLock)
        {
            // RPC reads may commit the index between the monitor's block notice and its canonical wallet
            // callbacks. Retain those changes until the exact height/hash completion marker arrives.
            if (_completedTip is not { } completed || completed.Height != tip
                || (anchor is not null && completed.Hash != anchor)) return;
            var canonical = _pendingCanonical.Where(t => t.BlockHeight <= tip).ToList();
            _pendingCanonical.RemoveAll(t => t.BlockHeight <= tip);
            var imported = _pendingImported.ToArray();
            _pendingImported.Clear();
            static (string, uint, string) Key(WalletTransactionEventArgs t) => (t.TxHash, t.BlockHeight, t.BlockHash);
            var canonicalStates = canonical.Select(Key).ToHashSet();
            var joined = canonical.Select(Enrich).Concat(imported.Where(t => !canonicalStates.Contains(Key(t))));
            foreach (var observed in joined.OrderBy(t => t.BlockHeight)) Publish(observed);
        }
    }

    private WalletTransactionEventArgs Enrich(WalletTransactionEventArgs canonical)
    {
        var tx = Transaction.Parse(canonical.RawTransactionHex, Network.Main);
        var outputs = canonical.OurOutputs.ToHashSet();
        var inputs = canonical.OurInputs.ToHashSet();
        var amount = canonical.AmountSat;
        lock (_observationsLock)
        {
            for (var i = 0; i < tx.Outputs.Count; i++)
                if (_importedScripts.Contains(Convert.ToHexString(tx.Outputs[i].ScriptPubKey.ToBytes()))
                    && outputs.Add((uint)i))
                    amount += tx.Outputs[i].Value.Satoshi;
            for (var i = 0; i < tx.Inputs.Count; i++)
                if (_importedOutputs.TryGetValue(tx.Inputs[i].PrevOut, out var value) && inputs.Add((uint)i))
                    amount -= value;
        }
        var fee = inputs.Count == tx.Inputs.Count && !tx.IsCoinBase
            ? Math.Max(0, outputs.Sum(i => tx.Outputs[(int)i].Value.Satoshi) - amount
                          - tx.Outputs.Sum(o => o.Value.Satoshi)) : canonical.FeeSat;
        return new WalletTransactionEventArgs(canonical.RawTransactionHex, amount, fee, canonical.BlockHeight,
            canonical.BlockHash, canonical.Timestamp, canonical.Label, outputs.Order().ToArray(), inputs.Order().ToArray(),
            canonical.TxHash, canonical.IsReorg);
    }

    private void ScheduleUpdate()
    {
        if (_lifetime.IsCancellationRequested) return;
        Interlocked.Exchange(ref _requested, 1);
        if (Interlocked.CompareExchange(ref _worker, 1, 0) == 0)
            _ = Task.Run(UpdateAsync);
    }

    private async Task UpdateAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested && Interlocked.Exchange(ref _requested, 0) != 0)
            {
                try { await SnapshotAsync(_lifetime); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Imported history update failed; the next RPC or block will retry");
                    FailObservers(e);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _worker, 0);
            if (Volatile.Read(ref _requested) != 0) ScheduleUpdate();
        }
    }

    // RPC mappers receive their own mutable NBitcoin objects; a caller cannot poison a later cache hit.
    private static ImportedWatchSnapshot Copy(ImportedWatchSnapshot snapshot) => new(snapshot.Tip,
        snapshot.Outputs.Select(o => o with
        {
            Outpoint = new OutPoint(new uint256(o.Outpoint.Hash.ToBytes()), o.Outpoint.N),
            Output = new TxOut(o.Output.Value, new Script(o.Output.ScriptPubKey.ToBytes()))
        }).ToArray(), snapshot.Transactions.Select(t => t with
        {
            Transaction = t.Transaction.Clone(),
            BlockHash = new uint256(t.BlockHash.ToBytes()),
            OurOutputs = t.OurOutputs.ToArray(),
            SpentOutputs = t.SpentOutputs.Select(o => new OutPoint(new uint256(o.Hash.ToBytes()), o.N)).ToArray()
        }).ToArray());

    private sealed record CachedWatch(uint256 Hash, string ScriptSet, ImportedWatchSnapshot Snapshot);
    private sealed record IndexedTransaction(uint Height, uint256 BlockHash, DateTimeOffset Time, Transaction Transaction);
}

public sealed record ImportedWatchOutput(OutPoint Outpoint, TxOut Output, uint Height);
public sealed record ImportedWatchTransaction(Transaction Transaction, uint Height, uint256 BlockHash,
                                               DateTimeOffset Time, long Amount, IReadOnlyList<uint> OurOutputs,
                                               IReadOnlyList<OutPoint> SpentOutputs);
public sealed record ImportedWatchSnapshot(uint Tip, IReadOnlyList<ImportedWatchOutput> Outputs,
                                           IReadOnlyList<ImportedWatchTransaction> Transactions);