using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using LNUnit.LND;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq.Protected;
using NBitcoin;
using NBitcoin.RPC;
using NLightning.Tests.Utils;
using ServiceStack;

namespace NLightning.Integration.Tests.Docker.Utils;

using Daemon.Extensions;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Fixtures;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Transport.Interfaces;
using Infrastructure.Transport.Services;
using Mock;

/// <summary>
/// One NLightning node for the Docker tests, built from the daemon's own composition
/// (<see cref="NodeServiceExtensions.AddNltgNodeServices"/>) so the tests exercise the same service graph as
/// <c>nltg</c>. Several can run in one test process (Bob and Carol of the ABCD tests): each has its own name (the
/// prefix of its log lines), port, key and database.
/// </summary>
/// <remarks>
/// The node keeps its key manager and database across <see cref="StopAsync"/> / <see cref="StartAsync"/> (and
/// <see cref="CrashAsync"/>), so a test can restart it and see its channels again. A node made with
/// <see cref="CreateAsync"/> owns its port and SQLite file and releases them in <see cref="DisposeAsync"/>; otherwise the
/// owner releases the port and calls <see cref="DeleteFiles"/>. Server databases (Postgres, SQL Server) are left in
/// their throwaway container. The fee estimation endpoint is replaced by a fixed answer, so the tests never reach
/// the internet.
/// </remarks>
public sealed class NLightningTestNode : IAsyncDisposable
{
    /// <summary>
    /// How many times <see cref="ConnectToAsync(NLightningTestNode, CancellationToken)"/> tries.
    /// </summary>
    public const int MaxConnectAttempts = 3;

    /// <summary>
    /// The reconnect backoff <see cref="CreateAsync"/> gives its nodes (the daemon starts at 5 s).
    /// </summary>
    public static readonly TimeSpan FastReconnectInitialDelay = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan s_openStepTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_bothEndsConnectedTimeout = TimeSpan.FromSeconds(10);

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly Action<NodeOptions>? _configureNodeOptions;
    private readonly bool _ownsResources;

    private readonly ConcurrentQueue<string> _nodeLog = new();

    private CrashableTcpService? _tcpService;
    private IFeeService? _feeService;
    private ServiceProvider? _serviceProvider;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// The node's name, used as the prefix of its log lines (<c>[bob]</c>).
    /// </summary>
    public string Name { get; }

    public TestNodeDatabase Database { get; }

    /// <summary>
    /// The SQLite file, or <c>null</c> when the node runs on a server database.
    /// </summary>
    public string? DatabaseFilePath => Database.SqliteFilePath;

    public ISecureKeyManager SecureKeyManager { get; }
    public int Port { get; }

    /// <summary>
    /// The first wait of the peer manager's reconnect backoff, or <c>null</c> for the daemon's default. Applied on
    /// every <see cref="StartAsync"/>, so set it before starting.
    /// </summary>
    /// <remarks>
    /// Set through reflection on <c>PeerManager.ReconnectInitialDelay</c> (internal, and the Application assembly
    /// grants no internals to this project) until <c>NodeOptions</c> has a reconnect-backoff option.
    /// </remarks>
    public TimeSpan? ReconnectInitialDelay { get; set; }

    public bool IsRunning => _started;

    /// <summary>
    /// Every log line of the node's own (<c>NLightning.*</c>) categories since it was created, across restarts, for
    /// tests that assert on what the node logged.
    /// </summary>
    public IReadOnlyCollection<string> NodeLog => _nodeLog;

    /// <summary>
    /// How many <see cref="NodeLog"/> lines contain <paramref name="fragment"/>.
    /// </summary>
    public int CountLogLines(string fragment) =>
        _nodeLog.Count(line => line.Contains(fragment, StringComparison.Ordinal));

    public CompactPubKey NodeId => SecureKeyManager.GetNodePubKey();
    public string NodeIdHex => Convert.ToHexString(NodeId).ToLowerInvariant();

