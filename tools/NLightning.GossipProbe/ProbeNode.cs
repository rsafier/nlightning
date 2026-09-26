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
using Application.Gossip.Sync;
using Daemon.Extensions;
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

    public ProbeNode(ProbeOptions options, ProbeLoggerProvider loggerProvider, PeerTraffic traffic, uint tip)
    {
        _options = options;
        LoggerProvider = loggerProvider;
        Traffic = traffic;
        Tip = tip;
        FundingLookups = new CountingFundingOutputLookup();
    }

    public ProbeLoggerProvider LoggerProvider { get; }
    public PeerTraffic Traffic { get; }
    public uint Tip { get; }
    public CountingFundingOutputLookup FundingLookups { get; }
    public string DatabasePath => Path.Combine(_options.Directory, "probe.db");
    public string KeyPath => Path.Combine(_options.Directory, "node-key.json");

    public IServiceProvider Services =>
        _provider ?? throw new InvalidOperationException("The probe node has not been built");

    /// <summary>Builds the service graph and checks every option the daemon validates on start.</summary>
    public void Build(int syncPeers)
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
            // No bitcoind: the stub monitor and lookup replace every chain reader the gossip stack uses
            ["Bitcoin:RpcEndpoint"] = "http://127.0.0.1:1",
            ["Bitcoin:RpcUser"] = "none",
            ["Bitcoin:RpcPassword"] = "none",
            ["Bitcoin:ZmqHost"] = "127.0.0.1",
            ["Bitcoin:ZmqBlockPort"] = "1",
            ["Bitcoin:ZmqTxPort"] = "2",
            ["Bitcoin:WatchMempool"] = "false",
            ["FeeEstimation:Source"] = "Fixed",
            ["FeeEstimation:CacheFile"] = Path.Combine(_options.Directory, "fee-cache.bin"),
            ["Node:FeeUpdates:Enabled"] = "false",
            // The test's gossip settings: a leech that syncs, never relays, and takes channels on their signatures
            ["Gossip:Enabled"] = "true",
            ["Gossip:SyncEnabled"] = "true",
            ["Gossip:RelayEnabled"] = "false",
            ["Gossip:AssumeChannelValid"] = "true",
            ["Gossip:AllowPublicChannelsOnMainnet"] = "false",
            ["Gossip:AcceptPublicChannels"] = "false",
            ["Gossip:SyncPeers"] = syncPeers.ToString(CultureInfo.InvariantCulture)
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders()
                                              .AddProvider(LoggerProvider)
                                              .SetMinimumLevel(_options.LogLevel)
                                              // EF logs every SQL command at Information
                                              .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning));
        services.AddNltgNodeServices(configuration, keyManager);

        // The chain: a fixed tip, no blocks, nothing published; funding lookups counted (must stay at 0)
        var monitor = ChainMonitorStub.Create(Tip);
        services.Replace(ServiceDescriptor.Singleton(monitor));
        services.Replace(ServiceDescriptor.Singleton<IFundingOutputLookup>(FundingLookups));

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
        if (!graphOptions.AssumeChannelValid || relayOptions.IsRelayEnabledFor(network))
            throw new InvalidOperationException("The probe must run with AssumeChannelValid on and relay off");
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