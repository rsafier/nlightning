using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Node.Bootstrap;

using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Bootstrap;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Gossip.Graph.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// BOLT 10 bootstrap (NL-113) and graph top-up (NL-543): a node with fewer than
/// <see cref="BootstrapOptions.MinPeers"/> peers dials nodes of its gossip graph and, when those do not bring it there,
/// asks the DNS seeds for more. On by default on mainnet only (<c>Node:Bootstrap:Enabled</c>, D-B10-1 as reversed on
/// 2026-09-28; the graph top-up follows the same switch).
/// </summary>
/// <remarks>
/// <para>The host starts it after <c>PeerManager.StartAsync</c>; it runs in the background and never blocks the start.
/// Before every run it checks the gate. Two reasons end the loop: no seeds for the network and no graph, and
/// <see cref="BootstrapOptions.MinPeers"/> connected. The others skip that run only and are checked again after
/// <see cref="BootstrapOptions.RetryInterval"/> (each skip counts toward <see cref="BootstrapOptions.MaxRuns"/>):
/// chain processing halted, and a saved peer with active channels that is not connected (the peer manager keeps
/// reconnecting those; a saved peer without channels is dialed once at start and never again, so it does not count).
/// </para>
/// <para>A run first tops up from the graph (<see cref="GraphPeerCandidateSelector"/>: announced nodes with a usable
/// IP address and an active channel, fresh ones first, random within each tier; never ourselves, a connected peer or
/// an endpoint that failed earlier in this process), dialing at most the peers missing to
/// <see cref="BootstrapOptions.MinPeers"/>. When the node is still below it, the run queries the seeds one at a time
/// in random order until it has enough candidates, drops candidates already connected, saved or ourselves and
/// endpoints that failed earlier in this process, keeps one address per node and one node per address, caps what one
/// seed may supply and dials them, so a graph whose nodes do not answer never blocks the seeds. Every dial goes through
/// <see cref="IPeerManager.DialPeerAsync"/> under <see cref="BootstrapOptions.ConnectTimeout"/> (at least
/// <c>Node:NetworkTimeout</c>), which cancels the dial (so a timed-out dial never outlives its slot), at most
/// <see cref="BootstrapOptions.MaxDialConcurrency"/> at a time and at most
/// <see cref="BootstrapOptions.MaxPeersFromBootstrap"/> connections per run, graph and seeds together; the peer
/// manager saves the peers it connects. The BOLT 8 handshake is the only proof of a node id. Every run, seed query and
/// dial is recorded for <see cref="GetStatus"/> (graph dials with the seed <see cref="GraphPeerCandidateSelector.GraphSource"/>).
/// </para>
/// </remarks>
internal sealed class PeerBootstrapService : IPeerBootstrapService
{
    private static readonly TimeSpan s_stopTimeout = TimeSpan.FromSeconds(5);

    private readonly IDnsSeedClient _dnsSeedClient;
    private readonly IGraphStore? _graphStore;
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly ILogger<PeerBootstrapService> _logger;
    private readonly NodeOptions _nodeOptions;
    private readonly IPeerManager _peerManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISecureKeyManager _secureKeyManager;

    // Endpoints whose dial failed in this process; only the loop touches it
    private readonly HashSet<(IPAddress, ushort)> _failedEndpoints = [];

    // What GetStatus reports; written by the loop (dials from concurrent tasks), read by any thread
    private readonly Lock _statusLock = new();
    private readonly List<BootstrapRunRecord> _runs = [];
    private readonly List<BootstrapSeedQueryRecord> _seedQueries = [];
    private readonly List<BootstrapDialRecord> _dials = [];
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private string? _endReason;

