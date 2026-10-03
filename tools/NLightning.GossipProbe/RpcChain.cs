using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Reflection;
using NBitcoin;
using NBitcoin.RPC;

namespace NLightning.GossipProbe;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Models;
using Domain.Money;
using Domain.Onchain.Events;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The RPC settings of the owner's bitcoind, read from a <c>KEY=VALUE</c> file (<c>MAINNET_RPC_URL</c>,
/// <c>MAINNET_RPC_USER</c>, <c>MAINNET_RPC_PASSWORD</c>). The password only ever goes to the node's in-memory
/// configuration and the RPC client: never to the console, a log or a file.
/// </summary>
public sealed class RpcSettings
{
    public required string Url { get; init; }
    public required string User { get; init; }
    public required string Password { get; init; }

    public static RpcSettings Load(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            if (trimmed.StartsWith("export ", StringComparison.Ordinal))
                trimmed = trimmed[7..];

            var equals = trimmed.IndexOf('=');
            if (equals <= 0)
                continue;

            values[trimmed[..equals].Trim()] = trimmed[(equals + 1)..].Trim().Trim('"', '\'');
        }

        string Get(string key) => values.TryGetValue(key, out var value) && value.Length > 0
                                      ? value
                                      : throw new InvalidOperationException($"{path} has no {key}");

        return new RpcSettings
        {
            Url = Get("MAINNET_RPC_URL"),
            User = Get("MAINNET_RPC_USER"),
            Password = Get("MAINNET_RPC_PASSWORD")
        };
    }

    /// <summary>A read-only client of the probe's own (getblockchaininfo only; the node uses its own client).</summary>
    public RPCClient CreateClient() =>
        new(new RPCCredentialString { UserPassword = new NetworkCredential(User, Password) }, Url, Network.Main);
}

/// <summary>Latencies of one kind of call: count, failures and a percentile set (kept in memory, bounded).</summary>
public sealed class LatencyRecorder
{
    private const int MaxSamples = 500_000;
    private readonly Lock _gate = new();
    private readonly List<double> _milliseconds = [];
    private long _count;
    private long _failures;

    public long Count => Interlocked.Read(ref _count);
    public long Failures => Interlocked.Read(ref _failures);

    public void Record(TimeSpan elapsed, bool failed)
    {
        Interlocked.Increment(ref _count);
        if (failed)
            Interlocked.Increment(ref _failures);
        lock (_gate)
        {
            if (_milliseconds.Count < MaxSamples)
                _milliseconds.Add(elapsed.TotalMilliseconds);
        }
    }

    public Dictionary<string, object> Describe()
    {
        List<double> sorted;
        lock (_gate)
            sorted = _milliseconds.Order().ToList();

        double P(double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
        return new Dictionary<string, object>
        {
            ["count"] = Count,
            ["failures"] = Failures,
            ["mean_ms"] = sorted.Count == 0 ? 0 : Math.Round(sorted.Average(), 2),
            ["p50_ms"] = Math.Round(P(0.50), 2),
            ["p90_ms"] = Math.Round(P(0.90), 2),
            ["p99_ms"] = Math.Round(P(0.99), 2),
            ["max_ms"] = sorted.Count == 0 ? 0 : Math.Round(sorted[^1], 2)
        };
    }
}

/// <summary>
/// Counts and times the product's <see cref="IBitcoinChainService"/> calls by method (the probe decorates the
/// daemon's own <c>BitcoinChainService</c>; nothing is reimplemented). One method is one or two RPCs:
/// <c>GetBlockTxIdsAsync</c> = getblockhash + getblock verbosity 1, <c>GetUnspentOutputAsync</c> /
/// <c>GetConfirmedUnspentOutputAsync</c> = gettxout (+ getblockcount when the output exists). The real HTTP request
/// count is <see cref="HttpRequestCollector"/>'s.
/// </summary>
public sealed class CountingChainService(IBitcoinChainService inner) : IBitcoinChainService
{
    public ConcurrentDictionary<string, LatencyRecorder> Calls { get; } = new(StringComparer.Ordinal);

    public Task<uint256> SendTransactionAsync(Transaction transaction) =>
        throw new InvalidOperationException("The gossip probe never publishes a transaction");

    public Task<Transaction?> GetTransactionAsync(uint256 txId) => Time(nameof(GetTransactionAsync),
                                                                        () => inner.GetTransactionAsync(txId));

