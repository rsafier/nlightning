using System.Collections.Concurrent;
using System.Net;
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

using Application.Channels.Fees;
using Application.Channels.Safety.Interfaces;
using Application.Onchain.Mempool;
using Application.Payments.Send.Interfaces;
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
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Services;
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
    /// What <c>PeerService</c> logs when the first message of a connection is not <c>init</c>. Between two of our
    /// nodes this was the responder losing the initiator's <c>init</c> (NL-239, fixed); tests assert it is never
    /// logged.
    /// </summary>
    public const string InitLostLogFragment = "Failed to receive init message";

    /// <summary>
    /// What a node logs (in the exception text) when writing its own <c>init</c> fails. On a simultaneous connect
    /// between two of our nodes this was a live connection looking closed to the writer, which the other end then
    /// kept (NL-240, fixed); tests assert it is never logged.
    /// </summary>
    public const string InitWriteFailedLogFragment = "Error initializing peer communication";

    /// <summary>
    /// The reconnect backoff <see cref="CreateAsync"/> gives its nodes (the daemon starts at 5 s).
    /// </summary>
    public static readonly TimeSpan FastReconnectInitialDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a new connection between two NLightning nodes must stay up before
    /// <see cref="ConnectToAsync(NLightningTestNode, CancellationToken)"/> counts it (the connect bugs NL-239/NL-240
    /// used to tear a connection down about 100 ms after both ends listed it).
    /// </summary>
    public static readonly TimeSpan ConnectionStableWindow = TimeSpan.FromSeconds(1.5);

    private static readonly TimeSpan s_openStepTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_bothEndsConnectedTimeout = TimeSpan.FromSeconds(10);

    private readonly Lazy<RegtestBitcoinEndpoint> _bitcoinEndpoint;
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
    /// <remarks>Applied as <c>NodeOptions.ReconnectInitialDelay</c>, before the caller's own option changes.</remarks>
    public TimeSpan? ReconnectInitialDelay { get; set; }

    /// <summary>
    /// <c>Bitcoin:WatchMempool</c> (default true, BOLT 5 O8): false leaves the chain monitor without the ZMQ
    /// <c>rawtx</c> loop, so nothing reacts to unconfirmed spends. Applied on every <see cref="StartAsync"/>.
    /// </summary>
    public bool WatchMempool { get; set; } = true;

    /// <summary>
    /// Last changes to the node's services, applied on every <see cref="StartAsync"/> after the daemon's composition and
    /// the test overrides (e.g. a test-only decorator of the HTLC switch). Set it before starting.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>
    /// Extra configuration keys (e.g. <c>Gossip:Enabled</c>, <c>Node:Alias</c>), layered over the test node's own
    /// settings on every <see cref="StartAsync"/>, so a key here overrides a default. Empty by default: the node then
    /// behaves as before. Set them before starting.
    /// </summary>
    public IDictionary<string, string?> ExtraConfiguration { get; } = new Dictionary<string, string?>();

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

    public RPCClient Bitcoin => _bitcoinEndpoint.Value.Rpc;

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
        : this(() => RegtestBitcoinEndpoint.FromFixture(fixture), name, database, secureKeyManager, port,
               configureNodeOptions, ownsResources: false)
    {
    }

    private NLightningTestNode(Func<RegtestBitcoinEndpoint> bitcoinEndpoint, string name, TestNodeDatabase database,
                               ISecureKeyManager secureKeyManager, int port,
                               Action<NodeOptions>? configureNodeOptions, bool ownsResources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // Resolved once (a failed resolution is not cached, so a node built before its fixture was ready retries)
        _bitcoinEndpoint = new Lazy<RegtestBitcoinEndpoint>(bitcoinEndpoint, LazyThreadSafetyMode.PublicationOnly);
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
    public static Task<NLightningTestNode> CreateAsync(LightningRegtestNetworkFixture fixture, string name,
                                                       TestNodeDatabase? database = null,
                                                       Action<NodeOptions>? configureNodeOptions = null) =>
        CreateAsync(() => RegtestBitcoinEndpoint.FromFixture(fixture), name, database, configureNodeOptions);

    /// <summary>
    /// As <see cref="CreateAsync(LightningRegtestNetworkFixture, string, TestNodeDatabase?, Action{NodeOptions}?)"/>,
    /// for a node on a bitcoind outside the shared regtest network (e.g. the CLN interop fixture's own).
    /// </summary>
    public static Task<NLightningTestNode> CreateAsync(RegtestBitcoinEndpoint bitcoin, string name,
                                                       TestNodeDatabase? database = null,
                                                       Action<NodeOptions>? configureNodeOptions = null) =>
        CreateAsync(() => bitcoin, name, database, configureNodeOptions);

    private static async Task<NLightningTestNode> CreateAsync(Func<RegtestBitcoinEndpoint> bitcoinEndpoint,
                                                              string name, TestNodeDatabase? database,
                                                              Action<NodeOptions>? configureNodeOptions)
    {
        var port = await PortPoolUtil.GetAvailablePortAsync();
        database ??= TestNodeDatabase.Sqlite($"nlightning_{name}_{Guid.NewGuid():N}.db");
        return new NLightningTestNode(bitcoinEndpoint, name, database, new FakeSecureKeyManager(), port,
                                      configureNodeOptions, ownsResources: true)
        {
            ReconnectInitialDelay = FastReconnectInitialDelay
        };
    }

    /// <summary>
    /// Builds the service graph, migrates the database and starts the fee service, the peer manager and the
    /// blockchain monitor, in the daemon's order.
    /// </summary>
    /// <remarks>
    /// If a step fails, the services started so far are stopped and the service graph is disposed (releasing the
    /// listener, ZMQ and database connections) before the exception propagates, so the node can be started again.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            throw new InvalidOperationException($"The node {Name} is already running");

        _serviceProvider = BuildServiceProvider();
        var feeServiceStarted = false;
        var peerManagerStarted = false;
        var safetyStarted = false;
        try
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
                await context.Database.MigrateAsync(cancellationToken);
            }

            // A fresh database starts scanning at the current tip; a restarted node resumes from its stored state
            var currentHeight = (uint)await Bitcoin.GetBlockCountAsync(cancellationToken);

            // IFeeService is one shared singleton (AddFeeServices): the instance we start is the one every consumer reads
            _feeService = Services.GetRequiredService<IFeeService>();
            await _feeService.StartAsync(cancellationToken);
            feeServiceStarted = true;
            await PeerManager.StartAsync(cancellationToken);
            peerManagerStarted = true;
            // As the daemon does: settle the payments a crash left without an HTLC id once every channel is loaded
            await Services.GetRequiredService<IPaymentOutcomeHandler>().ReconcileInFlightPaymentsAsync(cancellationToken);
            // As the daemon does: the N9 safety services and the update_fee rounds (off unless a test enables them)
            Services.GetRequiredService<IChannelFailureService>().Start();
            Services.GetRequiredService<IHtlcExpiryMonitor>().Start();
            await Services.GetRequiredService<IFeeUpdateScheduler>().StartAsync(cancellationToken);
            safetyStarted = true;
            // As the daemon does: BOLT 5 O8, unconfirmed spends of our outputs (before the monitor's mempool loop)
            Services.GetRequiredService<IMempoolReactor>().Start();
            await BlockchainMonitor.StartAsync(currentHeight, cancellationToken);
            // As the daemon does: prune the onion replay set on every block (NL-327)
            Services.GetRequiredService<OnionReplayBlockPruner>().Start();
            _started = true;
        }
        catch
        {
            await AbortStartAsync(feeServiceStarted, peerManagerStarted, safetyStarted);
            throw;
        }
    }

    /// <summary>
    /// Stops the node the way the daemon does and disposes its service graph. The database and key stay. The
    /// service graph is disposed, and the node counts as stopped, even when a service fails to stop.
    /// </summary>
    public async Task StopAsync()
    {
        if (_serviceProvider is null)
            return;

        try
        {
            if (_started)
            {
                await StopSafetyServicesAsync();
                await Task.WhenAll(Services.GetRequiredService<OnionReplayBlockPruner>().StopAsync(),
                                   Services.GetRequiredService<IMempoolReactor>().StopAsync());
                await Task.WhenAll(BlockchainMonitor.StopAsync(), _feeService!.StopAsync(), PeerManager.StopAsync());
            }
        }
        finally
        {
            _started = false;
            await DisposeServiceProviderAsync();
        }
    }

    /// <summary>
    /// Simulates a crash <b>on the wire</b>: every connection is reset at once (the peers get no final
    /// <c>error</c>/<c>warning</c> and no graceful close) and the node stops listening and connecting, then the
    /// services are torn down. The key and the database stay, so <see cref="StartAsync"/> brings the node back as
    /// after a process restart.
    /// </summary>
    /// <remarks>
    /// This is not a crash for persistence: after the reset the services are stopped with the graceful
    /// <see cref="StopAsync"/>, and <c>PeerManager.StopAsync</c> waits for the inbound message loops and disconnects
    /// every peer, so whatever those loops persist is flushed to the database. It covers what the peers see; a crash
    /// between two persist steps needs a hook in the code under test (e.g. <c>CrashingUnitOfWork</c>).
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
    /// Connects to another in-process node over <c>127.0.0.1</c> and returns once both ends list each other and the
    /// connection stayed up for <see cref="ConnectionStableWindow"/>. There is no retry: since NL-239/NL-240 were
    /// fixed a connect between two NLightning nodes must work the first time.
    /// </summary>
    /// <returns>The <c>pubkey@127.0.0.1:port</c> address used.</returns>
    /// <exception cref="InvalidOperationException">Already connected to <paramref name="other"/>.</exception>
    /// <exception cref="TimeoutException">The connection did not come up on both ends, or dropped.</exception>
    public async Task<string> ConnectToAsync(NLightningTestNode other, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other == this)
            throw new ArgumentException("A node cannot connect to itself", nameof(other));

        if (IsConnectedTo(other.NodeId) && other.IsConnectedTo(NodeId))
            throw new InvalidOperationException($"{Name} is already connected to {other.Name}");

        try
        {
            await PeerManager.ConnectToPeerAsync(new PeerAddressInfo(other.Address)).WaitAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The other node connected to us in the meantime; wait below for both ends to agree
        }

        var failure = await WaitForStableConnectionAsync(other, cancellationToken);
        return failure is null ? other.Address : throw new TimeoutException($"{Name} -> {other.Name}: {failure}");
    }

    /// <summary>
    /// Waits until both ends list each other, then checks the connection stays up for
    /// <see cref="ConnectionStableWindow"/>.
    /// </summary>
    /// <returns><c>null</c> for a stable connection, otherwise what went wrong.</returns>
    public async Task<string?> WaitForStableConnectionAsync(NLightningTestNode other,
                                                            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(other);
        try
        {
            await Poll.UntilAsync(() => IsConnectedTo(other.NodeId) && other.IsConnectedTo(NodeId),
                                  s_bothEndsConnectedTimeout, $"{Name} and {other.Name} connected",
                                  cancellationToken);
        }
        catch (TimeoutException)
        {
            return $"{Name} and {other.Name} did not both list each other within {s_bothEndsConnectedTimeout}";
        }

        return await Poll.HoldsAsync(() => IsConnectedTo(other.NodeId) && other.IsConnectedTo(NodeId),
                                     ConnectionStableWindow, cancellationToken)
                   ? null
                   : $"the connection between {Name} and {other.Name} dropped within {ConnectionStableWindow}";
    }

    /// <summary>
    /// Whether the peer manager has a live connection to <paramref name="peerId"/>.
    /// </summary>
    public bool IsConnectedTo(CompactPubKey peerId) => _started && PeerManager.GetPeer(peerId) is not null;

    /// <summary>
    /// The EF Core provider the running node's database context actually uses (e.g.
    /// <c>Npgsql.EntityFrameworkCore.PostgreSQL</c>), read from a fresh <see cref="NLightningDbContext"/>.
    /// </summary>
    public string? GetEfProviderName()
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database.ProviderName;
    }

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
    /// Undoes a failed <see cref="StartAsync"/>: stops what was started (best effort; the start failure is the error
    /// that matters) and disposes the service graph.
    /// </summary>
    private async Task AbortStartAsync(bool feeServiceStarted, bool peerManagerStarted, bool safetyStarted)
    {
        try
        {
            if (safetyStarted)
            {
                await StopSafetyServicesAsync();
                await Services.GetRequiredService<IMempoolReactor>().StopAsync();
            }
            if (peerManagerStarted)
                await PeerManager.StopAsync();
            if (feeServiceStarted)
                await _feeService!.StopAsync();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[{Name}] failed to stop after a failed start: {e.Message}");
        }
        finally
        {
            await DisposeServiceProviderAsync();
        }
    }

    /// <summary>
    /// Stops the HTLC deadline monitor, the fail-the-channel service and the fee rounds, before the chain monitor and
    /// the peers they use (the daemon's order).
    /// </summary>
    private async Task StopSafetyServicesAsync()
    {
        await Task.WhenAll(Services.GetRequiredService<IHtlcExpiryMonitor>().StopAsync(),
                           Services.GetRequiredService<IFeeUpdateScheduler>().StopAsync());
        Services.GetRequiredService<IChannelFailureService>().Stop();
    }

    private async Task DisposeServiceProviderAsync()
    {
        var serviceProvider = _serviceProvider;
        _serviceProvider = null;
        _feeService = null;
        _tcpService = null;
        if (serviceProvider is not null)
            await serviceProvider.DisposeAsync();
    }

    private ServiceProvider BuildServiceProvider()
    {
        var endpoint = _bitcoinEndpoint.Value;
        var bitcoin = endpoint.Rpc;

        List<KeyValuePair<string, string?>> inMemoryConfiguration =
        [
            new("Node:Network", "regtest"),
            new("Node:Daemon", "false"),
            new("Database:Provider", Database.ConfigurationProviderName),
            new("Database:ConnectionString", Database.ConnectionString),
            new("Bitcoin:RpcEndpoint", bitcoin.Address.ToString()),
            new("Bitcoin:RpcUser", bitcoin.CredentialString.UserPassword.UserName),
            new("Bitcoin:RpcPassword", bitcoin.CredentialString.UserPassword.Password),
            new("Bitcoin:ZmqHost", endpoint.ZmqHost),
            new("Bitcoin:ZmqBlockPort", endpoint.ZmqBlockPort.ToString()),
            new("Bitcoin:ZmqTxPort", endpoint.ZmqTxPort.ToString()),
            new("FeeEstimation:CacheFile", FeeCacheFilePath),
            // Deterministic Docker runs: no periodic update_fee (FeeUpdateFlowTests run rounds by hand; a test can turn
            // it on through configureNodeOptions)
            new("Node:FeeUpdates:Enabled", "false"),
            new("Bitcoin:WatchMempool", WatchMempool ? "true" : "false")
        ];
        // A later source overrides an earlier one, so ExtraConfiguration wins over the defaults above
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfiguration)
                                                      .AddInMemoryCollection(ExtraConfiguration)
                                                      .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(new TestConsoleLoggerProvider(Name, _nodeLog))
                                              .SetMinimumLevel(LogLevel.Debug));

        // The daemon's composition
        services.AddNltgNodeServices(configuration, SecureKeyManager);

        // Test-only overrides: a fixed fee estimate, our own port and network, and a TCP service CrashAsync can reset
        services.AddFeeServices(_ => CreateFixedFeeHandler());
        services.AddOptions<NodeOptions>()
                .PostConfigure(options =>
                 {
                     options.Features = new FeatureOptions { ChainHashes = [ChainConstants.Regtest] };
                     options.ListenAddresses = [$"{IPAddress.Loopback}:{Port}"];
                     options.BitcoinNetwork = BitcoinNetwork.Regtest;
                     options.Features.ChainHashes = [options.BitcoinNetwork.ChainHash];
                     if (ReconnectInitialDelay is { } reconnectInitialDelay)
                         options.ReconnectInitialDelay = reconnectInitialDelay;
                     _configureNodeOptions?.Invoke(options);
                 });
        services.AddSingleton<TcpService>();
        services.AddSingleton<ITcpService>(sp =>
        {
            _tcpService = new CrashableTcpService(sp.GetRequiredService<TcpService>());
            return _tcpService;
        });
        ConfigureServices?.Invoke(services);

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