    /// <summary>
    /// The <c>pubkey@127.0.0.1:port</c> other in-process nodes connect to.
    /// </summary>
    public string Address => $"{NodeIdHex}@{IPAddress.Loopback}:{Port}";

    private string FeeCacheFilePath { get; }

    public IServiceProvider Services =>
        _serviceProvider ?? throw new InvalidOperationException($"The node {Name} has not been started");

    public IPeerManager PeerManager => Services.GetRequiredService<IPeerManager>();
    public IBlockchainMonitor BlockchainMonitor => Services.GetRequiredService<IBlockchainMonitor>();
    public IChannelManager ChannelManager => Services.GetRequiredService<IChannelManager>();

    public IChannelMemoryRepository ChannelMemoryRepository =>
        Services.GetRequiredService<IChannelMemoryRepository>();

    public RPCClient Bitcoin => _fixture.Bitcoin;

    /// <summary>
    /// A SQLite node (the original constructor).
    /// </summary>
    /// <param name="fixture">The running regtest network.</param>
    /// <param name="databaseFilePath">The SQLite file; reused on restart.</param>
    /// <param name="secureKeyManager">The node key; reused on restart.</param>
    /// <param name="port">The port the node listens on.</param>
    /// <param name="configureNodeOptions">Extra <see cref="NodeOptions"/> changes, applied last.</param>
    /// <param name="name">The log prefix.</param>
    public NLightningTestNode(LightningRegtestNetworkFixture fixture, string databaseFilePath,
                              ISecureKeyManager secureKeyManager, int port,
                              Action<NodeOptions>? configureNodeOptions = null, string name = "nltg")
        : this(fixture, name, TestNodeDatabase.Sqlite(databaseFilePath), secureKeyManager, port, configureNodeOptions)
    {
    }

    /// <param name="fixture">The running regtest network.</param>
    /// <param name="name">The log prefix.</param>
    /// <param name="database">The provider and connection string; reused on restart.</param>
    /// <param name="secureKeyManager">The node key; reused on restart.</param>
    /// <param name="port">The port the node listens on.</param>
    /// <param name="configureNodeOptions">Extra <see cref="NodeOptions"/> changes, applied last.</param>
    public NLightningTestNode(LightningRegtestNetworkFixture fixture, string name, TestNodeDatabase database,
                              ISecureKeyManager secureKeyManager, int port,
                              Action<NodeOptions>? configureNodeOptions = null)
        : this(fixture, name, database, secureKeyManager, port, configureNodeOptions, ownsResources: false)
    {
    }

    private NLightningTestNode(LightningRegtestNetworkFixture fixture, string name, TestNodeDatabase database,
                               ISecureKeyManager secureKeyManager, int port,
                               Action<NodeOptions>? configureNodeOptions, bool ownsResources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _fixture = fixture;
        _configureNodeOptions = configureNodeOptions;
        _ownsResources = ownsResources;
        Name = name;
        Database = database;
        SecureKeyManager = secureKeyManager;
        Port = port;
        FeeCacheFilePath = database.SqliteFilePath is not null
                               ? $"{database.SqliteFilePath}.fee_cache.bin"
                               : Path.Combine(Path.GetTempPath(), $"nlightning_{name}_{Guid.NewGuid():N}.fee_cache.bin");
    }

    /// <summary>
    /// Makes a node that owns its port (from <see cref="PortPoolUtil"/>), a fresh random key and, unless
    /// <paramref name="database"/> says otherwise, its own SQLite file; <see cref="DisposeAsync"/> releases them. Its
    /// reconnect backoff starts at <see cref="FastReconnectInitialDelay"/>. The node is not started.
    /// </summary>
    public static async Task<NLightningTestNode> CreateAsync(LightningRegtestNetworkFixture fixture, string name,
                                                        TestNodeDatabase? database = null,
                                                        Action<NodeOptions>? configureNodeOptions = null)
    {
        var port = await PortPoolUtil.GetAvailablePortAsync();
        database ??= TestNodeDatabase.Sqlite($"nlightning_{name}_{Guid.NewGuid():N}.db");
        return new NLightningTestNode(fixture, name, database, new FakeSecureKeyManager(), port,
                                      configureNodeOptions, ownsResources: true)
        {
            ReconnectInitialDelay = FastReconnectInitialDelay
        };
    }

