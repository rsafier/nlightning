using System.Net;
using LNUnit.LND;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq.Protected;
using NBitcoin;
using NBitcoin.RPC;
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

/// <summary>
/// One NLightning node for the Docker tests, built from the daemon's own composition
/// (<see cref="NodeServiceExtensions.AddNltgNodeServices"/>) so the tests exercise the same service graph as
/// <c>nltg</c>.
/// </summary>
/// <remarks>
/// The node keeps its key manager and SQLite file across <see cref="StopAsync"/> / <see cref="StartAsync"/>, so a
/// test can restart it and see its channels again. The owner releases the port and deletes the database file.
/// The fee estimation endpoint is replaced by a fixed answer, so the tests never reach the internet.
/// </remarks>
public sealed class NLightningTestNode : IAsyncDisposable
{
    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly Action<NodeOptions>? _configureNodeOptions;

    private IFeeService? _feeService;
    private ServiceProvider? _serviceProvider;
    private bool _started;

    public string DatabaseFilePath { get; }
    public ISecureKeyManager SecureKeyManager { get; }
    public int Port { get; }

    private string FeeCacheFilePath => $"{DatabaseFilePath}.fee_cache.bin";

    public IServiceProvider Services =>
        _serviceProvider ?? throw new InvalidOperationException("The node has not been started");

    public IPeerManager PeerManager => Services.GetRequiredService<IPeerManager>();
    public IBlockchainMonitor BlockchainMonitor => Services.GetRequiredService<IBlockchainMonitor>();

    public IChannelMemoryRepository ChannelMemoryRepository =>
        Services.GetRequiredService<IChannelMemoryRepository>();

    public RPCClient Bitcoin => _fixture.Builder?.BitcoinRpcClient
                             ?? throw new InvalidOperationException("The regtest network is not running");

    /// <param name="fixture">The running regtest network.</param>
    /// <param name="databaseFilePath">The SQLite file; reused on restart.</param>
    /// <param name="secureKeyManager">The node key; reused on restart.</param>
    /// <param name="port">The port the node listens on.</param>
    /// <param name="configureNodeOptions">Extra <see cref="NodeOptions"/> changes, applied last.</param>
    public NLightningTestNode(LightningRegtestNetworkFixture fixture, string databaseFilePath,
                              ISecureKeyManager secureKeyManager, int port,
                              Action<NodeOptions>? configureNodeOptions = null)
    {
        _fixture = fixture;
        _configureNodeOptions = configureNodeOptions;
        DatabaseFilePath = databaseFilePath;
        SecureKeyManager = secureKeyManager;
        Port = port;
    }

    /// <summary>
    /// Builds the service graph, migrates the database and starts the fee service, the peer manager and the
    /// blockchain monitor, in the daemon's order.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
            throw new InvalidOperationException("The node is already running");

        _serviceProvider = BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
            await context.Database.MigrateAsync(cancellationToken);
        }

        // A fresh database starts scanning at the current tip; a restarted node resumes from its stored state
        var currentHeight = (uint)await Bitcoin.GetBlockCountAsync(cancellationToken);

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

        await PeerManager.ConnectToPeerAsync(new PeerAddressInfo(address));
        return address;
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
            openResponse = await handler.HandleAsync(request, cancellationToken);
        }

        while (true)
        {
            OpenChannelClientSubscriptionResponse state;
            using (var scope = Services.CreateScope())
            {
                var handler = scope.ServiceProvider
                                   .GetRequiredService<IClientCommandHandler<OpenChannelClientSubscriptionRequest,
                                        OpenChannelClientSubscriptionResponse>>();
                state = await handler.HandleAsync(new OpenChannelClientSubscriptionRequest(openResponse.ChannelId),
                                                  cancellationToken);
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
    /// </summary>
    public void DeleteFiles()
    {
        foreach (var file in new[]
                 {
                     DatabaseFilePath, $"{DatabaseFilePath}-wal", $"{DatabaseFilePath}-shm", FeeCacheFilePath
                 })
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
        await StopAsync();
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
            new("Database:Provider", "Sqlite"),
            new("Database:ConnectionString", $"Data Source={DatabaseFilePath}"),
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
        services.AddLogging(builder => builder.AddProvider(new TestConsoleLoggerProvider())
                                              .SetMinimumLevel(LogLevel.Debug));

        // The daemon's composition
        services.AddNltgNodeServices(configuration, SecureKeyManager);

        // Test-only overrides: a fixed fee estimate, and our own port and network
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
    /// Writes log lines to <see cref="Console"/>, which the Docker tests redirect to the xUnit output.
    /// </summary>
    private sealed class TestConsoleLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new TestConsoleLogger(categoryName);

        public void Dispose()
        {
        }

        private sealed class TestConsoleLogger(string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [{logLevel}] {categoryName}: "
                                + formatter(state, exception)
                                + (exception is null ? string.Empty : $" {exception}"));
            }
        }
    }
}