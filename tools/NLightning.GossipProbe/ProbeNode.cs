using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.GossipProbe;

using Application.Gossip.Graph;
using Application.Gossip.Relay;
using Application.Gossip.Relay.Interfaces;
using Application.Gossip.Sync;
using Daemon.Extensions;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Persistence.Contexts;

/// <summary>
/// The node the probe runs: the daemon's own composition (<see cref="NodeServiceExtensions.AddNltgNodeServices"/>, the
/// same the Docker test node uses) on mainnet, with a SQLite database and a throwaway key in the probe directory, a stub
/// chain (<see cref="ChainMonitorStub"/>, <see cref="CountingFundingOutputLookup"/>), gossip sync on,
/// <c>Gossip:AssumeChannelValid</c> on, relay off and HTLCs off. It listens on 127.0.0.1 only and has no wallet use,
/// no channel and nothing to announce.
/// </summary>
public sealed class ProbeNode : IAsyncDisposable
{
    /// <summary>The throwaway key file's password (the key holds no funds and is never used for a channel).</summary>
    private const string KeyPassword = "nltg-gossip-probe";

    private readonly ProbeOptions _options;
    private ServiceProvider? _provider;

    public ProbeNode(ProbeOptions options, ProbeLoggerProvider loggerProvider, PeerTraffic traffic, uint tip,
                     RpcSettings? rpc = null)
    {
        _options = options;
        LoggerProvider = loggerProvider;
        Traffic = traffic;
        Tip = tip;
        _rpc = rpc;
        FundingLookups = new CountingFundingOutputLookup();
    }

    private readonly RpcSettings? _rpc;

    /// <summary>RPC mode: the product's chain service, counted and timed (null in stub mode).</summary>
    public CountingChainService? Chain { get; private set; }

    /// <summary>RPC mode: the product's funding output lookup, timed (null in stub mode).</summary>
    public TimedFundingOutputLookup? TimedLookups { get; private set; }

    /// <summary>RPC mode: the block follower behind <see cref="IBlockchainMonitor"/> (null in stub mode).</summary>
    public RpcBlockFollower? Follower { get; private set; }

    /// <summary>Funding lookups asked for (the stub's count, or the product lookup's in RPC mode).</summary>
    public long FundingLookupCount => TimedLookups?.Calls ?? FundingLookups.Lookups;

    public ProbeLoggerProvider LoggerProvider { get; }
    public PeerTraffic Traffic { get; }
    public uint Tip { get; }
    public CountingFundingOutputLookup FundingLookups { get; }

    /// <summary>The NL-417 relayer's recording sender (<c>--relay-to-all</c>), for the per-peer outbox depths.</summary>
    public RecordingGossipPeerSender? RelaySender { get; private set; }
    public string DatabasePath => Path.Combine(_options.Directory, "probe.db");
    public string KeyPath => Path.Combine(_options.Directory, "node-key.json");

    public IServiceProvider Services =>
        _provider ?? throw new InvalidOperationException("The probe node has not been built");

