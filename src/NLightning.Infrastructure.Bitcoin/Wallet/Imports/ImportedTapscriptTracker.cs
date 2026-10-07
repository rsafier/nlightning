using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet.Imports;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Onchain.Events;
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

    public ImportedTapscriptTracker(IServiceScopeFactory scopes, IBitcoinChainService chain,
                                    IBlockchainMonitor monitor, ILogger<ImportedTapscriptTracker>? logger = null)
    {
        _scopes = scopes;
        _chain = chain;
        _monitor = monitor;
        _logger = logger ?? NullLogger<ImportedTapscriptTracker>.Instance;
        _lifetime = _stop.Token;
        monitor.OnNewBlockDetected += OnNewBlock;
        monitor.OnBlockDisconnected += OnBlockDisconnected;
    }

    public void Dispose()
    {
        _monitor.OnNewBlockDetected -= OnNewBlock;
        _monitor.OnBlockDisconnected -= OnBlockDisconnected;
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
            return new ImportedWatchSnapshot(tip, [], []);
        }
        ct.ThrowIfCancellationRequested();
        var anchor = await _chain.GetBlockHashAsync(tip).WaitAsync(ct);
        var scriptSet = string.Join("|", scripts.OrderBy(s => Convert.ToHexString(s.Script), StringComparer.Ordinal)
            .Select(s => Convert.ToHexString(s.Script) + ":" + s.CreatedHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (_cached is { } cached && cached.Snapshot.Tip == tip && cached.Hash == anchor && cached.ScriptSet == scriptSet)
            return Copy(cached.Snapshot);

        var start = scripts.Min(s => s.CreatedHeight);
        var saved = await uow.ImportedTapscriptDbRepository.GetIndexAsync();
        var transactions = saved is not null && saved.ScriptSet == scriptSet ? ReadHistory(saved.History, ct) : [];
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
        await uow.ImportedTapscriptDbRepository.SetIndexAsync(new ImportedWatchIndex(tip, anchor.ToBytes(), scriptSet, WriteHistory(transactions, ct)));
        ct.ThrowIfCancellationRequested();
        await uow.SaveChangesAsync();
        var snapshot = new ImportedWatchSnapshot(tip, outputs.Values.ToList(), history);
        _cached = new CachedWatch(anchor, scriptSet, Copy(snapshot));
        return snapshot;
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

    private void OnNewBlock(object? sender, NewBlockEventArgs args) => ScheduleUpdate();
    private void OnBlockDisconnected(object? sender, BlockDisconnectedEventArgs args) => ScheduleUpdate();

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
                catch (Exception e) { _logger.LogWarning(e, "Imported history update failed; the next RPC or block will retry"); }
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