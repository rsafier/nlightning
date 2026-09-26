using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Options;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Memory;

/// <summary>
/// The real <see cref="BlockchainMonitorService"/> over a real SQLite unit of work and a <see cref="FakeBitcoinChain"/>
/// (BOLT 5 plan O0): blocks are handed to it as ZMQ would, a restart is a new monitor over the same database, and
/// <see cref="FailSaves"/> makes every save that writes the header ring fail inside its transaction.
/// </summary>
internal sealed class ChainMonitorHarness : IAsyncDisposable
{
    private readonly FailingHeaderSaveInterceptor _interceptor = new();
    private readonly ServiceProvider _services;

    public SqliteTestDatabase Db { get; } = new();
    public FakeBitcoinChain Chain { get; }
    public BlockchainMonitorService Monitor { get; private set; }

    /// <summary>When true, every save that writes a block header throws (after its earlier statements ran).</summary>
    public bool FailSaves
    {
        get => _interceptor.Armed;
        set => _interceptor.Armed = value;
    }

    public ChainMonitorHarness(uint tipHeight = 100)
    {
        Chain = new FakeBitcoinChain(tipHeight);
        var services = new ServiceCollection();
        services.AddSingleton<IUtxoMemoryRepository, UtxoMemoryRepository>();
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(Db.CreateContext(_interceptor),
                                                             NullLogger<UnitOfWork>.Instance, new Sha256(),
                                                             sp.GetRequiredService<IUtxoMemoryRepository>()));
        _services = services.BuildServiceProvider();
        Monitor = CreateMonitor();
    }

    public NLightningDbContext Context() => Db.CreateContext();

    /// <summary>Starts the monitor with the state (if new) at <paramref name="heightOfBirth"/>.</summary>
    public Task StartAsync(uint heightOfBirth) =>
        Monitor.StartAsync(heightOfBirth, TestContext.Current.CancellationToken);

    /// <summary>
    /// Stops the monitor (unless <paramref name="alreadyStopped"/>) and starts a new one over the same database (a node
    /// restart); <paramref name="beforeStart"/> subscribes to the new monitor's events.
    /// </summary>
    public async Task RestartAsync(Action<BlockchainMonitorService>? beforeStart = null, bool alreadyStopped = false)
    {
        if (!alreadyStopped)
            await Monitor.StopAsync();
        Monitor = CreateMonitor();
        beforeStart?.Invoke(Monitor);
        await StartAsync(0);
    }

    /// <summary>Mines a block holding <paramref name="transactions"/> (and the mempool) and hands it to the monitor.</summary>
    public async Task<Block> MineAndDeliverAsync(params Transaction[] transactions)
    {
        var block = Chain.Mine(transactions);
        await Monitor.ProcessNewBlockAsync(block, Chain.TipHeight);
        return block;
    }

    /// <summary>Hands the chain's tip to the monitor again (the ZMQ notification of the active tip).</summary>
    public Task DeliverTipAsync() => Monitor.ProcessNewBlockAsync(Chain[Chain.TipHeight], Chain.TipHeight);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Monitor.StopAsync();
        }
        catch (InvalidOperationException)
        {
            // Never started
        }

        await _services.DisposeAsync();
        Db.Dispose();
    }

    private BlockchainMonitorService CreateMonitor()
    {
        var bitcoinOptions = Options.Create(new BitcoinOptions
        {
            RpcEndpoint = "",
            RpcUser = "",
            RpcPassword = "",
            ZmqHost = "127.0.0.1",
            ZmqBlockPort = 28332,
            ZmqTxPort = 28333
        });
        var nodeOptions = Options.Create(new NodeOptions { BitcoinNetwork = "regtest" });
        return new BlockchainMonitorService(bitcoinOptions, Chain, NullLogger<BlockchainMonitorService>.Instance,
                                            nodeOptions, _services)
        {
            BlockRetryBaseDelay = TimeSpan.Zero,
            MaxBlockProcessingAttempts = 2
        };
    }

    /// <summary>Throws from the command that writes the header ring, inside the save's transaction.</summary>
    private sealed class FailingHeaderSaveInterceptor : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
                                                                         InterceptionResult<DbDataReader> result)
        {
            Check(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
                                                                  InterceptionResult<int> result)
        {
            Check(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return ValueTask.FromResult(result);
        }

        private void Check(DbCommand command)
        {
            if (Armed && command.CommandText.Contains("INSERT INTO \"BlockHeaders\"", StringComparison.Ordinal))
                throw new SimulatedCrashException(1);
        }
    }
}

/// <summary>Database helpers for the chain-monitor tests.</summary>
internal static class ChainMonitorHarnessExtensions
{
    /// <summary>A stored channel whose funding output is <paramref name="fundingTransaction"/>:<paramref name="fundingOutputIndex"/>.</summary>
    public static async Task SeedChannelAsync(this ChainMonitorHarness harness, byte seed,
                                              Transaction fundingTransaction, ushort fundingOutputIndex,
                                              ChannelState state)
    {
        await using var context = harness.Context();
        await ChainWatchSchemaRoundTrip.SeedChannelAsync(context, DatabaseType.Sqlite,
                                                         ChainWatchSchemaRoundTrip.ChannelIdOf(seed),
                                                         new TxId(fundingTransaction.GetHash().ToBytes()),
                                                         fundingOutputIndex, state,
                                                         TestContext.Current.CancellationToken);
    }
}