    public Task<uint> GetCurrentBlockHeightAsync() => Time(nameof(GetCurrentBlockHeightAsync),
                                                           inner.GetCurrentBlockHeightAsync);

    public Task<Block?> GetBlockAsync(uint height) => Time("GetBlockAsync(height)", () => inner.GetBlockAsync(height));

    public Task<uint256> GetBlockHashAsync(uint height) => Time(nameof(GetBlockHashAsync),
                                                                () => inner.GetBlockHashAsync(height));

    public Task<uint> GetTransactionConfirmationsAsync(uint256 txId) =>
        Time(nameof(GetTransactionConfirmationsAsync), () => inner.GetTransactionConfirmationsAsync(txId));

    public Task<Block?> GetBlockAsync(uint256 blockHash) => Time("GetBlockAsync(hash)",
                                                                 () => inner.GetBlockAsync(blockHash));

    public Task<(TxOut Output, uint Height)?> GetUnspentOutputAsync(OutPoint outPoint) =>
        Time(nameof(GetUnspentOutputAsync), () => inner.GetUnspentOutputAsync(outPoint));

    public Task<(TxOut Output, uint Height)?> GetConfirmedUnspentOutputAsync(OutPoint outPoint) =>
        Time(nameof(GetConfirmedUnspentOutputAsync), () => inner.GetConfirmedUnspentOutputAsync(outPoint));

    public Task<(uint256 BlockHash, IReadOnlyList<uint256> TxIds)?> GetBlockTxIdsAsync(uint height) =>
        Time(nameof(GetBlockTxIdsAsync), () => inner.GetBlockTxIdsAsync(height));

    private async Task<T> Time<T>(string method, Func<Task<T>> call)
    {
        var watch = Stopwatch.StartNew();
        var failed = false;
        try
        {
            return await call();
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            Calls.GetOrAdd(method, _ => new LatencyRecorder()).Record(watch.Elapsed, failed);
        }
    }
}

/// <summary>
/// Every HTTP request of the process (<c>System.Net.Http</c>'s <c>http.client.request.duration</c>): in RPC mode
/// these are the bitcoind RPCs, the peers being plain TCP. Counted per minute for the RPC load.
/// </summary>
public sealed class HttpRequestCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<int, long> _perMinute = new();

    public HttpRequestCollector()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument is { Name: "http.client.request.duration", Meter.Name: "System.Net.Http" })
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((_, seconds, tags, _) =>
        {
            var failed = false;
            foreach (var tag in tags)
            {
                if (tag.Key == "error.type"
                 || (tag.Key == "http.response.status_code" && tag.Value is int code && code >= 400))
                    failed = true;
            }

            Requests.Record(TimeSpan.FromSeconds(seconds), failed);
            _perMinute.AddOrUpdate((int)_clock.Elapsed.TotalMinutes, 1, (_, c) => c + 1);
        });
        _listener.Start();
    }

    public LatencyRecorder Requests { get; } = new();

    /// <summary>Requests per elapsed minute (minute index → count).</summary>
    public SortedDictionary<int, long> PerMinute => new(_perMinute);

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// The product <see cref="IFundingOutputLookup"/> (<c>FundingOutputLookup</c>, D3) with every call timed and its
/// status counted, and each result appended to <c>lookups.csv</c>. The wait for the lookup's own concurrency and rate
/// limits is part of the time.
/// </summary>
public sealed class TimedFundingOutputLookup(IFundingOutputLookup inner) : IFundingOutputLookup, IDisposable
{
    private readonly Lock _gate = new();
    private StreamWriter? _log;

    public ConcurrentDictionary<string, long> Statuses { get; } = new(StringComparer.Ordinal);
    public LatencyRecorder Verify { get; } = new();
    public LatencyRecorder Lookup { get; } = new();
    public long Calls => Verify.Count + Lookup.Count;

    public void OpenLog(string path)
    {
        _log = new StreamWriter(path) { AutoFlush = false };
        _log.WriteLine("utc,call,scid,block,status,confirmations,amount_sat,ms");
    }

    public async Task<FundingOutputLookupResult> LookupAsync(ShortChannelId shortChannelId,
                                                             CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var result = await inner.LookupAsync(shortChannelId, cancellationToken);
        Record("lookup", Lookup, shortChannelId, result, watch.Elapsed);
        return result;
    }