    /// <summary>Builds the service graph and checks every option the daemon validates on start.</summary>
    public void Build(int? syncPeers)
    {
        var keyManager = LoadOrCreateKey();
        var settings = new Dictionary<string, string?>
        {
            ["Node:Network"] = "mainnet",
            ["Node:Daemon"] = "false",
            ["Node:EnableHtlcs"] = "false",
            ["Node:ListenAddresses:0"] = $"127.0.0.1:{_options.ListenPort}",
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = $"Data Source={DatabasePath}",
            // Stub mode: no bitcoind, the stub monitor and lookup replace every chain reader the gossip stack uses.
            // RPC mode: the owner's bitcoind (the password stays in this in-memory configuration)
            ["Bitcoin:RpcEndpoint"] = _rpc?.Url ?? "http://127.0.0.1:1",
            ["Bitcoin:RpcUser"] = _rpc?.User ?? "none",
            ["Bitcoin:RpcPassword"] = _rpc?.Password ?? "none",
            ["Bitcoin:ZmqHost"] = "127.0.0.1",
            ["Bitcoin:ZmqBlockPort"] = "1",
            ["Bitcoin:ZmqTxPort"] = "2",
            ["Bitcoin:WatchMempool"] = "false",
            ["FeeEstimation:Source"] = "Fixed",
            ["FeeEstimation:CacheFile"] = Path.Combine(_options.Directory, "fee-cache.bin"),
            ["Node:FeeUpdates:Enabled"] = "false",
            // The test's gossip settings: a leech that syncs, never relays, and takes channels on their signatures
            ["Gossip:Enabled"] = "true",
            // The NL-417 sink queries nothing and sends no filter of its own (the probe sends its one filter)
            ["Gossip:SyncEnabled"] = _options.Sink ? "false" : "true",
            ["Gossip:RelayEnabled"] = _options.IsRelaying ? "true" : "false",
            ["Gossip:AssumeChannelValid"] = _rpc is null ? "true" : "false",
            ["Gossip:FundingValidation"] = "Full",
            ["Gossip:AllowPublicChannelsOnMainnet"] = "false",
            ["Gossip:AcceptPublicChannels"] = "false",
            // BOLT 10: a bootstrap run leaves Node:Bootstrap at its mainnet default (on); every other run keeps it off, so
            // the configured peers are the only ones
            ["Node:Bootstrap:Enabled"] = _options.Bootstrap ? null : "false"
        };
        if (syncPeers is { } peers)
            settings["Gossip:SyncPeers"] = peers.ToString(CultureInfo.InvariantCulture);
        if (_options.ChainConcurrency is { } concurrency)
            settings["Gossip:ChainLookupConcurrency"] = concurrency.ToString(CultureInfo.InvariantCulture);
        if (_options.ChainRate is { } rate)
            settings["Gossip:ChainLookupsPerSecond"] = rate.ToString(CultureInfo.InvariantCulture);
        foreach (var (key, value) in _options.Overrides)
            settings[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders()
                                              .AddProvider(LoggerProvider)
                                              .SetMinimumLevel(_options.LogLevel)
                                              // EF logs every SQL command at Information
                                              .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning));
        services.AddNltgNodeServices(configuration, keyManager);

        if (_rpc is null)
        {
            // The chain: a fixed tip, no blocks, nothing published; funding lookups counted (must stay at 0)
            var monitor = ChainMonitorStub.Create(Tip);
            services.Replace(ServiceDescriptor.Singleton(monitor));
            services.Replace(ServiceDescriptor.Singleton<IFundingOutputLookup>(FundingLookups));
            // NL-415 registers the product's FundingOutputLookup as a pending-channel view, which would build the
            // bitcoind client (an RPC at construction); the stub has no lookups in flight, and the sync still asks
            // the ingress's view
            services.RemoveAll<IGossipPendingChannels>();
        }
        else
        {
            // The product's BitcoinChainService and FundingOutputLookup (D3), counted and timed; the monitor follows
            // bitcoind's blocks by polling (spend detection for the pruner) and reports Tip to the range sync
            Decorate<IBitcoinChainService>(services, inner => Chain = new CountingChainService(inner));
            Decorate<IFundingOutputLookup>(services, inner => TimedLookups = new TimedFundingOutputLookup(inner));
            services.Replace(ServiceDescriptor.Singleton<IBlockchainMonitor>(sp =>
            {
                var (monitor, follower) = RpcBlockFollower.Create(sp.GetRequiredService<IBitcoinChainService>(), Tip,
                                                                  _options.MaxBlocksPerPoll);
                Follower = follower;
                return monitor;
            }));
        }

        if (_options.RelayTo is { } relayTo)
        {
            // The relay run (D12): relay on, toward the one peer only, every send recorded
            if (_rpc is null)
                throw new InvalidOperationException("--relay-to needs --chain rpc (only chain-checked channels relay)");

            var target = new CompactPubKey(Convert.FromHexString(relayTo));
            Traffic.Relay = new RelayRecorder();
            Decorate<IGossipPeerDirectory>(services, inner => new RelayTargetPeerDirectory(inner, target));
            Decorate<IGossipPeerSender>(services, inner => RelaySender = new RecordingGossipPeerSender(inner,
                                                                          Traffic.Relay));
        }
        else if (_options.RelayToAll)
        {
            // NL-417 relayer: relay on toward every connected peer (the product's directory), every send recorded
            if (_rpc is null)
                throw new InvalidOperationException("--relay-to-all needs --chain rpc (only chain-checked channels relay)");

            Traffic.Relay = new RelayRecorder();
            Decorate<IGossipPeerSender>(services, inner => RelaySender = new RecordingGossipPeerSender(inner,
                                                                          Traffic.Relay));
        }

