// Ported from LNUnit.LND (https://github.com/nbd-wtf/LNUnit, LNDNodePool.cs), Copyright (c) 2024-2025 nbd, MIT
// License; the full text is in LICENSE-LNUnit.txt next to this file. Changed: thread-safe state (ReadyNodes is a
// snapshot), a node joins ReadyNodes only when its readiness check passes (LNUnit.LND also added nodes that were not
// SERVER_ACTIVE), TotalNodes follows AddNode/RemoveNode, replaceable readiness check and connection factory, an
// explicit UpdateReadyStatesAsync/WaitUntilAllReadyAsync, no DI/IOptions constructor, Async names, a rebalance counts
// only a payment LND reports Succeeded, and the balance task computation is a pure function.

using System.Diagnostics;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace NLightning.Testing.Lnd;

using Lnrpc;
using Routerrpc;

/// <summary>
/// A set of LND connections with a readiness loop: <see cref="ReadyNodes"/> holds the nodes whose readiness check
/// passed on the last pass. Also LNUnit.LND's 50/50 rebalancing between pool members.
/// </summary>
public class LndNodePool : IDisposable
{
    private const long StartupMaxTimeMilliseconds = 10_000;
    private static readonly TimeSpan s_quickPollPeriod = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_stateTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Func<LndSettings, CancellationToken, Task<LndNodeConnection>> _connect;
    private readonly Lock _gate = new();
    private readonly ILogger<LndNodePool>? _logger;
    private readonly List<LndNodeConnection> _nodes = [];
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private readonly List<LndSettings> _pending = [];
    private readonly HashSet<LndNodeConnection> _ready = [];
    private readonly Func<LndNodeConnection, CancellationToken, Task<bool>> _readinessCheck;
    private readonly Stopwatch _runtime = Stopwatch.StartNew();
    private readonly TimeSpan _updateReadyStatesPeriod;
    private bool _quickStartupMode;
    private bool _isDisposed;

    /// <summary>Creates the pool and, unless the config turns it off, starts the background readiness loop.</summary>
    public LndNodePool(LndNodePoolConfig config, ILogger<LndNodePool>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _logger = logger;
        _nodes.AddRange(config.Nodes);
        _pending.AddRange(config.ConnectTo);
        _updateReadyStatesPeriod = TimeSpan.FromSeconds(config.UpdateReadyStatesPeriod);
        _quickStartupMode = config.QuickStartupMode;
        _readinessCheck = config.ReadinessCheck ?? IsServerActiveAsync;
        _connect = config.ConnectionFactory ?? ((settings, ct) => LndNodeConnection.ConnectAsync(settings, null, ct));

        if (config.StartBackgroundUpdates)
            _ = Task.Run(() => RunReadinessLoopAsync(_cancellationTokenSource.Token));
        _logger?.LogDebug("LndNodePool created with {TotalNodes} nodes", TotalNodes);
    }

    /// <summary>Every node, connected or still to connect.</summary>
    public int TotalNodes
    {
        get
        {
            lock (_gate)
                return _nodes.Count + _pending.Count;
        }
    }

    /// <summary>The ready nodes, in the order they joined the pool (a snapshot).</summary>
    public IReadOnlyList<LndNodeConnection> ReadyNodes
    {
        get
        {
            lock (_gate)
                return _nodes.Where(_ready.Contains).ToArray();
        }
    }

    /// <summary>Every connected node, ready or not (a snapshot).</summary>
    public IReadOnlyList<LndNodeConnection> Nodes
    {
        get
        {
            lock (_gate)
                return _nodes.ToArray();
        }
    }

    /// <summary>Every node is connected and ready.</summary>
    public bool AllReady
    {
        get
        {
            lock (_gate)
                return _pending.Count == 0 && _nodes.Count == _ready.Count;
        }
    }

    /// <summary>Called with each completed rebalance task, to persist it.</summary>
    public Func<BalanceTask, Task>? SaveRebalanceAction { get; set; }