    public async Task<FundingOutputLookupResult> VerifyAsync(ShortChannelId shortChannelId, CompactPubKey bitcoinKey1,
                                                             CompactPubKey bitcoinKey2,
                                                             LightningMoney? expectedAmount = null,
                                                             CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var result = await inner.VerifyAsync(shortChannelId, bitcoinKey1, bitcoinKey2, expectedAmount,
                                             cancellationToken);
        Record("verify", Verify, shortChannelId, result, watch.Elapsed);
        return result;
    }

    public void InvalidateFrom(uint height) => inner.InvalidateFrom(height);

    public void Flush()
    {
        lock (_gate)
            _log?.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _log?.Dispose();
            _log = null;
        }
    }

    private void Record(string call, LatencyRecorder recorder, ShortChannelId scid, FundingOutputLookupResult result,
                        TimeSpan elapsed)
    {
        recorder.Record(elapsed, result.IsTransient);
        Statuses.AddOrUpdate($"{call}:{result.Status}", 1, (_, c) => c + 1);
        lock (_gate)
            _log?.WriteLine(string.Join(',', DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), call,
                                        scid.ToString(), scid.BlockHeight.ToString(CultureInfo.InvariantCulture),
                                        result.Status.ToString(),
                                        result.Confirmations.ToString(CultureInfo.InvariantCulture),
                                        result.Amount?.Satoshi.ToString(CultureInfo.InvariantCulture) ?? "",
                                        elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)));
    }
}

/// <summary>
/// The chain monitor of the probe's RPC mode: follows bitcoind by polling <c>getblockcount</c> (no ZMQ, no wallet, no
/// database) and, for every block connected after the start, reads it through the product's
/// <see cref="IBitcoinChainService"/> and raises <see cref="IBlockchainMonitor.OnNewBlockDetected"/> and
/// <see cref="IBlockchainMonitor.OnBlockInputs"/> (the graph pruner's spend detection); a block whose parent is not the
/// last one raised is a reorg (<c>OnBlockDisconnected</c> to the fork point found in the recent hashes).
/// <see cref="IBlockchainMonitor.LastProcessedBlockHeight"/> is the tip the sync asks the peers about
/// (<see cref="SyncTip"/>: bitcoind's header height by default, so the range sync covers the whole graph even while
/// bitcoind is in IBD). Anything that would publish throws.
/// </summary>
public class RpcBlockFollower : DispatchProxy
{
    private readonly Lock _gate = new();
    private readonly List<EventHandler<BlockInputsEventArgs>> _inputHandlers = [];
    private readonly List<EventHandler<NewBlockEventArgs>> _blockHandlers = [];
    private readonly List<EventHandler<BlockDisconnectedEventArgs>> _disconnectHandlers = [];
    private readonly LinkedList<(uint Height, uint256 Hash)> _recent = new();

    private IBitcoinChainService _chain = null!;
    private int _maxBlocksPerPoll;

    public uint SyncTip { get; set; }
    public uint LastBlockHeight { get; private set; }
    public long BlocksProcessed { get; private set; }
    public long SpentOutpointsRaised { get; private set; }
    public long Reorgs { get; private set; }
    public string? LastError { get; private set; }

    public static (IBlockchainMonitor Monitor, RpcBlockFollower Follower) Create(IBitcoinChainService chain,
                                                                                 uint syncTip, int maxBlocksPerPoll)
    {
        var proxy = Create<IBlockchainMonitor, RpcBlockFollower>();
        var follower = (RpcBlockFollower)(object)proxy;
        follower._chain = chain;
        follower.SyncTip = syncTip;
        follower._maxBlocksPerPoll = maxBlocksPerPoll;
        return (proxy, follower);
    }

    /// <summary>Starts following from bitcoind's current block count (no history is read).</summary>
    public async Task StartAtAsync(uint height)
    {
        LastBlockHeight = height;
        var hash = await _chain.GetBlockHashAsync(height);
        lock (_gate)
            _recent.AddLast((height, hash));
    }

