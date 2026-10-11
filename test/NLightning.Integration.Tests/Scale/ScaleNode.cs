using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Integration.Tests.Scale;

using Application.Accounting;
using Application.Accounting.Backfill;
using Application.Accounting.Books;
using Application.Channels.Fees;
using Application.Channels.RoutingPolicies;
using Application.Channels.Safety.Interfaces;
using Application.Onchain.Mempool;
using Application.Payments.Send.Interfaces;
using Application.Payments.Trampoline;
using Daemon.Extensions;
using Daemon.Services;
using Docker.Utils;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Transport.Tor;

/// <summary>
/// One node of the channel-scale benchmark (NL-1357): the daemon's own composition
/// (<see cref="NodeServiceExtensions.AddNltgNodeServices"/>) over SQLite or Postgres, a <see cref="ScaleChain"/> in place
/// of bitcoind (blocks are handed to the monitor by the test, as ZMQ would) and a fixed fee estimate. The start runs the
/// daemon's steps in the daemon's order (<c>NltgDaemonService</c>, as <see cref="NLightningTestNode"/> does) and times
/// each one into <see cref="StartSteps"/>.
/// </summary>
/// <remarks>
/// The node logs at Warning and above only (10,000 "Loaded channel" lines would time the logger); the lines are counted
/// by category in <see cref="WarningCounts"/>.
/// </remarks>
internal sealed class ScaleNode : IAsyncDisposable
{
    private readonly ScaleChain _chain;
    private readonly SilentZmqEndpoint _zmq = new(); // never a real bitcoind's ZMQ port (NL-310)
    private readonly Action<NodeOptions>? _configureNodeOptions;
    private ServiceProvider? _services;
    private IFeeService? _feeService;
    private GossipGraphHostedService? _gossipGraph;
    private bool _started;

    public ScaleNode(string name, TestNodeDatabase database, ISecureKeyManager keyManager, ScaleChain chain, int port,
                     Action<NodeOptions>? configureNodeOptions = null)
    {
        Name = name;
        Database = database;
        KeyManager = keyManager;
        _chain = chain;
        Port = port;
        _configureNodeOptions = configureNodeOptions;
    }

    public string Name { get; }
    public TestNodeDatabase Database { get; }
    public ISecureKeyManager KeyManager { get; }
    public int Port { get; }
    public CompactPubKey NodeId => KeyManager.GetNodePubKey();
    public string Address => $"{Convert.ToHexString(NodeId).ToLowerInvariant()}@{IPAddress.Loopback}:{Port}";

    /// <summary>Extra configuration keys layered over the node's own (set before building).</summary>
    public IDictionary<string, string?> ExtraConfiguration { get; } = new Dictionary<string, string?>();

    /// <summary>Every step of the last <see cref="StartAsync"/> with its wall time, in order.</summary>
    public List<(string Step, TimeSpan Elapsed)> StartSteps { get; } = [];

    /// <summary>Warning-or-worse log lines per category since the node was built.</summary>
    public ConcurrentDictionary<string, int> WarningCounts { get; } = new();

    public IServiceProvider Services =>
        _services ?? throw new InvalidOperationException($"The node {Name} has no service graph");

    public IPeerManager PeerManager => Services.GetRequiredService<IPeerManager>();
    public BlockchainMonitorService Monitor => (BlockchainMonitorService)Services.GetRequiredService<IBlockchainMonitor>();
    public IChannelMemoryRepository Channels => Services.GetRequiredService<IChannelMemoryRepository>();

    /// <summary>Builds the service graph and migrates the database (no service started); for seeding.</summary>
    public async Task BuildAsync(CancellationToken ct)
    {
        _services = BuildServiceProvider();
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database.MigrateAsync(ct);
    }

    /// <summary>
    /// Builds a fresh service graph (a process start) and runs the daemon's start steps, timing each. Ready means the
    /// last step returned: the peers are registered and dialed (up to <c>StartupDialWait</c>) and the chain monitor runs.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (_started)
            throw new InvalidOperationException($"The node {Name} is already running");