    /// <summary>Stops the loop and disposes every connection the pool holds.</summary>
    public void Dispose()
    {
        LndNodeConnection[] nodes;
        lock (_gate)
        {
            if (_isDisposed)
                return;
            _isDisposed = true;
            nodes = _nodes.ToArray();
            _nodes.Clear();
            _ready.Clear();
            _pending.Clear();
        }

        _logger?.LogDebug("Disposing LndNodePool with {NodeCount} nodes", nodes.Length);
        _cancellationTokenSource.Cancel();
        foreach (var node in nodes)
            node.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// One readiness pass: connects the nodes not connected yet, then runs the readiness check on every node and
    /// updates <see cref="ReadyNodes"/>. Passes never overlap.
    /// </summary>
    public async Task UpdateReadyStatesAsync(CancellationToken cancellationToken = default)
    {
        await _passGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ConnectPendingAsync(cancellationToken).ConfigureAwait(false);

            foreach (var node in Nodes)
            {
                var ready = await CheckAsync(node, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_isDisposed || !_nodes.Contains(node))
                        continue;
                    if (ready && _ready.Add(node))
                        _logger?.LogDebug("{Alias} is ready, added to the pool's ready nodes", node.LocalAlias);
                    else if (!ready && _ready.Remove(node))
                        _logger?.LogDebug("{Alias} is not ready, removed from the pool's ready nodes", node.LocalAlias);
                }
            }
        }
        finally
        {
            _passGate.Release();
        }
    }

    /// <summary>Runs readiness passes until <see cref="AllReady"/>; throws <see cref="TimeoutException"/> after <paramref name="timeout"/>.</summary>
    public async Task WaitUntilAllReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                await UpdateReadyStatesAsync(deadline.Token).ConfigureAwait(false);
                if (AllReady)
                    return;
                await Task.Delay(s_quickPollPeriod, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"LndNodePool: {ReadyNodes.Count} of {TotalNodes} nodes ready after {timeout.TotalSeconds:0.#} s.");
        }
    }

    /// <summary>The first ready node (LNUnit.LND's GetLNDNodeConnection); throws when none is ready.</summary>
    public LndNodeConnection GetLndNodeConnection() =>
        ReadyNodes.FirstOrDefault() ?? throw new InvalidOperationException("LndNodePool has no ready node.");

    /// <summary>The node with this identity key (ready or not), or null.</summary>
    public LndNodeConnection? GetLndNodeConnection(string pubkey)
    {
        lock (_gate)
            return _nodes.FirstOrDefault(x => x.LocalNodePubKey == pubkey);
    }

    /// <summary>Takes a node out of the pool; the caller owns it from then on.</summary>
    public void RemoveNode(LndNodeConnection node)
    {
        lock (_gate)
        {
            _nodes.Remove(node);
            _ready.Remove(node);
        }
    }

    /// <summary>Queues a node to connect to on the next readiness pass.</summary>
    public void AddNode(LndSettings nodeSettings)
    {
        ArgumentNullException.ThrowIfNull(nodeSettings);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _pending.Add(nodeSettings);
        }
    }

    /// <summary>
    /// Brings every channel between two pool members towards a 50/50 split by paying an invoice of the side with
    /// less over that channel (direct peers, so no fees). Channels whose sides differ by less than
    /// <paramref name="deltaThreshold"/> sat are left alone.
    /// </summary>
    public async Task<PoolRebalanceStats> RebalanceNodePoolAsync(int deltaThreshold = 100_000,
                                                                 CancellationToken cancellationToken = default)
    {
        var ready = ReadyNodes;
        var tasks = await GetInternalNodeEvenBalanceTasksAsync(ready, deltaThreshold, cancellationToken)
                       .ConfigureAwait(false);
        var stats = new PoolRebalanceStats();
        foreach (var task in tasks)
        {
            var src = ready.First(x => x.LocalNodePubKey == task.SrcPk);
            var dest = ready.First(x => x.LocalNodePubKey == task.DestPk);
            var paymentHash = await InvoicePayRebalanceAsync(src, dest, task.Amount, _logger, task.ChanId,
                                                             cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(paymentHash))
                continue;

            stats.TotalAmount += (ulong)task.Amount;
            stats.TotalRebalanceCount++;
            task.PaymentHash = Convert.FromHexString(paymentHash);
            if (SaveRebalanceAction is not null)
                await SaveRebalanceAction(task).ConfigureAwait(false);
            stats.Tasks.Add(task);
        }

        return stats;
    }

    /// <summary>
    /// Pays an invoice of <paramref name="dest"/> from <paramref name="src"/> (over <paramref name="channelId"/> when
    /// not 0). Returns the payment hash when LND reports the payment succeeded, else null.
    /// </summary>
    public static async Task<string?> InvoicePayRebalanceAsync(LndNodeConnection src, LndNodeConnection dest,
                                                               long valueInSatoshis, ILogger? logger = null,
                                                               ulong channelId = 0,
                                                               CancellationToken cancellationToken = default)
    {
        try
        {
            logger?.LogDebug("InvoicePayRebalance: {Value} sat from {Source} to {Destination}", valueInSatoshis,
                             src.LocalAlias, dest.LocalAlias);
            var invoice = await dest.LightningClient.AddInvoiceAsync(new Invoice
            {
                Value = valueInSatoshis,
                Memo = "InvoicePayRebalance",
                Expiry = 60
            }, cancellationToken: cancellationToken).ConfigureAwait(false);

            var payment = new SendPaymentRequest
            {
                PaymentRequest = invoice.PaymentRequest,
                TimeoutSeconds = 20,
                NoInflightUpdates = true
            };
            if (channelId != 0)
                payment.OutgoingChanIds.Add(channelId);

            using var call = src.RouterClient.SendPaymentV2(payment, cancellationToken: cancellationToken);
            Payment? last = null;
            await foreach (var update in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                last = update;

            if (last?.Status != Payment.Types.PaymentStatus.Succeeded)
            {
                logger?.LogDebug("InvoicePayRebalance: {Source} -> {Destination} ended {Status} ({Reason})",
                                 src.LocalAlias, dest.LocalAlias, last?.Status, last?.FailureReason);
                return null;
            }

            logger?.LogDebug("InvoicePayRebalance: {Source} -> {Destination} paid {PaymentHash}", src.LocalAlias,
                             dest.LocalAlias, last.PaymentHash);
            return last.PaymentHash;
        }
        catch (RpcException e)
        {
            logger?.LogDebug(e, "InvoicePayRebalance: {Source} -> {Destination} failed", src.LocalAlias,
                             dest.LocalAlias);
            return null;
        }
    }

    /// <summary>The 50/50 balance tasks for every active channel between the given pool members.</summary>
    public static async Task<List<BalanceTask>> GetInternalNodeEvenBalanceTasksAsync(
        IReadOnlyList<LndNodeConnection> nodes, int deltaThreshold = 100_000,
        CancellationToken cancellationToken = default)
    {
        var channelsByNode = new List<(string NodePubKey, IReadOnlyList<Channel> Channels)>();
        foreach (var node in nodes)
        {
            var response = await node.LightningClient.ListChannelsAsync(new ListChannelsRequest
            {
                ActiveOnly = true,
                PeerAliasLookup = false
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            channelsByNode.Add((node.LocalNodePubKey, response.Channels));
        }

        return ComputeEvenBalanceTasks(channelsByNode, deltaThreshold);
    }

    /// <summary>
    /// LNUnit.LND's balance task rule: for each channel between two of these nodes (once per channel point) whose
    /// sides differ by at least <paramref name="deltaThreshold"/> sat, move the richer side's excess over half the
    /// capacity, capped below the larger <c>max_pending_amt_msat</c> and skipped under the smaller <c>min_htlc_msat</c>.
    /// </summary>
    public static List<BalanceTask> ComputeEvenBalanceTasks(
        IEnumerable<(string NodePubKey, IReadOnlyList<Channel> Channels)> channelsByNode, long deltaThreshold)
    {
        var nodes = channelsByNode.ToList();
        var poolPubKeys = nodes.Select(x => x.NodePubKey).ToHashSet(StringComparer.Ordinal);
        var tasks = new List<BalanceTask>();
        foreach (var (nodePubKey, channels) in nodes)
            foreach (var channel in channels.Where(x => poolPubKeys.Contains(x.RemotePubkey)))
            {
                if (tasks.Any(x => x.ChannelPoint == channel.ChannelPoint))
                    continue;
                if (Math.Abs(channel.LocalBalance - channel.RemoteBalance) < deltaThreshold)
                    continue;

                var even = channel.Capacity / 2;
                BalanceTask task;
                if (channel.RemoteBalance > channel.LocalBalance)
                    task = new BalanceTask
                    {
                        ChannelPoint = channel.ChannelPoint,
                        ChanId = channel.ChanId,
                        SrcPk = channel.RemotePubkey,
                        DestPk = nodePubKey,
                        Amount = channel.RemoteBalance - even
                    };
                else if (channel.RemoteBalance < channel.LocalBalance)
                    task = new BalanceTask
                    {
                        ChannelPoint = channel.ChannelPoint,
                        ChanId = channel.ChanId,
                        SrcPk = nodePubKey,
                        DestPk = channel.RemotePubkey,
                        Amount = channel.LocalBalance - even
                    };
                else
                    continue;

                if (FitToLimits(task, channel))
                    tasks.Add(task);
            }

        return tasks;
    }

    private static bool FitToLimits(BalanceTask task, Channel channel)
    {
        var max = Math.Max((long)((channel.LocalConstraints?.MaxPendingAmtMsat ?? 0) / 1000),
                           (long)((channel.RemoteConstraints?.MaxPendingAmtMsat ?? 0) / 1000));
        var min = Math.Min((long)((channel.LocalConstraints?.MinHtlcMsat ?? 0) / 1000),
                           (long)((channel.RemoteConstraints?.MinHtlcMsat ?? 0) / 1000));
        if (max > 0 && task.Amount > max)
            task.Amount = max - 1;
        else if (task.Amount < min)
            return false;

        return true;
    }

    private static async Task<bool> IsServerActiveAsync(LndNodeConnection node, CancellationToken cancellationToken) =>
        await node.GetStateSafeAsync(s_stateTimeout, cancellationToken).ConfigureAwait(false) == WalletState.ServerActive;

    private async Task<bool> CheckAsync(LndNodeConnection node, CancellationToken cancellationToken)
    {
        try
        {
            return await _readinessCheck(node, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger?.LogDebug(e, "Readiness check failed for {Alias} @ {Host}", node.LocalAlias, node.Host);
            return false;
        }
    }

    private async Task ConnectPendingAsync(CancellationToken cancellationToken)
    {
        LndSettings[] pending;
        lock (_gate)
            pending = _pending.ToArray();

        foreach (var settings in pending)
        {
            LndNodeConnection node;
            try
            {
                node = await _connect(settings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(e, "Failed to connect to the LND node @ {GrpcEndpoint}", settings.GrpcEndpoint);
                continue;
            }

            var added = false;
            lock (_gate)
            {
                if (!_isDisposed && _pending.Remove(settings))
                {
                    _nodes.Add(node);
                    added = true;
                }
            }

            if (!added)
            {
                node.Dispose();
                continue;
            }

            _logger?.LogDebug("Connected to {Alias} @ {GrpcEndpoint}", node.LocalAlias, settings.GrpcEndpoint);
        }
    }

    private async Task RunReadinessLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_quickStartupMode ? s_quickPollPeriod : _updateReadyStatesPeriod, cancellationToken)
                          .ConfigureAwait(false);
                await UpdateReadyStatesAsync(cancellationToken).ConfigureAwait(false);

                if (_quickStartupMode && (_runtime.ElapsedMilliseconds > StartupMaxTimeMilliseconds || AllReady))
                {
                    _logger?.LogDebug("LndNodePool: quick startup mode off");
                    _quickStartupMode = false;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposed.
        }
        catch (ObjectDisposedException)
        {
            // Disposed while a pass was running.
        }
    }

    /// <summary>What <see cref="RebalanceNodePoolAsync"/> did.</summary>
    public class PoolRebalanceStats
    {
        public int TotalRebalanceCount { get; set; }
        public ulong TotalAmount { get; set; }
        public List<BalanceTask> Tasks { get; set; } = [];
    }

    /// <summary>One rebalance payment: <see cref="Amount"/> sat from <see cref="SrcPk"/> to <see cref="DestPk"/>.</summary>
    public record BalanceTask
    {
        public required string ChannelPoint { get; set; }
        public ulong ChanId { get; set; }
        public required string SrcPk { get; set; }
        public required string DestPk { get; set; }
        public long Amount { get; set; }
        public byte[]? PaymentHash { get; set; }
    }
}