    /// <summary>Runs until cancelled: every <paramref name="interval"/>, raises the blocks connected since.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync();
                LastError = null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LastError = $"{e.GetType().Name}: {e.Message}";
            }

            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync()
    {
        var count = await _chain.GetCurrentBlockHeightAsync();
        var processed = 0;
        while (LastBlockHeight < count && processed < _maxBlocksPerPoll)
        {
            var height = LastBlockHeight + 1;
            var block = await _chain.GetBlockAsync(height) ?? throw new InvalidOperationException(
                            $"block {height} is not available");
            var parent = block.Header.HashPrevBlock;
            (uint Height, uint256 Hash) last;
            lock (_gate)
                last = _recent.Last!.Value;

            if (parent != last.Hash)
            {
                // A reorg: step back to the newest recent block that is still in the active chain
                await DisconnectToForkAsync();
                continue;
            }

            var spent = new List<(TxId TransactionId, uint OutputIndex)>();
            foreach (var tx in block.Transactions)
            {
                if (tx.IsCoinBase)
                    continue;
                foreach (var input in tx.Inputs)
                    spent.Add((new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N));
            }

            var hash = block.GetHash();
            lock (_gate)
            {
                _recent.AddLast((height, hash));
                while (_recent.Count > 100)
                    _recent.RemoveFirst();
            }

            LastBlockHeight = height;
            BlocksProcessed++;
            SpentOutpointsRaised += spent.Count;
            var hashBytes = new Hash(hash.ToBytes());
            foreach (var handler in Snapshot(_blockHandlers))
                handler(this, new NewBlockEventArgs(height, hashBytes));
            foreach (var handler in Snapshot(_inputHandlers))
                handler(this, new BlockInputsEventArgs(height, hashBytes, spent));
            processed++;
        }
    }

    private async Task DisconnectToForkAsync()
    {
        List<(uint Height, uint256 Hash)> recent;
        lock (_gate)
            recent = _recent.ToList();

        for (var i = recent.Count - 1; i >= 0; i--)
        {
            var (height, hash) = recent[i];
            if (await _chain.GetBlockHashAsync(height) != hash)
                continue;

            var disconnectedFrom = LastBlockHeight;
            var disconnectedHash = recent[^1].Hash;
            lock (_gate)
            {
                while (_recent.Last!.Value.Height > height)
                    _recent.RemoveLast();
            }

            LastBlockHeight = height;
            Reorgs++;
            foreach (var handler in Snapshot(_disconnectHandlers))
                handler(this, new BlockDisconnectedEventArgs(disconnectedFrom, new Hash(disconnectedHash.ToBytes()), height));
            return;
        }

        throw new InvalidOperationException("A reorg deeper than the 100 recent blocks");
    }

    private List<T> Snapshot<T>(List<T> handlers)
    {
        lock (_gate)
            return handlers.ToList();
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var name = targetMethod.Name;
        switch (name)
        {
            case "get_LastProcessedBlockHeight":
                return Math.Max(SyncTip, LastBlockHeight);
            case "get_IsChainProcessingHalted":
                return false;
            case "get_ChainProcessingHaltReason":
                return null;
            case "add_OnBlockInputs":
                lock (_gate) _inputHandlers.Add((EventHandler<BlockInputsEventArgs>)args![0]!);
                return null;
            case "remove_OnBlockInputs":
                lock (_gate) _inputHandlers.Remove((EventHandler<BlockInputsEventArgs>)args![0]!);
                return null;
            case "add_OnNewBlockDetected":
                lock (_gate) _blockHandlers.Add((EventHandler<NewBlockEventArgs>)args![0]!);
                return null;
            case "remove_OnNewBlockDetected":
                lock (_gate) _blockHandlers.Remove((EventHandler<NewBlockEventArgs>)args![0]!);
                return null;
            case "add_OnBlockDisconnected":
                lock (_gate) _disconnectHandlers.Add((EventHandler<BlockDisconnectedEventArgs>)args![0]!);
                return null;
            case "remove_OnBlockDisconnected":
                lock (_gate) _disconnectHandlers.Remove((EventHandler<BlockDisconnectedEventArgs>)args![0]!);
                return null;
        }

        if (name.Contains("Publish", StringComparison.Ordinal) || name.Contains("Broadcast", StringComparison.Ordinal))
            throw new InvalidOperationException($"The gossip probe never publishes: {name} must never be called");

        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(void))
            return null;
        if (returnType == typeof(Task))
            return Task.CompletedTask;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType);
            return fromResult.Invoke(null, [resultType.IsValueType ? Activator.CreateInstance(resultType) : null]);
        }

        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}