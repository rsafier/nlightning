using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet.Imports;

using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Persistence.Interfaces;
using Interfaces;

/// <summary>
/// Confirmed-only watch history reconstructed from active blocks (no txindex). Kept outside the wallet's UTXO
/// repository so selection, wallet signing and accounting cannot mistake a two-party deposit for spendable money.
/// Active-block reconstruction also removes disconnected deposits and reinstates disconnected spends after restart.
/// </summary>
public sealed class ImportedTapscriptTracker(IServiceScopeFactory scopes, IBitcoinChainService chain, IBlockchainMonitor monitor) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private CachedWatch? _cached;

    public void Dispose() { _cached = null; _gate.Dispose(); }

    public async Task ImportAsync(ImportedTapscript script, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
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
        if (monitor.IsChainProcessingHalted)
            throw new InvalidOperationException("chain processing halted");
        await using var scope = scopes.CreateAsyncScope();
        var scripts = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ImportedTapscriptDbRepository.ListAsync();
        var tip = monitor.LastProcessedBlockHeight;
        var outputs = new Dictionary<OutPoint, ImportedWatchOutput>();
        var history = new List<ImportedWatchTransaction>();
        if (scripts.Count == 0)
        {
            _cached = null;
            return new ImportedWatchSnapshot(tip, outputs.Values.ToList(), history);
        }
        ct.ThrowIfCancellationRequested();
        var anchor = await chain.GetBlockHashAsync(tip).WaitAsync(ct);
        var scriptSet = string.Join("|", scripts.OrderBy(s => Convert.ToHexString(s.Script), StringComparer.Ordinal)
            .Select(s => Convert.ToHexString(s.Script) + ":" + s.CreatedHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (_cached is { } cached && cached.Snapshot.Tip == tip && cached.Hash == anchor && cached.ScriptSet == scriptSet)
            return Copy(cached.Snapshot);
        var start = scripts.Min(s => s.CreatedHeight);
        if ((ulong)tip - Math.Min(start, tip) > 1_000_000)
            throw new InvalidOperationException("imported tapscript scan exceeds one million blocks");
        var tracked = scripts.ToDictionary(s => Convert.ToHexString(s.Script), StringComparer.Ordinal);
        uint256? previous = null;
        for (var height = start; height <= tip; height++)
        {
            var block = await chain.GetBlockAsync(height).WaitAsync(ct)
                     ?? throw new InvalidOperationException($"imported tapscript historical block {height} unavailable (possibly pruned)");
            if (previous is not null && block.Header.HashPrevBlock != previous)
                throw new InvalidOperationException("chain changed during imported tapscript scan; retry");
            previous = block.GetHash();
            foreach (var tx in block.Transactions)
            {
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
                        || height < script.CreatedHeight) continue;
                    var outpoint = new OutPoint(tx.GetHash(), (uint)i);
                    outputs[outpoint] = new ImportedWatchOutput(outpoint, output, height);
                    amount += output.Value.Satoshi;
                    ours.Add((uint)i);
                }
                if (ours.Count > 0 || spent.Count > 0)
                    history.Add(new ImportedWatchTransaction(tx, height, block.GetHash(), block.Header.BlockTime, amount, ours, spent));
            }
        }
        if (previous is not null && (previous != anchor || await chain.GetBlockHashAsync(tip).WaitAsync(ct) != previous))
            throw new InvalidOperationException("chain changed during imported tapscript scan; retry");
        var snapshot = new ImportedWatchSnapshot(tip, outputs.Values.ToList(), history);
        _cached = new CachedWatch(anchor, scriptSet, Copy(snapshot));
        return snapshot;
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
}

public sealed record ImportedWatchOutput(OutPoint Outpoint, TxOut Output, uint Height);
public sealed record ImportedWatchTransaction(Transaction Transaction, uint Height, uint256 BlockHash,
                                               DateTimeOffset Time, long Amount, IReadOnlyList<uint> OurOutputs,
                                               IReadOnlyList<OutPoint> SpentOutputs);
public sealed record ImportedWatchSnapshot(uint Tip, IReadOnlyList<ImportedWatchOutput> Outputs,
                                           IReadOnlyList<ImportedWatchTransaction> Transactions);