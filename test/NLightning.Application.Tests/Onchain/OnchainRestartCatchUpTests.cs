using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Onchain;

using Application.Channels.Services;
using Application.Onchain;
using Channels.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;

/// <summary>
/// NL-311 restart proof on a real SQLite database: a crash between the save of a channel's resolution rows and watches
/// and <c>TrackWatchedOutpoint</c> lets the chain monitor process the block that spends a watched output without the
/// watch. After the restart the monitor tracks the saved watch again but never rescans that block, so the executor's
/// first round of the new process must find the spend (BOLT 5: "MUST monitor the blockchain for transactions that spend
/// any output that is not irrevocably resolved").
/// </summary>
public sealed class OnchainRestartCatchUpTests : IDisposable
{
    private const uint SpentAt = 1_000;

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"nltg-nl311-{Guid.NewGuid():N}.db");

    private readonly RealSigningCommitmentPair _pair = new(hasAnchors: false);
    private readonly FakeBitcoinChain _chain = new(SpentAt - 1);
    private readonly RecordingResolver _resolver = new();
    private readonly Mock<IChainBroadcaster> _broadcaster = new();
    private readonly Mock<IOutpointWatcher> _outpointWatcher = new();
    private bool _chainUnreachable;

    [Fact]
    public async Task Given_CrashBetweenSaveAndTrack_When_Restarted_Then_TheSpendMinedMeanwhileIsResolved()
    {
        // Arrange: the first process classified the funding spend and saved the close, the rows and their watches in
        // one save, then died before tracking them
        var (commitmentTxId, spend) = await RunFirstProcessUntilTheCrashAsync();

        // Act: the restarted process loads the channel and runs its first block round
        await using var provider = StartProcess();
        var executor = await LoadAndCreateExecutorAsync(provider);
        await executor.RunRoundAsync(_chain.TipHeight, TestContext.Current.CancellationToken);

        // Assert: the spend is resolved at its block and recorded on the stored watch
        await AssertResolvedAsync(provider, commitmentTxId, spend);
        Assert.Equal(new uint256(spend.GetHash()), new uint256(Assert.Single(_resolver.Spends).Spender.TxId));
    }

    [Fact]
    public async Task Given_ChainUnreachableInTheFirstRoundAfterARestart_When_NextRound_Then_TheCatchUpRunsThen()
    {
        // Arrange
        var (commitmentTxId, spend) = await RunFirstProcessUntilTheCrashAsync();
        await using var provider = StartProcess();
        var executor = await LoadAndCreateExecutorAsync(provider);

        // Act: bitcoind cannot be read in the first round
        _chainUnreachable = true;
        await executor.RunRoundAsync(_chain.TipHeight, TestContext.Current.CancellationToken);

        // Assert: nothing resolved yet
        var pending = await InScopeAsync(provider, u => u.OnchainResolutionDbRepository.GetOutputAsync(commitmentTxId,
                                                                                                         1));
        Assert.Equal(OutputResolutionState.Pending, pending?.State);

        // Act: bitcoind is back for the next block
        _chainUnreachable = false;
        _chain.Mine();
        await executor.RunRoundAsync(_chain.TipHeight, TestContext.Current.CancellationToken);

        // Assert
        await AssertResolvedAsync(provider, commitmentTxId, spend);
    }

    public void Dispose()
    {
        _pair.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless
            }
        }
    }

    /// <summary>
    /// The first process: stores the channel, mines the commitment, saves what the watcher saves for it (the close,
    /// two output rows and their watches, <c>OnchainResolving</c>) without tracking the watches, then mines a spend of
    /// output 1 and a block on top, which the monitor processes without the watch. The provider is disposed (the
    /// crash).
    /// </summary>
    private async Task<(TxId CommitmentTxId, Transaction Spend)> RunFirstProcessUntilTheCrashAsync()
    {
        var channel = _pair.Alice.Channel;
        var funding = channel.FundingOutput ?? throw new InvalidOperationException("No funding output");
        var fundingTxId = funding.TransactionId ?? throw new InvalidOperationException("No funding transaction");
        var commitment = CreateTransaction(fundingTxId, funding.Index ?? 0, outputs: 2);
        var commitmentTxId = new TxId(commitment.GetHash().ToBytes());
        var commitmentBlock = _chain.Mine(commitment);

        await using (var provider = StartProcess())
        {
            using (var scope = provider.CreateScope())
                await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database
                           .MigrateAsync(TestContext.Current.CancellationToken);

            await InScopeAsync(provider, async unitOfWork =>
            {
                await unitOfWork.ChannelDbRepository.AddAsync(channel);
                await unitOfWork.SaveChangesAsync();
                return true;
            });

            // The watcher's save
            await InScopeAsync(provider, async unitOfWork =>
            {
                var stored = await unitOfWork.ChannelDbRepository.GetByIdAsync(channel.ChannelId)
                          ?? throw new InvalidOperationException("The channel was not stored");
                stored.UpdateState(ChannelState.OnchainResolving);
                await unitOfWork.ChannelDbRepository.UpdateAsync(stored);
                await unitOfWork.OnchainResolutionDbRepository.UpsertCloseAsync(
                    new ChannelCloseModel(channel.ChannelId, ChannelCloseKind.LocalCommitment, commitmentTxId, 0,
                                          SpentAt, new Hash(commitmentBlock.GetHash().ToBytes()),
                                          DateTimeOffset.UtcNow));
                foreach (var vout in new uint[] { 0, 1 })
                {
                    await unitOfWork.OnchainResolutionDbRepository.UpsertOutputAsync(new OutputResolutionModel
                    {
                        TransactionId = commitmentTxId,
                        OutputIndex = vout,
                        ChannelId = channel.ChannelId,
                        Descriptor = vout == 0
                                         ? OutputDescriptorKind.DelayedToLocal
                                         : OutputDescriptorKind.PaymentToRemote,
                        DescriptorData = [],
                        State = OutputResolutionState.Pending,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                    unitOfWork.WatchedOutpointDbRepository.Add(
                        new WatchedOutpointModel(commitmentTxId, vout, channel.ChannelId,
                                                 WatchedOutpointPurpose.ResolutionOutput));
                }

                await unitOfWork.SaveChangesAsync();
                return true;
            });
        }

        // While nobody watched output 1 (process dead before the tracking, or the monitor raced the save)
        var spend = CreateTransaction(commitmentTxId, 1);
        _chain.Mine(spend);
        _chain.Mine();
        return (commitmentTxId, spend);
    }

    private async Task<OnchainResolutionExecutor> LoadAndCreateExecutorAsync(ServiceProvider provider)
    {
        var channel = await InScopeAsync(provider, u => u.ChannelDbRepository.GetByIdAsync(_pair.Alice.Channel.ChannelId))
                   ?? throw new InvalidOperationException("The channel was not stored");
        Assert.Equal(ChannelState.OnchainResolving, channel.State);
        var memory = provider.GetRequiredService<IChannelMemoryRepository>();
        memory.AddChannel(channel);

        return new OnchainResolutionExecutor(_broadcaster.Object, new ChannelLockProvider(), memory,
                                             NullLogger<OnchainResolutionExecutor>.Instance, _outpointWatcher.Object,
                                             provider.GetRequiredService<IServiceScopeFactory>(),
                                             Options.Create(new OnchainOptions()));
    }

    private async Task AssertResolvedAsync(ServiceProvider provider, TxId commitmentTxId, Transaction spend)
    {
        var resolved = await InScopeAsync(provider, u => u.OnchainResolutionDbRepository.GetOutputAsync(commitmentTxId,
                                                                                                          1));
        Assert.Equal(OutputResolutionState.Resolved, resolved?.State);
        Assert.Equal(SpentAt + 1, resolved?.ResolvedHeight);

        var watch = await InScopeAsync(provider, u => u.WatchedOutpointDbRepository.GetAsync(commitmentTxId, 1));
        Assert.Equal(new TxId(spend.GetHash().ToBytes()), watch?.SpentByTransactionId);
        Assert.Equal(SpentAt + 1, watch?.SpentAtHeight);

        var untouched = await InScopeAsync(provider, u => u.OnchainResolutionDbRepository.GetOutputAsync(commitmentTxId,
                                                                                                           0));
        Assert.Equal(OutputResolutionState.Pending, untouched?.State);
    }

    private ServiceProvider StartProcess()
    {
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Database:Provider"] = "sqlite",
                               ["Database:ConnectionString"] = $"Data Source={_databasePath}"
                           })
                           .Build();

        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetCurrentBlockHeightAsync())
             .Returns(() => _chainUnreachable
                                ? throw new HttpRequestException("bitcoind is unreachable")
                                : _chain.GetCurrentBlockHeightAsync());
        chain.Setup(c => c.GetBlockAsync(It.IsAny<uint>()))
             .Returns((uint height) => _chainUnreachable
                                           ? throw new HttpRequestException("bitcoind is unreachable")
                                           : _chain.GetBlockAsync(height));
        chain.Setup(c => c.GetBlockHashAsync(It.IsAny<uint>()))
             .Returns((uint height) => _chainUnreachable
                                           ? throw new HttpRequestException("bitcoind is unreachable")
                                           : _chain.GetBlockHashAsync(height));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISha256, Sha256>();
        services.AddPersistenceInfrastructureServices(configuration);
        services.AddRepositoriesInfrastructureServices();
        services.AddSingleton(chain.Object);
        services.AddScoped<IOutputResolver>(_ => _resolver);
        return services.BuildServiceProvider();
    }

    private static async Task<T> InScopeAsync<T>(ServiceProvider provider, Func<IUnitOfWork, Task<T>> action)
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    private static Transaction CreateTransaction(TxId spent, uint vout, int outputs = 1)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(spent), vout));
        for (var i = 0; i < outputs; i++)
            transaction.Outputs.Add(Money.Satoshis(10_000 + i), new Key().PubKey.WitHash.ScriptPubKey);
        return transaction;
    }

    /// <summary>A resolver of local commitments that records the spends it is told about and decides nothing.</summary>
    private sealed class RecordingResolver : IOutputResolver
    {
        public List<(OutputResolutionModel Output, ChainTx Spender, uint Height)> Spends { get; } = [];

        public bool CanResolve(ChannelCloseKind kind) => kind == ChannelCloseKind.LocalCommitment;

        public Task<IReadOnlyList<OutputResolverAction>> ResolveAsync(ChannelCloseModel close,
                                                                      IReadOnlyList<OutputResolutionModel> outputs,
                                                                      uint height,
                                                                      CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutputResolverAction>>([]);

        public Task<IReadOnlyList<OutputResolverAction>> OnOutputSpentAsync(
            ChannelCloseModel close, OutputResolutionModel output, ChainTx spendingTransaction, uint height,
            CancellationToken cancellationToken)
        {
            Spends.Add((output, spendingTransaction, height));
            return Task.FromResult<IReadOnlyList<OutputResolverAction>>([]);
        }
    }
}