    /// <summary>
    /// Builds the service graph, migrates the database and starts the fee service, the peer manager and the
    /// blockchain monitor, in the daemon's order.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            throw new InvalidOperationException($"The node {Name} is already running");

        _serviceProvider = BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
            await context.Database.MigrateAsync(cancellationToken);
        }

        // A fresh database starts scanning at the current tip; a restarted node resumes from its stored state
        var currentHeight = (uint)await Bitcoin.GetBlockCountAsync(cancellationToken);

        ApplyPeerManagerOverrides();

        // IFeeService is a transient typed HttpClient, so keep the instance we start (the daemon does the same)
        _feeService = Services.GetRequiredService<IFeeService>();
        await _feeService.StartAsync(cancellationToken);
        await PeerManager.StartAsync(cancellationToken);
        await BlockchainMonitor.StartAsync(currentHeight, cancellationToken);
        _started = true;
    }

    /// <summary>
    /// Stops the node the way the daemon does and disposes its service graph. The database and key stay.
    /// </summary>
    public async Task StopAsync()
    {
        if (_serviceProvider is null)
            return;

        if (_started)
        {
            await Task.WhenAll(BlockchainMonitor.StopAsync(), _feeService!.StopAsync(), PeerManager.StopAsync());
            _started = false;
        }

        await _serviceProvider.DisposeAsync();
        _serviceProvider = null;
        _tcpService = null;
    }

    /// <summary>
    /// Simulates a crash: every connection is reset at once (the peers get no final <c>error</c>/<c>warning</c> and
    /// no graceful close) and the node stops listening and connecting, then the services are torn down. The key and
    /// the database stay, so <see cref="StartAsync"/> brings the node back as after a process restart.
    /// </summary>
    /// <remarks>
    /// In-process, a database write that is already running when the sockets reset still completes; a crash between
    /// two persist steps needs a hook in the code under test (e.g. <c>CrashingUnitOfWork</c>).
    /// </remarks>
    public async Task CrashAsync()
    {
        if (!_started || _tcpService is null)
            throw new InvalidOperationException($"The node {Name} is not running");

        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [{Name}] crashing");
        await _tcpService.CrashAsync();

        // Nothing can reach a peer any more; stop the loops and release the database and ZMQ sockets
        await StopAsync();
    }

    /// <summary>
    /// Sends <paramref name="amount"/> to a new wallet address, mines 6 blocks and waits until the monitor has seen
    /// the deposit with 6 confirmations.
    /// </summary>
    public async Task FundWalletAsync(LightningMoney amount, AddressType addressType,
                                      CancellationToken cancellationToken, bool isChange = false)
    {
        using var scope = Services.CreateScope();
        var walletService = scope.ServiceProvider.GetRequiredService<IBitcoinWalletService>();
        var address = await walletService.GetUnusedAddressAsync(addressType, isChange);

        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seenInBlock = uint.MaxValue;

        void OnWalletMovementDetected(object? _, WalletMovementEventArgs e)
        {
            if (e.WalletAddress == address.Address && e.Amount == amount)
                seenInBlock = e.BlockHeight;
        }

        void OnNewBlockDetected(object? _, NewBlockEventArgs e)
        {
            if (seenInBlock != uint.MaxValue && e.Height >= seenInBlock + 5)
                confirmed.TrySetResult();
        }

        BlockchainMonitor.OnWalletMovementDetected += OnWalletMovementDetected;
        BlockchainMonitor.OnNewBlockDetected += OnNewBlockDetected;
        try
        {
            await Bitcoin.SendToAddressAsync(BitcoinAddress.Create(address.Address, NBitcoin.Network.RegTest),
                                             Money.Satoshis((long)amount.Satoshi), cancellationToken);
            await MineBlocksAsync(6, cancellationToken);
            await confirmed.Task.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
        }
        finally
        {
            BlockchainMonitor.OnWalletMovementDetected -= OnWalletMovementDetected;
            BlockchainMonitor.OnNewBlockDetected -= OnNewBlockDetected;
        }
    }

    public async Task MineBlocksAsync(int count, CancellationToken cancellationToken)
    {
        await Bitcoin.GenerateToAddressAsync(count, await Bitcoin.GetNewAddressAsync(cancellationToken),
                                             cancellationToken);
    }

    /// <summary>
    /// Connects to an LND node of the fixture over its container address.
    /// </summary>
    /// <returns>The <c>pubkey@host:port</c> address used.</returns>
    public async Task<string> ConnectToAsync(LNDNodeConnection lndNode, CancellationToken cancellationToken)
    {
        var host = new IPEndPoint(
            (await Dns.GetHostAddressesAsync(lndNode.Host.SplitOnFirst("//")[1].SplitOnFirst(":")[0],
                                             cancellationToken)).First(), 9735);
        var address = $"{Convert.ToHexString(lndNode.LocalNodePubKeyBytes)}@{host}";

        await PeerManager.ConnectToPeerAsync(new PeerAddressInfo(address)).WaitAsync(cancellationToken);
        return address;
    }

    /// <summary>
    /// Connects to another in-process node over <c>127.0.0.1</c> and returns once both ends list each other.
    /// </summary>
    /// <remarks>
    /// Between two NLightning nodes the responder can lose the initiator's <c>init</c> (it arrives before the
    /// responder's message pipeline subscribes, so the responder drops the connection on the next message: NL-239).
    /// Until that is fixed this retries such a half-open connection up to <see cref="MaxConnectAttempts"/> times and
    /// logs every retry; use <see cref="IPeerManager.ConnectToPeerAsync"/> directly to observe the raw behaviour.
    /// </remarks>
    /// <returns>The <c>pubkey@127.0.0.1:port</c> address used.</returns>
    /// <exception cref="InvalidOperationException">Already connected to <paramref name="other"/>.</exception>
    /// <exception cref="TimeoutException">No connection both ends agree on after every attempt.</exception>
    public async Task<string> ConnectToAsync(NLightningTestNode other, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other == this)
            throw new ArgumentException("A node cannot connect to itself", nameof(other));

        if (IsConnectedTo(other.NodeId) && other.IsConnectedTo(NodeId))
            throw new InvalidOperationException($"{Name} is already connected to {other.Name}");

        for (var attempt = 1; attempt <= MaxConnectAttempts; attempt++)
        {
            try
            {
                await PeerManager.ConnectToPeerAsync(new PeerAddressInfo(other.Address)).WaitAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // The other node connected to us in the meantime; wait below for both ends to agree
            }

            try
            {
                await Poll.UntilAsync(() => IsConnectedTo(other.NodeId) && other.IsConnectedTo(NodeId),
                                      s_bothEndsConnectedTimeout, $"{Name} and {other.Name} connected",
                                      cancellationToken);
                return other.Address;
            }
            catch (TimeoutException) when (attempt < MaxConnectAttempts)
            {
                Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [{Name}] connection to {other.Name} is half open "
                                + $"(NL-239 init race?), retrying ({attempt}/{MaxConnectAttempts})");
                if (IsConnectedTo(other.NodeId))
                    PeerManager.DisconnectPeer(other.NodeId);
                if (other.IsConnectedTo(NodeId))
                    other.PeerManager.DisconnectPeer(NodeId);

                await Poll.UntilAsync(() => !IsConnectedTo(other.NodeId) && !other.IsConnectedTo(NodeId),
                                      s_bothEndsConnectedTimeout, $"{Name} and {other.Name} disconnected",
                                      cancellationToken);
            }
        }

        throw new UnreachableException();
    }

    /// <summary>
    /// Whether the peer manager has a live connection to <paramref name="peerId"/>.
    /// </summary>
    public bool IsConnectedTo(CompactPubKey peerId) => _started && PeerManager.GetPeer(peerId) is not null;

    /// <summary>
    /// Opens a channel through the daemon's client handlers and follows it: mines 6 blocks once funding_signed
    /// arrives and returns when <c>channel_ready</c> was sent or received.
    /// </summary>
    public async Task<OpenChannelClientSubscriptionResponse> OpenChannelAsync(OpenChannelClientRequest request,
                                                                              CancellationToken cancellationToken)
    {
        OpenChannelClientResponse openResponse;
        using (var scope = Services.CreateScope())
        {
            var handler = scope.ServiceProvider
                               .GetRequiredService<IClientCommandHandler<OpenChannelClientRequest,
                                    OpenChannelClientResponse>>();
            openResponse = await handler.HandleAsync(request, cancellationToken)
                                        .WaitAsync(s_openStepTimeout, cancellationToken);
        }

        while (true)
        {
            OpenChannelClientSubscriptionResponse state;
            using (var scope = Services.CreateScope())
            {
                var handler = scope.ServiceProvider
                                   .GetRequiredService<IClientCommandHandler<OpenChannelClientSubscriptionRequest,
                                        OpenChannelClientSubscriptionResponse>>();
                // The subscription misses a peer error sent for the temporary channel id, so never wait forever
                state = await handler.HandleAsync(new OpenChannelClientSubscriptionRequest(openResponse.ChannelId),
                                                  cancellationToken)
                                     .WaitAsync(s_openStepTimeout, cancellationToken);
            }

            if (state.ChannelState == ChannelState.V1FundingSigned)
                await MineBlocksAsync(6, cancellationToken);
            else if (state.ChannelState is ChannelState.ReadyForThem or ChannelState.ReadyForUs
                                        or ChannelState.Open)
                return state;
        }
    }

    /// <summary>
    /// Lists the node's channels through the daemon's <c>ListChannels</c> client handler.
    /// </summary>
    public async Task<ListChannelsClientResponse> ListChannelsAsync(CancellationToken cancellationToken)
    {
        using var scope = Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<ListChannelsClientRequest,
                                ListChannelsClientResponse>>();
        return await handler.HandleAsync(new ListChannelsClientRequest(), cancellationToken);
    }

    /// <summary>
    /// Deletes the node's SQLite file (with its WAL/SHM side files) and fee cache. Call after <see cref="StopAsync"/>.
    /// A server database is left alone: it dies with its container.
    /// </summary>
    public void DeleteFiles()
    {
        List<string> files = [FeeCacheFilePath];
        if (DatabaseFilePath is not null)
            files.AddRange([DatabaseFilePath, $"{DatabaseFilePath}-wal", $"{DatabaseFilePath}-shm"]);

        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete {file}: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await StopAsync();
        _disposed = true;

        if (!_ownsResources)
            return;

        DeleteFiles();
        PortPoolUtil.ReleasePort(Port);
    }

    public override string ToString() => $"{Name} ({NodeIdHex[..16]}…, :{Port}, {Database.Provider})";

    /// <summary>
    /// Test-only knobs the peer manager keeps internal (see <see cref="ReconnectInitialDelay"/>).
    /// </summary>
    private void ApplyPeerManagerOverrides()
    {
        if (ReconnectInitialDelay is not { } delay)
            return;

        var peerManager = PeerManager as Application.Node.Managers.PeerManager
                       ?? throw new InvalidOperationException($"Unexpected peer manager {PeerManager.GetType()}");
        SetNonPublicProperty(peerManager, "ReconnectInitialDelay", delay);
    }

    private static void SetNonPublicProperty(object target, string propertyName, object value)
    {
        var property = target.GetType()
                             .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public
                                                                               | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException(
                           $"{target.GetType().Name}.{propertyName} is gone; update the test hook in "
                         + nameof(NLightningTestNode));
        property.SetValue(target, value);
    }

    private ServiceProvider BuildServiceProvider()
    {
        Assert.NotNull(_fixture.Builder);
        var bitcoinConfiguration = _fixture.Builder.Configuration.BTCNodes[0];
        var zmqRawBlockPort = bitcoinConfiguration.Cmd.First(c => c.Contains("-zmqpubrawblock")).Split(':')[2];
        var zmqRawTxPort = bitcoinConfiguration.Cmd.First(c => c.Contains("-zmqpubrawtx")).Split(':')[2];
        var bitcoin = Bitcoin;

        List<KeyValuePair<string, string?>> inMemoryConfiguration =
        [
            new("Node:Network", "regtest"),
            new("Node:Daemon", "false"),
            new("Database:Provider", Database.ConfigurationProviderName),
            new("Database:ConnectionString", Database.ConnectionString),
            new("Bitcoin:RpcEndpoint", bitcoin.Address.ToString()),
            new("Bitcoin:RpcUser", bitcoin.CredentialString.UserPassword.UserName),
            new("Bitcoin:RpcPassword", bitcoin.CredentialString.UserPassword.Password),
            new("Bitcoin:ZmqHost", bitcoin.Address.Host),
            new("Bitcoin:ZmqBlockPort", zmqRawBlockPort),
            new("Bitcoin:ZmqTxPort", zmqRawTxPort),
            new("FeeEstimation:CacheFile", FeeCacheFilePath)
        ];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfiguration).Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(new TestConsoleLoggerProvider(Name, _nodeLog))
                                              .SetMinimumLevel(LogLevel.Debug));

        // The daemon's composition
        services.AddNltgNodeServices(configuration, SecureKeyManager);

        // Test-only overrides: a fixed fee estimate, our own port and network, and a TCP service CrashAsync can reset
        services.AddHttpClient<IFeeService, Infrastructure.Bitcoin.Services.FeeService>()
                .ConfigurePrimaryHttpMessageHandler(CreateFixedFeeHandler);
        services.AddOptions<NodeOptions>()
                .PostConfigure(options =>
                 {
                     options.Features = new FeatureOptions { ChainHashes = [ChainConstants.Regtest] };
                     options.ListenAddresses = [$"{IPAddress.Loopback}:{Port}"];
                     options.BitcoinNetwork = BitcoinNetwork.Regtest;
                     options.Features.ChainHashes = [options.BitcoinNetwork.ChainHash];
                     _configureNodeOptions?.Invoke(options);
                 });
        services.AddSingleton<TcpService>();
        services.AddSingleton<ITcpService>(sp =>
        {
            _tcpService = new CrashableTcpService(sp.GetRequiredService<TcpService>());
            return _tcpService;
        });

        return services.BuildServiceProvider();
    }

    private static HttpMessageHandler CreateFixedFeeHandler()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
               .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(),
                                                 ItExpr.IsAny<CancellationToken>())
               .ReturnsAsync(() => new HttpResponseMessage
               {
                   StatusCode = HttpStatusCode.OK,
                   Content = new StringContent("{\"fastestFee\": 10}")
               });
        return handler.Object;
    }

    /// <summary>
    /// Writes log lines, prefixed with the node's name, to <see cref="Console"/>, which the Docker tests redirect to
    /// the xUnit output.
    /// </summary>
    private sealed class TestConsoleLoggerProvider(string nodeName, ConcurrentQueue<string> nodeLog) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            new TestConsoleLogger(nodeName, categoryName,
                                  categoryName.StartsWith("NLightning.", StringComparison.Ordinal) ? nodeLog : null);

        public void Dispose()
        {
        }

        private sealed class TestConsoleLogger(string nodeName, string categoryName, ConcurrentQueue<string>? nodeLog)
            : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [{nodeName}] [{logLevel}] {categoryName}: "
                         + formatter(state, exception)
                         + (exception is null ? string.Empty : $" {exception}");
                nodeLog?.Enqueue(line);
                Console.WriteLine(line);
            }
        }
    }
}