        StartSteps.Clear();
        _services ??= BuildServiceProvider();
        await StepAsync("migrate + signing enrollment", async () =>
        {
            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database.MigrateAsync(ct);
            await NodeSigningEnrollmentExtensions.ValidateNodeSigningEnrollmentAsync(scope.ServiceProvider, ct);
        });
        await StepAsync("fee service", async () =>
        {
            _feeService = Services.GetRequiredService<IFeeService>();
            await _feeService.StartAsync(ct);
        });
        await StepAsync("channel key index reconcile", async () =>
        {
            using var scope = Services.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<Domain.Persistence.Interfaces.IUnitOfWork>();
            KeyManager.EnsureLastUsedChannelIndexAtLeast(await uow.ChannelDbRepository.GetHighestLocalKeyIndexAsync());
        });
        await StepAsync("gossip graph load", async () =>
        {
            _gossipGraph = ActivatorUtilities.CreateInstance<GossipGraphHostedService>(Services);
            await _gossipGraph.StartAsync(ct);
        });
        await StepAsync("accounting cutover",
                        () => Services.GetRequiredService<AccountingBackfillService>().EnsureCutoverAsync(ct));
        await StepAsync("wallet load", () => Monitor.LoadWalletAsync(ct));
        await StepAsync("channel policies",
                        () => Services.GetService<ChannelPolicyStore>()?.LoadAsync(ct) ?? Task.CompletedTask);
        await StepAsync("retired scid map",
                        () => Services.GetService<IRetiredScidMap>()?.LoadAsync(Monitor.LastProcessedBlockHeight, ct)
                           ?? Task.CompletedTask);
        await StepAsync("peer manager (load, register, dial)", () => PeerManager.StartAsync(ct));
        await StepAsync("tor onion service",
                        () => Services.GetRequiredService<ITorOnionService>().StartAsync(CancellationToken.None));
        await StepAsync("reconcile in-flight payments",
                        () => Services.GetRequiredService<IPaymentOutcomeHandler>()
                                      .ReconcileInFlightPaymentsAsync(ct));
        await StepAsync("trampoline relays",
                        () => Services.GetService<TrampolineRelayService>()?.StartAsync(ct) ?? Task.CompletedTask);
        await StepAsync("safety services + fee update scheduler", async () =>
        {
            Services.GetRequiredService<IChannelFailureService>().Start();
            Services.GetRequiredService<IHtlcExpiryMonitor>().Start();
            await Services.GetRequiredService<IFeeUpdateScheduler>().StartAsync(ct);
            Services.GetRequiredService<IMempoolReactor>().Start();
        });
        await StepAsync("chain monitor start", () => Monitor.StartAsync(0, ct));
        await StepAsync("after-monitor catch-ups", async () =>
        {
            Services.GetService<IRetiredScidMap>()?.PruneExpired(Monitor.LastProcessedBlockHeight);
            if (Services.GetService<IWalletSpendService>() is { } walletSpend)
                await walletSpend.ReleaseOrphanedReservationsAsync(ct);
            if (Services.GetService<Application.InteractiveTx.WalletInteractiveTxContributor>() is { } contributor)
                await contributor.ReleaseOrphanedReservationsAsync(ct);
            if (Services.GetService<Application.Channels.Splicing.SpliceDepthWatcher>() is { } depth)
                await depth.CatchUpAsync(ct);
            Services.GetRequiredService<OnionReplayBlockPruner>().Start();
            Services.GetService<AccountingEventSealerService>()?.Start();
            Services.GetService<AccountingBooksService>()?.Start();
        });
        _started = true;
    }

    /// <summary>Stops the node in the daemon's order and disposes the service graph (database and key stay).</summary>
    public async Task StopAsync()
    {
        if (_services is null)
            return;

        try
        {
            if (_started)
            {
                await Task.WhenAll(Services.GetRequiredService<IHtlcExpiryMonitor>().StopAsync(),
                                   Services.GetRequiredService<IFeeUpdateScheduler>().StopAsync());
                Services.GetRequiredService<IChannelFailureService>().Stop();
                await (Services.GetService<AccountingBooksService>()?.StopAsync() ?? Task.CompletedTask);
                await Task.WhenAll(Services.GetRequiredService<OnionReplayBlockPruner>().StopAsync(),
                                   Services.GetRequiredService<IMempoolReactor>().StopAsync(),
                                   Services.GetService<AccountingEventSealerService>()?.StopAsync()
                                ?? Task.CompletedTask);
                await Services.GetRequiredService<ITorOnionService>().StopAsync();
                await Task.WhenAll(Monitor.StopAsync(), _feeService!.StopAsync(), PeerManager.StopAsync());
                if (_gossipGraph is not null)
                    await _gossipGraph.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            _started = false;
            _gossipGraph = null;
            var services = _services;
            _services = null;
            await services.DisposeAsync();
            if (Database.SqliteFilePath is not null)
            {
                using var connection = new SqliteConnection(Database.ConnectionString);
                SqliteConnection.ClearPool(connection);
            }
        }
    }

    /// <summary>
    /// Hands block <paramref name="height"/> of the chain to the monitor (as ZMQ would) and returns the monitor's time.
    /// </summary>
    public async Task<TimeSpan> DeliverBlockAsync(uint height)
    {
        var watch = Stopwatch.StartNew();
        await Monitor.ProcessNewBlockAsync(_chain[height], height);
        return watch.Elapsed;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _zmq.Dispose();
    }

    private async Task StepAsync(string name, Func<Task> step)
    {
        var watch = Stopwatch.StartNew();
        await step();
        StartSteps.Add((name, watch.Elapsed));
    }

    private ServiceProvider BuildServiceProvider()
    {
        List<KeyValuePair<string, string?>> settings =
        [
            new("Node:Network", "regtest"),
            new("Node:Daemon", "false"),
            new("Database:Provider", Database.ConfigurationProviderName),
            new("Database:ConnectionString", Database.ConnectionString),
            new("Bitcoin:RpcEndpoint", "127.0.0.1:18443"),
            new("Bitcoin:RpcUser", "scale"),
            new("Bitcoin:RpcPassword", "scale"),
            new("Bitcoin:ZmqHost", _zmq.Host),
            new("Bitcoin:ZmqBlockPort", _zmq.BlockPort.ToString()),
            new("Bitcoin:ZmqTxPort", _zmq.TxPort.ToString()),
            // The test hands in every block itself
            new("Bitcoin:TipPollInterval", "00:00:00"),
            new("Bitcoin:WatchMempool", "false"),
            new("FeeEstimation:Source", "Fixed"),
            new("FeeEstimation:FixedFeeRatePerKw", "2500"),
            new("Accounting:Prices:Source", "None"),
            new("Gossip:MaxMemoryMb", "0")
        ];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings)
                                                      .AddInMemoryCollection(ExtraConfiguration)
                                                      .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(new CountingLoggerProvider(WarningCounts))
                                              .SetMinimumLevel(LogLevel.Warning));
        services.AddNltgNodeServices(configuration, KeyManager);
        services.AddSingleton<IBitcoinChainService>(_chain);
        services.AddOptions<NodeOptions>()
                .PostConfigure(options =>
                 {
                     options.ListenAddresses = [$"{IPAddress.Loopback}:{Port}"];
                     options.BitcoinNetwork = Domain.Protocol.ValueObjects.BitcoinNetwork.Regtest;
                     options.Features.ChainHashes = [options.BitcoinNetwork.ChainHash];
                     _configureNodeOptions?.Invoke(options);
                 });
        return services.BuildServiceProvider();
    }

    private sealed class CountingLoggerProvider(ConcurrentDictionary<string, int> counts) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CountingLogger(categoryName, counts);

        public void Dispose()
        {
        }

        private sealed class CountingLogger(string category, ConcurrentDictionary<string, int> counts) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                var shortName = category[(category.LastIndexOf('.') + 1)..];
                var count = counts.AddOrUpdate($"{logLevel} {shortName}", 1, (_, c) => c + 1);
                // The first lines of each kind, for the benchmark's output
                if (count <= 2)
                    Console.WriteLine($"[{logLevel}] {shortName}: {formatter(state, exception)}"
                                    + (exception is null ? string.Empty : $" {exception.GetType().Name}: {exception.Message}"));
            }
        }
    }
}