        if (_options.Sink)
        {
            // NL-417 sink: every received gossip message's order checked; our one filter asks for everything
            Traffic.Sink = new SinkRecorder();
            Decorate<IGossipSyncService>(services, inner => new SinkFilterSyncService(inner, Traffic.Sink));
        }

        // Per-peer traffic counters around the peer services' gossip ports
        Decorate<IGossipIngress>(services, inner => new CountingGossipIngress(inner, Traffic));
        Decorate<IGossipSyncService>(services, inner => new CountingGossipSyncService(inner, Traffic));

        _provider = services.BuildServiceProvider();

        // What ValidateOnStart does in the daemon's host (the AssumeChannelValid mainnet guard included)
        _ = Services.GetRequiredService<IOptions<NodeOptions>>().Value;
        _ = Services.GetRequiredService<IOptions<GossipOptions>>().Value;
        var graphOptions = Services.GetRequiredService<IOptions<GossipGraphOptions>>().Value;
        _ = Services.GetRequiredService<IOptions<GossipSyncOptions>>().Value;
        var relayOptions = Services.GetRequiredService<IOptions<GossipRelayOptions>>().Value;
        var network = Services.GetRequiredService<IOptions<NodeOptions>>().Value.BitcoinNetwork;
        if (graphOptions.AssumeChannelValid != (_rpc is null)
         || relayOptions.IsRelayEnabledFor(network) != _options.IsRelaying)
            throw new InvalidOperationException(
                "The probe must run with relay off (on only with --relay-to or --relay-to-all), and AssumeChannelValid "
              + "on without a chain, off with one");
        if (Services.GetRequiredService<IOptions<NodeOptions>>().Value.Bootstrap.IsEnabledOn(network)
         != _options.Bootstrap)
            throw new InvalidOperationException(
                "BOLT 10 bootstrap must be on (by its mainnet default) with --bootstrap and off without it");

        if (_rpc is not null)
        {
            // Resolve the chain side now, so the decorators exist before anything runs
            _ = Services.GetRequiredService<IFundingOutputLookup>();
            _ = Services.GetRequiredService<IBlockchainMonitor>();
            if (Chain is null || TimedLookups is null || Follower is null)
                throw new InvalidOperationException("The probe's chain decorators were not used");
        }
    }

    /// <summary>Creates the database schema (or applies new migrations).</summary>
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
        await context.Database.MigrateAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => _provider?.DisposeAsync() ?? ValueTask.CompletedTask;

    private SecureKeyManager LoadOrCreateKey()
    {
        if (File.Exists(KeyPath))
            return SecureKeyManager.FromFilePath(KeyPath, BitcoinNetwork.Mainnet, KeyPassword);

        var manager = new SecureKeyManager(RandomNumberGenerator.GetBytes(32), BitcoinNetwork.Mainnet, KeyPath, Tip);
        manager.SaveToFile(KeyPassword);
        return manager;
    }

    private static void Decorate<T>(IServiceCollection services, Func<T, T> decorate) where T : class
    {
        var descriptor = services.Last(d => d.ServiceType == typeof(T));
        services.Replace(ServiceDescriptor.Singleton<T>(sp => decorate(Resolve<T>(sp, descriptor))));
    }

    private static T Resolve<T>(IServiceProvider provider, ServiceDescriptor descriptor) where T : class =>
        descriptor switch
        {
            { ImplementationInstance: T instance } => instance,
            { ImplementationFactory: { } factory } => (T)factory(provider),
            { ImplementationType: { } type } => (T)ActivatorUtilities.CreateInstance(provider, type),
            _ => throw new InvalidOperationException($"Cannot decorate {typeof(T).Name}")
        };

    /// <summary>The monitor the probe gave the node (for tests of the stub).</summary>
    public IBlockchainMonitor Monitor => Services.GetRequiredService<IBlockchainMonitor>();
}