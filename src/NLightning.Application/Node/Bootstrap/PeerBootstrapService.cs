using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Node.Bootstrap;

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
/// Before every run it checks the gate: enabled, seeds for the network, chain processing not halted, fewer than
/// <see cref="BootstrapOptions.MinPeers"/> connected, no saved dialable peer that is not connected (saved peers are
/// the peer manager's job), and no graph node with an address (the node already knows contacts).</para>
/// <para>A run queries the seeds one at a time in random order until it has enough candidates, drops candidates
/// already connected, saved or ourselves, keeps one address per node, caps what one seed may supply and dials them
/// through <see cref="IPeerManager.ConnectToPeerAsync"/>, which saves the peers it connects. The BOLT 8 handshake is
/// the only proof of a seed's node id. The next start sees the saved peers and does not bootstrap again.</para>
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
        if (Options.SeedsFromObsoleteKey)
            _logger.LogWarning("Node:DnsSeedServers is obsolete; its seeds were used as Node:Bootstrap:Seeds. Move "
                             + "them to Node:Bootstrap:Seeds");

        if (!Options.IsEnabled)
        {
            _logger.LogDebug("BOLT 10 bootstrap is off (Node:Bootstrap:Enabled)");
            return Task.CompletedTask;
        }

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

            for (var run = 1; run <= Options.MaxRuns; run++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var gate = await CheckGateAsync(ct);
                    if (gate is not null)
                    {
                        _logger.LogInformation("BOLT 10 bootstrap: {Reason}, skipped", gate);
                        return;
                    }

                    await RunOnceAsync(run, ct);
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

    /// <summary>Why the node should not bootstrap now, or null when it should.</summary>
    private async Task<string?> CheckGateAsync(CancellationToken ct)
    {
        var network = _nodeOptions.BitcoinNetwork;
        var seeds = Options.GetEffectiveSeeds(network, out var ignoredConfigured);
        if (ignoredConfigured)
            _logger.LogWarning("BOLT 10 bootstrap: the configured seeds are ignored on {Network}, which has no public "
                             + "seeds (set Node:Bootstrap:AllowSeedsOnThisNetwork to use them)", network);
        if (seeds.Count == 0)
            return $"no seeds for {network}";

        if (_blockchainMonitor is { IsChainProcessingHalted: true })
            return "chain processing is halted";

        var connected = _peerManager.ListPeers();
        if (connected.Count >= Options.MinPeers)
            return $"{connected.Count} peers connected (MinPeers {Options.MinPeers})";

        var connectedIds = connected.Select(p => p.NodeId).ToHashSet();
        var saved = await GetSavedPeersAsync();
        ct.ThrowIfCancellationRequested();
        var dialable = saved.Count(p => !p.IsInboundOnly && !string.IsNullOrWhiteSpace(p.Host)
                                                         && !connectedIds.Contains(p.NodeId));
        if (dialable > 0)
            return $"{dialable} saved peers to dial";

        if (_graphStore?.GetSnapshot().Nodes.Any(n => n.Addresses.Count > 0) == true)
            return "the graph knows nodes with addresses";

        return null;
    }

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
    /// Drops candidates already connected, saved or ourselves, keeps the first address of each node and shuffles them.
    /// </summary>
    private async Task<List<SeedPeerCandidate>> SelectCandidatesAsync(List<SeedPeerCandidate> collected)
    {
        var excluded = new HashSet<CompactPubKey> { _secureKeyManager.GetNodePubKey() };
        foreach (var peer in _peerManager.ListPeers())
            excluded.Add(peer.NodeId);
        foreach (var peer in await GetSavedPeersAsync())
            excluded.Add(peer.NodeId);

        var candidates = collected.Where(c => !excluded.Contains(c.NodeId)).DistinctBy(c => c.NodeId).ToArray();
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
                if (results[i])
                    connected++;
                else
                    perSeed[batch[i].Seed]--;
            }
        }

        return (connected, attempted);
    }

    private async Task<bool> DialOneAsync(SeedPeerCandidate candidate, CancellationToken ct)
    {
        var address = candidate.ToPeerAddressInfo();
        try
        {
            await _peerManager.ConnectToPeerAsync(address).WaitAsync(Options.ConnectTimeout, ct);
            _logger.LogInformation("BOLT 10 bootstrap: connected to {Peer} (seed {Seed})", address.Address,
                                   candidate.Seed);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            _logger.LogDebug("BOLT 10 bootstrap: already connected to {Peer}", address.Address);
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

        return false;
    }

    private async Task<IReadOnlyList<PeerModel>> GetSavedPeersAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return [.. await uow.GetPeersForStartupAsync()];
    }
}