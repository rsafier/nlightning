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
/// BOLT 10 bootstrap (NL-113): a node that knows no peer asks the DNS seeds for some and connects to them. Off unless
/// <c>Node:Bootstrap:Enabled</c> (D-B10-1), and only on networks with seeds (D-B10-2).
/// </summary>
/// <remarks>
/// <para>The host starts it after <c>PeerManager.StartAsync</c>; it runs in the background and never blocks the start.
/// Before every run it checks the gate. Two reasons end the loop: no seeds for the network, and
/// <see cref="BootstrapOptions.MinPeers"/> connected. The others skip that run only and are checked again after
/// <see cref="BootstrapOptions.RetryInterval"/> (each skip counts toward <see cref="BootstrapOptions.MaxRuns"/>):
/// chain processing halted, a saved peer with active channels that is not connected (the peer manager keeps
/// reconnecting those; a saved peer without channels is dialed once at start and never again, so it does not count),
/// and a graph node with an address (the node already knows contacts, NL-543).</para>
/// <para>A run queries the seeds one at a time in random order until it has enough candidates, drops candidates
/// already connected, saved or ourselves and endpoints that failed earlier in this process, keeps one address per
/// node and one node per address, caps what one seed may supply and dials them through
/// <see cref="IPeerManager.DialPeerAsync"/> under
/// <see cref="BootstrapOptions.ConnectTimeout"/>, which cancels the dial (so a timed-out dial never outlives its
/// slot); the peer manager saves the peers it connects. The BOLT 8 handshake is the only proof of a seed's node
/// id.</para>
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

    /// <summary>The background loop, while it runs (tests).</summary>
    internal Task? Loop => _loop;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Options.IsEnabled)
        {
            _logger.LogDebug("BOLT 10 bootstrap is off (Node:Bootstrap:Enabled)");
            return Task.CompletedTask;
        }

        if (Options.ObsoleteSeedsIgnored)
            _logger.LogWarning("Node:DnsSeedServers is obsolete and ignored; the bootstrap uses Node:Bootstrap:Seeds "
                             + "or the network's seeds. Move your seeds to Node:Bootstrap:Seeds");

        if (_loop is not null)
            throw new InvalidOperationException($"{nameof(PeerBootstrapService)} is already running");

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

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            if (Options.StartupDelay > TimeSpan.Zero)
                await Task.Delay(Options.StartupDelay, ct);

            string? lastSkip = null;
            for (var run = 1; run <= Options.MaxRuns; run++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var gate = await CheckGateAsync(ct);
                    if (gate is { Final: true })
                    {
                        _logger.LogInformation("BOLT 10 bootstrap: {Reason}, skipped", gate.Value.Reason);
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
                    }
                    else
                    {
                        lastSkip = null;
                        await RunOnceAsync(run, ct);
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

                if (_peerManager.ListPeers().Count >= Options.MinPeers)
                    return;

                if (run == Options.MaxRuns)
                {
                    _logger.LogInformation("BOLT 10 bootstrap: stopping after {Runs} runs with {Peers} peers",
                                           run, _peerManager.ListPeers().Count);
                    return;
                }

                await Task.Delay(Options.RetryInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping
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
        if (ignoredConfigured)
            _logger.LogWarning("BOLT 10 bootstrap: the configured seeds are ignored on {Network}, which has no public "
                             + "seeds (set Node:Bootstrap:AllowSeedsOnThisNetwork to use them)", network);
        if (seeds.Count == 0)
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

        if (_graphStore?.GetSnapshot().Nodes.Any(n => n.Addresses.Count > 0) == true)
            return ("the graph knows nodes with addresses", false);

        return null;
    }

    private static bool HasActiveChannels(PeerModel peer) =>
        peer.Channels is { Count: > 0 } channels
     && channels.Any(c => c.State is not (ChannelState.Closed or ChannelState.Stale));

    private async Task RunOnceAsync(int run, CancellationToken ct)
    {
        var seeds = Options.GetEffectiveSeeds(_nodeOptions.BitcoinNetwork, out _).ToArray();
        Random.Shared.Shuffle(seeds);
        var connectedBefore = _peerManager.ListPeers().Count;
        _logger.LogInformation("BOLT 10 bootstrap starting (run {Run}, seeds {Seeds}, have {Peers} peers)", run,
                               string.Join(", ", seeds), connectedBefore);

        // Query the seeds one at a time until there are enough candidates
        var wanted = Options.MaxPeersFromBootstrap * 3;
        var collected = new List<SeedPeerCandidate>();
        int seedsTried = 0, seedsOk = 0;
        foreach (var seed in seeds)
        {
            if (collected.Count >= wanted)
                break;

            seedsTried++;
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
                continue;
            }

            if (result.Outcome == DnsSeedOutcome.Ok && result.Candidates.Count > 0)
                seedsOk++;
            else
                _logger.LogWarning("BOLT 10 bootstrap: seed {Seed} answered {Outcome} ({Candidates} candidates, "
                                 + "{Rejected} rejected)", seed, result.Outcome, result.Candidates.Count,
                                   result.Rejected);

            collected.AddRange(result.Candidates);
        }

        var candidates = await SelectCandidatesAsync(collected);
        var (connected, attempted) = await DialAsync(candidates, ct);
        _logger.LogInformation("BOLT 10 bootstrap: connected {Connected}/{Attempted} from {SeedsOk}/{SeedsTried} seeds",
                               connected, attempted, seedsOk, seedsTried);
        if (connected == 0)
            _logger.LogWarning("BOLT 10 bootstrap: run {Run} connected no peer", run);
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
    private async Task<(int Connected, int Attempted)> DialAsync(List<SeedPeerCandidate> candidates,
                                                                 CancellationToken ct)
    {
        var maxPeers = Options.MaxPeersFromBootstrap;
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
            var results = await Task.WhenAll(batch.Select(c => DialOneAsync(c, ct)));
            for (var i = 0; i < batch.Count; i++)
            {
                switch (results[i])
                {
                    case DialOutcome.Connected:
                        connected++;
                        break;
                    case DialOutcome.Failed:
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

    private async Task<DialOutcome> DialOneAsync(SeedPeerCandidate candidate, CancellationToken ct)
    {
        var address = candidate.ToPeerAddressInfo();

        // The timeout cancels the dial itself, which closes its connection, rather than stop waiting for it
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Options.ConnectTimeout);
        try
        {
            await _peerManager.DialPeerAsync(address, timeout.Token);
            _logger.LogInformation("BOLT 10 bootstrap: connected to {Peer} (seed {Seed})", address.Address,
                                   candidate.Seed);
            return DialOutcome.Connected;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            _logger.LogDebug("BOLT 10 bootstrap: already connected to {Peer}", address.Address);
            return DialOutcome.AlreadyConnected;
        }
        catch (Exception e) when (e is ConnectionException or TimeoutException or SocketException
                                    or OperationCanceledException)
        {
            _logger.LogDebug("BOLT 10 bootstrap: could not connect to {Peer}: {Message}", address.Address, e.Message);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "BOLT 10 bootstrap: could not connect to {Peer}", address.Address);
        }

        return DialOutcome.Failed;
    }

    private async Task<IReadOnlyList<PeerModel>> GetSavedPeersAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return [.. await uow.GetPeersForStartupAsync()];
    }

    private enum DialOutcome
    {
        Connected,
        AlreadyConnected,
        Failed
    }
}