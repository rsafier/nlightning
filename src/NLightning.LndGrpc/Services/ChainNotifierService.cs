using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;

namespace NLightning.LndGrpc.Services;

using Chainrpc;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// Memory-only registrations over the monitor's committed tip. Each stream reconciles active block hashes before
/// dispatch, including historical matches and same-height reorgs. Polling also detects a halted monitor when no
/// block event is raised. No txindex is required: transactions come from blocks.
/// </summary>
public sealed class ChainNotifierService : ChainNotifier.ChainNotifierBase
{
    private readonly IBitcoinChainService _chain;
    private readonly IBlockchainMonitor _monitor;
    private readonly LndGrpcOptions _options;
    private readonly TimeProvider _clock;
    private int _registrations;

    public ChainNotifierService(IBitcoinChainService chain, IBlockchainMonitor monitor,
                                 IOptions<LndGrpcOptions> options, TimeProvider? clock = null)
    {
        _chain = chain;
        _monitor = monitor;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    public override async Task RegisterBlockEpochNtfn(BlockEpoch request,
                                                       IServerStreamWriter<BlockEpoch> responseStream,
                                                       ServerCallContext context)
    {
        Enter();
        try
        {
            var ct = context.CancellationToken;
            var tip = await TipAsync(ct);
            var history = new SortedDictionary<uint, uint256>();
            var start = tip;
            if (!request.Hash.IsEmpty)
            {
                CheckHash(request.Hash);
                if (request.Height > tip)
                    throw Error(StatusCode.InvalidArgument, "block epoch is above the processed tip");
                var hash = new uint256(request.Hash.ToByteArray());
                var ancestor = request.Height;
                var depth = 0;
                while (await Hash(ancestor, ct) != hash)
                {
                    if (ancestor == 0 || ++depth > 100)
                        throw Error(StatusCode.Unavailable, "block epoch is beyond the notification reorg window");
                    var stale = await _chain.GetBlockAsync(hash).WaitAsync(ct)
                             ?? throw Error(StatusCode.FailedPrecondition, "client block epoch is unknown");
                    hash = stale.Header.HashPrevBlock;
                    ancestor--;
                }
                history.Add(ancestor, hash);
                start = ancestor + 1;
                CheckRange(start, tip);
            }

            // Keep the preceding headers too: an immediate same-height fork must produce replacement epochs.
            var anchor = history.Count == 0 ? tip : history.First().Key;
            for (var height = anchor > 100 ? anchor - 100 : 0; height < anchor; height++)
            {
                if (await Hash(height, ct) is { } hash) history[height] = hash;
                else { start = Math.Min(start, height); break; }
            }

            while (true)
            {
                tip = await TipAsync(ct);
                // Roll back to the common ancestor. Keep a bounded header window, never silently skip a deep fork.
                while (history.Count > 0)
                {
                    var last = history.Last();
                    if (last.Key <= tip && await Hash(last.Key, ct) == last.Value)
                        break;
                    history.Remove(last.Key);
                    start = last.Key;
                    if (history.Count == 0 && start > 0)
                        throw Error(StatusCode.Unavailable, "chain reorg exceeds the notification header window; re-register");
                }
                for (var height = start; height <= tip; height++)
                {
                    ct.ThrowIfCancellationRequested();
                    var hash = await Hash(height, ct);
                    if (hash is null) break; // A disconnect raced the committed-tip check; retry the height.
                    await responseStream.WriteAsync(new BlockEpoch
                    {
                        Height = height,
                        Hash = ByteString.CopyFrom(hash.ToBytes())
                    }, ct);
                    history[height] = hash;
                    if (history.Count > 101)
                        history.Remove(history.First().Key);
                    start = height + 1;
                }
                await Pause(ct);
            }
        }
        finally { Interlocked.Decrement(ref _registrations); }
    }

    public override async Task RegisterConfirmationsNtfn(ConfRequest request,
                                                          IServerStreamWriter<ConfEvent> responseStream,
                                                          ServerCallContext context)
    {
        CheckScript(request.Script);
        CheckHash(request.Txid, true);
        if (request.NumConfs is 0 or > 2016)
            throw Error(StatusCode.InvalidArgument, "num_confs must be between 1 and 2016");
        var txid = IsZero(request.Txid) ? null : new uint256(request.Txid.ToByteArray());
        Enter();
        try
        {
            var ct = context.CancellationToken;
            var scan = request.HeightHint;
            ConfDetails? match = null;
            var delivered = false;
            uint? lastHeight = null;
            uint256? lastHash = null;
            while (true)
            {
                var tip = await TipAsync(ct);
                var fork = lastHeight is { } previous && (previous > tip || await Hash(previous, ct) != lastHash);
                var lost = match is not null && (match.BlockHeight > tip
                    || await Hash(match.BlockHeight, ct) != new uint256(match.BlockHash.ToByteArray())
                    || delivered && tip < (ulong)match.BlockHeight + request.NumConfs - 1);
                if (lost)
                {
                    if (delivered) await responseStream.WriteAsync(new ConfEvent { Reorg = new Reorg() }, ct);
                    match = null;
                    delivered = false;
                    scan = request.HeightHint;
                }
                else if (fork && match is null)
                    scan = request.HeightHint;
                CheckRange(scan, tip);
                for (; match is null && scan <= tip; scan++)
                {
                    var block = await Block(scan, ct);
                    if (block is null) break;
                    for (var i = 0; i < block.Transactions.Count; i++)
                    {
                        var tx = block.Transactions[i];
                        if (txid is not null && tx.GetHash() != txid)
                            continue;
                        if (!tx.Outputs.Any(o => o.ScriptPubKey.ToBytes().AsSpan().SequenceEqual(request.Script.Span)))
                            continue;
                        match = new ConfDetails
                        {
                            RawTx = ByteString.CopyFrom(tx.ToBytes()),
                            BlockHash = ByteString.CopyFrom(block.GetHash().ToBytes()),

                            BlockHeight = scan,
                            TxIndex = (uint)i,
                            RawBlock = request.IncludeBlock ? ByteString.CopyFrom(block.ToBytes()) : ByteString.Empty
                        };
                        break;
                    }
                }
                if (match is not null && !delivered && tip >= (ulong)match.BlockHeight + request.NumConfs - 1)
                {
                    if (await Hash(match.BlockHeight, ct) != new uint256(match.BlockHash.ToByteArray()))
                    {
                        scan = request.HeightHint;
                        match = null;
                        continue;
                    }
                    await responseStream.WriteAsync(new ConfEvent { Conf = match }, ct);
                    delivered = true;
                }
                lastHeight = tip;
                lastHash = await Hash(tip, ct);
                await Pause(ct);
            }
        }
        finally { Interlocked.Decrement(ref _registrations); }
    }

    public override async Task RegisterSpendNtfn(SpendRequest request, IServerStreamWriter<SpendEvent> responseStream,
                                                  ServerCallContext context)
    {
        CheckScript(request.Script);
        CheckHash(request.Outpoint?.Hash ?? ByteString.Empty, true);
        var outpoint = request.Outpoint is null || IsZero(request.Outpoint.Hash) ? null
            : new NBitcoin.OutPoint(new uint256(request.Outpoint.Hash.ToByteArray()), request.Outpoint.Index);
        var script = new Script(request.Script.ToByteArray());
        if (outpoint is null && script.IsScriptType(ScriptType.Taproot))
            throw Error(StatusCode.InvalidArgument, "taproot spend notification requires an outpoint");
        Enter();
        try
        {
            var ct = context.CancellationToken;
            var scan = request.HeightHint;
            SpendDetails? match = null;
            uint256? matchHash = null;
            uint? lastHeight = null;
            uint256? lastHash = null;
            while (true)
            {
                var tip = await TipAsync(ct);
                var fork = lastHeight is { } previous && (previous > tip || await Hash(previous, ct) != lastHash);
                if (match is not null && (match.SpendingHeight > tip || await Hash(match.SpendingHeight, ct) != matchHash))
                {
                    await responseStream.WriteAsync(new SpendEvent { Reorg = new Reorg() }, ct);
                    match = null;
                    scan = request.HeightHint;
                }
                else if (fork && match is null)
                    scan = request.HeightHint;
                CheckRange(scan, tip);
                var chainChanged = false;
                for (; match is null && scan <= tip; scan++)
                {
                    var block = await Block(scan, ct);
                    if (block is null) break;
                    foreach (var tx in block.Transactions)
                    {
                        for (var i = 0; i < tx.Inputs.Count; i++)
                        {
                            if (outpoint is not null ? tx.Inputs[i].PrevOut != outpoint : !SpendsScript(tx.Inputs[i], script))
                                continue;
                            if (await Hash(scan, ct) != block.GetHash())
                            {
                                scan = request.HeightHint;
                                chainChanged = true;
                                break;
                            }
                            matchHash = block.GetHash();
                            match = new SpendDetails
                            {
                                SpendingOutpoint = new Outpoint
                                {
                                    Hash = ByteString.CopyFrom(tx.Inputs[i].PrevOut.Hash.ToBytes()),
                                    Index = tx.Inputs[i].PrevOut.N
                                },
                                RawSpendingTx = ByteString.CopyFrom(tx.ToBytes()),
                                SpendingTxHash = ByteString.CopyFrom(tx.GetHash().ToBytes()),
                                SpendingInputIndex = (uint)i,
                                SpendingHeight = scan
                            };
                            await responseStream.WriteAsync(new SpendEvent { Spend = match }, ct);
                            break;
                        }
                        if (match is not null || chainChanged)
                            break;
                    }
                    if (chainChanged) break;
                }
                lastHeight = tip;
                lastHash = await Hash(tip, ct);
                await Pause(ct);
            }
        }
        finally { Interlocked.Decrement(ref _registrations); }
    }

    // LND permits script-only registrations for v0/legacy outputs. Their revealed redeem script/public key
    // identifies the spent script even when the funding transaction predates height_hint; taproot needs an outpoint.
    private static bool SpendsScript(TxIn input, Script script)
    {
        var witness = input.WitScript.Pushes.ToArray();
        if (witness.Length > 0)
        {
            var last = witness[^1];
            if (new Script(last).WitHash.ScriptPubKey == script) return true;
            if (last.Length is 33 or 65)
            {
                try { if (new PubKey(last).WitHash.ScriptPubKey == script) return true; }
                catch (FormatException) { }
            }
        }
        var pushes = input.ScriptSig.ToOps().Where(op => op.PushData is not null).Select(op => op.PushData).ToArray();
        if (pushes.Length == 0) return false;
        var redeem = new Script(pushes[^1]);
        if (redeem.Hash.ScriptPubKey == script) return true;
        if (pushes[^1].Length is 33 or 65)
        {
            try { return new PubKey(pushes[^1]).Hash.ScriptPubKey == script; }
            catch (FormatException) { }
        }
        return false;
    }

    private void Enter()
    {
        Tip();
        if (Interlocked.Increment(ref _registrations) <= _options.MaxChainNotifierRegistrations)
            return;
        Interlocked.Decrement(ref _registrations);
        throw Error(StatusCode.ResourceExhausted, "too many chain notification registrations");
    }

    private uint Tip()
    {
        if (_monitor.IsChainProcessingHalted)
            throw Error(StatusCode.Unavailable, "chain processing halted: " + _monitor.ChainProcessingHaltReason);
        if (_monitor.LastProcessedBlockHeight == 0)
            throw Error(StatusCode.Unavailable, "chain notifier RPC is still in the process of starting");
        return _monitor.LastProcessedBlockHeight;
    }

    private void CheckRange(uint start, uint tip)
    {
        if (start <= tip && (ulong)tip - start + 1 > (ulong)_options.MaxChainNotifierScanBlocks)
            throw Error(StatusCode.ResourceExhausted, "height hint exceeds MaxChainNotifierScanBlocks");
    }

    private async Task<uint> TipAsync(CancellationToken ct)
    {
        while (true)
        {
            var tip = Tip();
            if (tip <= await _chain.GetCurrentBlockHeightAsync().WaitAsync(ct)
                && await Hash(tip, ct) is not null)
                return tip;
            // Bitcoin Core can disconnect before the monitor commits the replacement branch.
            await Pause(ct);
        }
    }

    private async Task<uint256?> Hash(uint height, CancellationToken ct)
    {
        try { return await _chain.GetBlockHashAsync(height).WaitAsync(ct); }
        catch (NBitcoin.RPC.RPCException e) when (e.RPCCode == RPCErrorCode.RPC_INVALID_PARAMETER) { return null; }
    }

    private async Task<Block?> Block(uint height, CancellationToken ct)
    {
        try
        {
            return await _chain.GetBlockAsync(height).WaitAsync(ct)
                ?? throw Error(StatusCode.FailedPrecondition, $"historical block {height} unavailable (possibly pruned)");
        }
        catch (NBitcoin.RPC.RPCException e) when (e.RPCCode == RPCErrorCode.RPC_INVALID_PARAMETER) { return null; }
        catch (NBitcoin.RPC.RPCException e) when (e.Message.Contains("pruned", StringComparison.OrdinalIgnoreCase))
        { throw Error(StatusCode.FailedPrecondition, $"historical block {height} unavailable (pruned)"); }
    }

    private Task Pause(CancellationToken ct) => Task.Delay(TimeSpan.FromMilliseconds(500), _clock, ct);

    private static bool IsZero(ByteString bytes) => bytes.IsEmpty || bytes.Span.IndexOfAnyExcept((byte)0) < 0;

    private static void CheckHash(ByteString bytes, bool optional = false)
    {
        if (bytes.Length != 32 && !(optional && bytes.IsEmpty))
            throw Error(StatusCode.InvalidArgument, "hash must be 32 bytes in wire byte order");
    }

    private static void CheckScript(ByteString script)
    {
        if (script.Length is 0 or > 10_000)
            throw Error(StatusCode.InvalidArgument, "script must contain 1 to 10000 bytes");
    }

    private static RpcException Error(StatusCode code, string message) => new(new Status(code, message));
}