    private bool _warnedIgnoredSeeds;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PeerBootstrapService(IDnsSeedClient dnsSeedClient, ILogger<PeerBootstrapService> logger,
                                IOptions<NodeOptions> nodeOptions, IPeerManager peerManager,
                                IServiceScopeFactory scopeFactory, ISecureKeyManager secureKeyManager,
                                IGraphStore? graphStore = null, IBlockchainMonitor? blockchainMonitor = null)
    {
        _dnsSeedClient = dnsSeedClient;
        _graphStore = graphStore;
        _blockchainMonitor = blockchainMonitor;
        _logger = logger;
        _nodeOptions = nodeOptions.Value;
        _peerManager = peerManager;
        _scopeFactory = scopeFactory;
        _secureKeyManager = secureKeyManager;
    }

    private BootstrapOptions Options => _nodeOptions.Bootstrap;

    private bool IsEnabled => Options.IsEnabledOn(_nodeOptions.BitcoinNetwork);

    /// <summary>The background loop, while it runs (tests).</summary>
    internal Task? Loop => _loop;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            _logger.LogDebug("BOLT 10 bootstrap is off on {Network} (Node:Bootstrap:Enabled)",
                             _nodeOptions.BitcoinNetwork);
            return Task.CompletedTask;
        }

        if (Options.ObsoleteSeedsIgnored)
            _logger.LogWarning("Node:DnsSeedServers is obsolete and ignored; the bootstrap uses Node:Bootstrap:Seeds "
                             + "or the network's seeds. Move your seeds to Node:Bootstrap:Seeds");

        if (_loop is not null)
            throw new InvalidOperationException($"{nameof(PeerBootstrapService)} is already running");

        lock (_statusLock)
            _startedAt = DateTimeOffset.UtcNow;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        _loop = Task.Run(() => RunLoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null || _loop is null)
            return;

        await _cts.CancelAsync();
        try
        {
            await _loop.WaitAsync(s_stopTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("BOLT 10 bootstrap did not stop within {Timeout}", s_stopTimeout);
        }
        catch (OperationCanceledException)
        {
            // Stopping
        }
    }

    /// <inheritdoc />
    public PeerBootstrapStatus GetStatus()
    {
        lock (_statusLock)
            return new PeerBootstrapStatus(IsEnabled, _startedAt, _finishedAt, _endReason, [.. _runs],
                                           [.. _seedQueries], [.. _dials]);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var endReason = "stopped";
        try
        {
            if (Options.StartupDelay > TimeSpan.Zero)
                await Task.Delay(Options.StartupDelay, ct);

            string? lastSkip = null;
            for (var run = 1; run <= Options.MaxRuns; run++)
            {
                ct.ThrowIfCancellationRequested();
                var startedAt = DateTimeOffset.UtcNow;
                try
                {
                    var gate = await CheckGateAsync(ct);
                    if (gate is { Final: true })
                    {
                        _logger.LogInformation("BOLT 10 bootstrap: {Reason}, skipped", gate.Value.Reason);
                        endReason = gate.Value.Reason;
                        return;
                    }

                    if (gate is { } skip)
                    {
                        // A reason that may clear: checked again after RetryInterval; a repeat logs at Debug
                        if (skip.Reason == lastSkip)
                            _logger.LogDebug("BOLT 10 bootstrap: {Reason}, run {Run} skipped", skip.Reason, run);
                        else
                            _logger.LogInformation("BOLT 10 bootstrap: {Reason}, run {Run} skipped; checking again "
                                                 + "in {Interval}", skip.Reason, run, Options.RetryInterval);
                        lastSkip = skip.Reason;
                        Record(_runs, new BootstrapRunRecord(run, startedAt, skip.Reason, 0, 0, 0, 0,
                                                             _peerManager.ListPeers().Count));
                    }
                    else
                    {
                        lastSkip = null;
                        await RunOnceAsync(run, startedAt, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "BOLT 10 bootstrap run {Run} failed", run);
                }

                var peers = _peerManager.ListPeers().Count;
                if (peers >= Options.MinPeers)
                {
                    endReason = $"{peers} peers connected (MinPeers {Options.MinPeers})";
                    return;
                }

                if (run == Options.MaxRuns)
                {
                    _logger.LogInformation("BOLT 10 bootstrap: stopping after {Runs} runs with {Peers} peers",
                                           run, peers);
                    endReason = $"MaxRuns ({run}) reached with {peers} peers";
                    return;
                }

                await Task.Delay(Options.RetryInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping
        }
        finally
        {
            lock (_statusLock)
            {
                _finishedAt = DateTimeOffset.UtcNow;
                _endReason = endReason;
            }
        }
    }

    /// <summary>
    /// Why the node should not bootstrap now, or null when it should. <c>Final</c> reasons end the loop; the others
    /// skip this run only.
    /// </summary>
    private async Task<(string Reason, bool Final)?> CheckGateAsync(CancellationToken ct)
    {
        var network = _nodeOptions.BitcoinNetwork;
        var seeds = Options.GetEffectiveSeeds(network, out var ignoredConfigured);
        if (ignoredConfigured && !_warnedIgnoredSeeds)
        {
            _warnedIgnoredSeeds = true;
            _logger.LogWarning("BOLT 10 bootstrap: the configured seeds are ignored on {Network}, which has no public "
                             + "seeds (set Node:Bootstrap:AllowSeedsOnThisNetwork to use them)", network);
        }

        // Without seeds the graph top-up can still find peers (NL-543); without a graph either, nothing can
        if (seeds.Count == 0 && _graphStore is null)
            return ($"no seeds for {network}", true);

        var connected = _peerManager.ListPeers();
        if (connected.Count >= Options.MinPeers)
            return ($"{connected.Count} peers connected (MinPeers {Options.MinPeers})", true);

        if (_blockchainMonitor is { IsChainProcessingHalted: true })
            return ("chain processing is halted", false);

        // Only saved peers with active channels: the peer manager keeps reconnecting those, while a saved peer
        // without channels was dialed once at start and is never retried, so it must not hold the bootstrap back
        var connectedIds = connected.Select(p => p.NodeId).ToHashSet();
        var saved = await GetSavedPeersAsync();
        ct.ThrowIfCancellationRequested();
        var reconnecting = saved.Count(p => !p.IsInboundOnly && !string.IsNullOrWhiteSpace(p.Host)
                                                             && !connectedIds.Contains(p.NodeId)
                                                             && HasActiveChannels(p));
        if (reconnecting > 0)
            return ($"{reconnecting} saved peers with channels to reconnect", false);

        return null;
    }

    private static bool HasActiveChannels(PeerModel peer) =>
        peer.Channels is { Count: > 0 } channels
     && channels.Any(c => c.State is not (ChannelState.Closed or ChannelState.Stale));

    private async Task RunOnceAsync(int run, DateTimeOffset startedAt, CancellationToken ct)
    {
        var maxPeers = Options.MaxPeersFromBootstrap;

        // The graph first (NL-543): a node that restarts with a graph but cannot reach its saved peers dials nodes it
        // already knows, and asks the seeds only when those do not bring it to MinPeers
        var (graphSelected, graphAttempted, graphConnected) = await TopUpFromGraphAsync(run, maxPeers, ct);
        var peers = _peerManager.ListPeers().Count;
        var seeds = Options.GetEffectiveSeeds(_nodeOptions.BitcoinNetwork, out _).ToArray();
        if (peers >= Options.MinPeers || seeds.Length == 0 || graphConnected >= maxPeers)
        {
            if (graphAttempted > 0 || seeds.Length == 0)
                _logger.LogInformation("BOLT 10 bootstrap: connected {Connected}/{Attempted} graph nodes ({Selected} "
                                     + "candidates), have {Peers} peers{Seeds}", graphConnected, graphAttempted,
                                       graphSelected, peers, seeds.Length == 0 ? "; no seeds to ask" : string.Empty);
            Record(_runs, new BootstrapRunRecord(run, startedAt, null, 0, 0, graphAttempted, graphConnected, peers)
            {
                GraphSelected = graphSelected,
                GraphAttempted = graphAttempted,
                GraphConnected = graphConnected
            });
            return;
        }

        if (graphAttempted > 0)
            _logger.LogInformation("BOLT 10 bootstrap: {Connected}/{Attempted} graph nodes connected, {Peers} peers "
                                 + "(MinPeers {MinPeers}); asking the DNS seeds", graphConnected, graphAttempted,
                                   peers, Options.MinPeers);

        Random.Shared.Shuffle(seeds);
        _logger.LogInformation("BOLT 10 bootstrap starting (run {Run}, seeds {Seeds}, have {Peers} peers)", run,
                               string.Join(", ", seeds), peers);

        // Query the seeds one at a time until there are enough candidates
        var wanted = maxPeers * 3;
        var collected = new List<SeedPeerCandidate>();
        int seedsTried = 0, seedsOk = 0;
        foreach (var seed in seeds)
        {
            if (collected.Count >= wanted)
                break;

            seedsTried++;
            var watch = Stopwatch.StartNew();
            DnsSeedResult result;
            try
            {
                result = await _dnsSeedClient.QuerySeedAsync(seed, Options.AddressFamilies, Options.MaxPerSeed, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogWarning("BOLT 10 bootstrap: seed {Seed} failed: {Message}", seed, e.Message);
                Record(_seedQueries, new BootstrapSeedQueryRecord(run, DateTimeOffset.UtcNow, seed,
                                                                  DnsSeedOutcome.Error, 0, 0, false, null,
                                                                  watch.Elapsed, e.Message));
                continue;
            }

            Record(_seedQueries, new BootstrapSeedQueryRecord(run, DateTimeOffset.UtcNow, seed, result.Outcome,
                                                              result.Candidates.Count, result.Rejected,
                                                              result.UsedFallbackResolver,
                                                              result.SystemResolverOutcome, watch.Elapsed, null));
            if (result.Outcome == DnsSeedOutcome.Ok && result.Candidates.Count > 0)
            {
                seedsOk++;
                if (result.UsedFallbackResolver)
                    _logger.LogInformation("BOLT 10 bootstrap: seed {Seed} answered through the fallback resolvers "
                                         + "({Candidates} candidates; the system resolver answered {SystemOutcome})",
                                           seed, result.Candidates.Count, result.SystemResolverOutcome);
            }
            else
            {
                _logger.LogWarning("BOLT 10 bootstrap: seed {Seed} answered {Outcome} ({Candidates} candidates, "
                                 + "{Rejected} rejected)", seed, result.Outcome, result.Candidates.Count,
                                   result.Rejected);
            }

            collected.AddRange(result.Candidates);
        }

        var candidates = await SelectCandidatesAsync(collected);
        var (connected, attempted) = await DialAsync(run, candidates, maxPeers - graphConnected, ct);
        _logger.LogInformation("BOLT 10 bootstrap: connected {Connected}/{Attempted} from {SeedsOk}/{SeedsTried} seeds",
                               connected, attempted, seedsOk, seedsTried);
        if (connected + graphConnected == 0)
            _logger.LogWarning("BOLT 10 bootstrap: run {Run} connected no peer", run);

        Record(_runs, new BootstrapRunRecord(run, startedAt, null, collected.Count, candidates.Count,
                                             attempted + graphAttempted, connected + graphConnected,
                                             _peerManager.ListPeers().Count)
        {
            GraphSelected = graphSelected,
            GraphAttempted = graphAttempted,
            GraphConnected = graphConnected,
            AskedSeeds = true
        });
    }

    /// <summary>
    /// Dials nodes of the graph (NL-543, <see cref="GraphPeerCandidateSelector"/>) until <paramref name="maxPeers"/>
    /// connect, the node reaches <see cref="BootstrapOptions.MinPeers"/> or the candidates run out.
    /// </summary>
    private async Task<(int Selected, int Attempted, int Connected)> TopUpFromGraphAsync(
        int run, int maxPeers, CancellationToken ct)
    {
        if (_graphStore is null)
            return (0, 0, 0);

        var connectedPeers = _peerManager.ListPeers();
        var missing = Options.MinPeers - connectedPeers.Count;
        if (missing <= 0)
            return (0, 0, 0);

        var excluded = new HashSet<CompactPubKey> { _secureKeyManager.GetNodePubKey() };
        foreach (var peer in connectedPeers)
            excluded.Add(peer.NodeId);

        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var candidates = GraphPeerCandidateSelector.Select(_graphStore.GetSnapshot(), now, excluded, _failedEndpoints,
                                                           Options.AddressFamilies, Options.AllowNonRoutableAddresses,
                                                           maxPeers * 3, Random.Shared);
        if (candidates.Count == 0)
        {
            _logger.LogDebug("BOLT 10 bootstrap: no usable graph node to dial (run {Run})", run);
            return (0, 0, 0);
        }

        _logger.LogInformation("BOLT 10 bootstrap: {Peers} peers (MinPeers {MinPeers}), dialing graph nodes (run {Run}, "
                             + "{Candidates} candidates)", connectedPeers.Count, Options.MinPeers, run,
                               candidates.Count);

        // Only the peers still missing to MinPeers: the graph top-up keeps a minimum, it does not fill the set
        var (connected, attempted) = await DialAsync(run, candidates, Math.Min(maxPeers, missing), ct);
        return (candidates.Count, attempted, connected);
    }

    /// <summary>
    /// Drops candidates already connected, saved or ourselves and endpoints that failed earlier in this process,
    /// keeps the first address of each node and the first node of each address (so a seed cannot point many node ids
    /// at one third party) and shuffles them.
    /// </summary>
    private async Task<List<SeedPeerCandidate>> SelectCandidatesAsync(List<SeedPeerCandidate> collected)
    {
        var excluded = new HashSet<CompactPubKey> { _secureKeyManager.GetNodePubKey() };
        foreach (var peer in _peerManager.ListPeers())
            excluded.Add(peer.NodeId);
        foreach (var peer in await GetSavedPeersAsync())
            excluded.Add(peer.NodeId);

        var candidates = collected.Where(c => !excluded.Contains(c.NodeId)
                                           && !_failedEndpoints.Contains((c.Address, c.Port)))
                                  .DistinctBy(c => c.NodeId)
                                  .DistinctBy(c => (c.Address, c.Port))
                                  .ToArray();
        Random.Shared.Shuffle(candidates);
        return [.. candidates];
    }

    /// <summary>
    /// Dials until <see cref="BootstrapOptions.MaxPeersFromBootstrap"/> connect or the candidates run out, at most
    /// <see cref="BootstrapOptions.MaxDialConcurrency"/> at a time. No seed supplies more than its share of the
    /// connections unless it is the only one that answered.
    /// </summary>
    private async Task<(int Connected, int Attempted)> DialAsync(int run, List<SeedPeerCandidate> candidates,
                                                                 int maxPeers, CancellationToken ct)
    {
        var seedCount = candidates.Select(c => c.Seed).Distinct().Count();
        var perSeedCap = seedCount <= 1 ? maxPeers : (maxPeers + seedCount - 1) / seedCount + 1;
        var perSeed = new Dictionary<string, int>();
        int connected = 0, attempted = 0;
        var remaining = new List<SeedPeerCandidate>(candidates);
        while (connected < maxPeers && remaining.Count > 0)
        {
            // A batch never exceeds the concurrency, nor the connections still wanted
            var batch = new List<SeedPeerCandidate>();
            var batchSize = Math.Min(Options.MaxDialConcurrency, maxPeers - connected);
            for (var i = 0; i < remaining.Count && batch.Count < batchSize;)
            {
                var candidate = remaining[i];
                var used = perSeed.GetValueOrDefault(candidate.Seed);
                if (used >= perSeedCap)
                {
                    i++;
                    continue;
                }

                perSeed[candidate.Seed] = used + 1;
                batch.Add(candidate);
                remaining.RemoveAt(i);
            }

            // Every seed left is at its share
            if (batch.Count == 0)
                break;

            attempted += batch.Count;
            var results = await Task.WhenAll(batch.Select(c => DialOneAsync(run, c, ct)));
            for (var i = 0; i < batch.Count; i++)
            {
                switch (results[i])
                {
                    case BootstrapDialOutcome.Connected:
                        connected++;
                        break;
                    case BootstrapDialOutcome.Failed or BootstrapDialOutcome.TimedOut:
                        perSeed[batch[i].Seed]--;
                        _failedEndpoints.Add((batch[i].Address, batch[i].Port));
                        break;
                    default:
                        perSeed[batch[i].Seed]--;
                        break;
                }
            }
        }

        return (connected, attempted);
    }

    private async Task<BootstrapDialOutcome> DialOneAsync(int run, SeedPeerCandidate candidate, CancellationToken ct)
    {
        var address = candidate.ToPeerAddressInfo();
        var watch = Stopwatch.StartNew();

        // The timeout cancels the dial itself, which closes its connection, rather than stop waiting for it
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var connectTimeout = Options.GetEffectiveConnectTimeout(_nodeOptions.NetworkTimeout);
        timeout.CancelAfter(connectTimeout);
        BootstrapDialOutcome outcome;
        string? error;
        try
        {
            await _peerManager.DialPeerAsync(address, timeout.Token);
            _logger.LogInformation("BOLT 10 bootstrap: connected to {Peer} (seed {Seed})", address.Address,
                                   candidate.Seed);
            outcome = BootstrapDialOutcome.Connected;
            error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException e)
        {
            _logger.LogDebug("BOLT 10 bootstrap: already connected to {Peer}", address.Address);
            outcome = BootstrapDialOutcome.AlreadyConnected;
            error = e.Message;
        }
        catch (Exception e) when (timeout.IsCancellationRequested
                               && e is OperationCanceledException or TimeoutException or ConnectionException)
        {
            // Our ConnectTimeout cancelled the dial (whatever the layer that noticed it threw)
            _logger.LogDebug("BOLT 10 bootstrap: {Peer} did not connect within {Timeout}", address.Address,
                             connectTimeout);
            outcome = BootstrapDialOutcome.TimedOut;
            error = $"timed out after {connectTimeout}";
        }
        catch (Exception e) when (e is ConnectionException or TimeoutException or SocketException
                                    or OperationCanceledException)
        {
            _logger.LogDebug("BOLT 10 bootstrap: could not connect to {Peer}: {Message}", address.Address, e.Message);
            outcome = BootstrapDialOutcome.Failed;
            error = $"{e.GetType().Name}: {e.Message}";
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "BOLT 10 bootstrap: could not connect to {Peer}", address.Address);
            outcome = BootstrapDialOutcome.Failed;
            error = $"{e.GetType().Name}: {e.Message}";
        }

        Record(_dials, new BootstrapDialRecord(run, DateTimeOffset.UtcNow, candidate, outcome, watch.Elapsed, error));
        return outcome;
    }

    private void Record<T>(List<T> records, T record)
    {
        lock (_statusLock)
        {
            if (records.Count >= PeerBootstrapStatus.MaxRecords)
                records.RemoveAt(0);
            records.Add(record);
        }
    }

    private async Task<IReadOnlyList<PeerModel>> GetSavedPeersAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return [.. await uow.GetPeersForStartupAsync()];
    }
}