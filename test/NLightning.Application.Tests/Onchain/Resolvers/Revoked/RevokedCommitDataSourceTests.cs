using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Revoked;

using Application.Channels.Services;
using Application.Onchain.Resolvers;
using Application.Onchain.Resolvers.Revoked;
using Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Protocol.Services;

public class RevokedCommitDataSourceTests
{
    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    private readonly Mock<IRemoteShachainDbRepository> _shachain = new();
    private readonly Mock<IRevokedCommitmentDbRepository> _log = new();
    private readonly Mock<IWatchedOutpointDbRepository> _watches = new();
    private readonly Mock<IBroadcastTransactionDbRepository> _broadcasts = new();
    private readonly Mock<IChannelDbRepository> _channels = new();
    private readonly Mock<IBitcoinChainService> _chain = new();
    private readonly Mock<IFeeService> _fees = new();

    private RevokedCommitDataSource CreateDataSource(ChannelModel? inMemory = null)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(_shachain.Object);
        unitOfWork.SetupGet(u => u.RevokedCommitmentDbRepository).Returns(_log.Object);
        unitOfWork.SetupGet(u => u.WatchedOutpointDbRepository).Returns(_watches.Object);
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(_broadcasts.Object);
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_channels.Object);

        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        var provider = services.BuildServiceProvider();

        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
              .Returns(new TryGetChannelCallback((ChannelId _, out ChannelModel? channel) =>
               {
                   channel = inMemory;
                   return channel is not null;
               }));

        return new RevokedCommitDataSource(provider.GetRequiredService<IServiceScopeFactory>(), _chain.Object,
                                           new SecretStorageServiceFactory(), _fees.Object,
                                           Options.Create(new NodeOptions()),
                                           NullLogger<RevokedCommitDataSource>.Instance, memory.Object);
    }

    private static ChannelCloseModel Close(TxId txId, ulong? number) =>
        new(RealSigningCommitmentPair.ChannelId, ChannelCloseKind.RevokedCommitment, txId, number, 321,
            new Hash(new byte[32]), DateTimeOffset.UtcNow);

    private static Transaction CreateTransaction(byte tag)
    {
        var tx = Network.Main.CreateTransaction();
        tx.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat(tag, 32).ToArray()), 0));
        tx.Outputs.Add(Money.Satoshis(1_000), new Key().PubKey.WitHash);
        return tx;
    }

    private void ServeBlock(uint height, params Transaction[] transactions)
    {
        var block = Network.Main.Consensus.ConsensusFactory.CreateBlock();
        block.Transactions.AddRange(transactions);
        _chain.Setup(c => c.GetBlockAsync(height)).ReturnsAsync(block);
    }

    [Fact]
    public async Task Given_ShachainHoldsTheSecret_When_Loaded_Then_ContextHasSecretPointLogAndTransaction()
    {
        // Arrange: the peer revealed secrets 0..2 (the shachain as persisted), commitment 1 is on chain
        using var pair = new RealSigningCommitmentPair(false);
        using var store = new SecretStorageService();
        var secrets = DeriveSecrets(pair, 3);
        for (var n = 0; n < 3; n++)
            Assert.True(store.InsertSecret(secrets[n], PerCommitmentIndex.From((ulong)n)));
        _shachain.Setup(s => s.GetByChannelIdAsync(RealSigningCommitmentPair.ChannelId)).ReturnsAsync(store.Export());
        var entry = new RevokedCommitmentModel(RealSigningCommitmentPair.ChannelId, 1, pair.Bob.State.RemoteCommit.Spec);
        _log.Setup(l => l.GetAsync(RealSigningCommitmentPair.ChannelId, 1)).ReturnsAsync(entry);
        _log.Setup(l => l.GetLogStartAsync(RealSigningCommitmentPair.ChannelId)).ReturnsAsync(0ul);
        var commitment = CreateTransaction(1);
        ServeBlock(321, CreateTransaction(2), commitment);
        var dataSource = CreateDataSource(pair.Bob.Channel);

        // Act
        var result = await dataSource.LoadAsync(Close(commitment.GetHash().ToBytes(), 1),
                                                TestContext.Current.CancellationToken);

        // Assert
        var context = Assert.IsType<RevokedCommitContext>(result.Context);
        Assert.Equal(secrets[1], context.PerCommitmentSecret);
        Assert.Equal(pair.Alice.Point(1), context.PerCommitmentPoint);
        Assert.Same(entry, context.LogEntry);
        Assert.Equal((TxId)commitment.GetHash().ToBytes(), context.CommitmentTransaction.TxId);
        Assert.Same(pair.Bob.Channel, context.Channel);
        Assert.False(context.PredatesLog);
    }

    [Fact]
    public async Task Given_SecretNotRevealed_When_Loaded_Then_Missing()
    {
        // Arrange: only secret 0 is known; commitment 1 is not revoked
        using var pair = new RealSigningCommitmentPair(false);
        using var store = new SecretStorageService();
        Assert.True(store.InsertSecret(DeriveSecrets(pair, 1)[0], PerCommitmentIndex.From(0)));
        _shachain.Setup(s => s.GetByChannelIdAsync(RealSigningCommitmentPair.ChannelId)).ReturnsAsync(store.Export());
        var dataSource = CreateDataSource(pair.Bob.Channel);

        // Act
        var result = await dataSource.LoadAsync(Close(new TxId(new byte[32]), 1), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result.Context);
        Assert.Contains("secret 1", result.Problem);
    }

    [Fact]
    public async Task Given_NoCommitmentNumber_When_Loaded_Then_Missing()
    {
        // Arrange
        var dataSource = CreateDataSource();

        // Act
        var result = await dataSource.LoadAsync(Close(new TxId(new byte[32]), null),
                                                TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result.Context);
    }

    [Fact]
    public async Task Given_WatchedOutpointSpentByOurBroadcast_When_GetSpend_Then_TransactionFromItsBlockAndByUs()
    {
        // Arrange
        var spender = CreateTransaction(7);
        TxId spenderId = spender.GetHash().ToBytes();
        var watch = new WatchedOutpointModel(new TxId(Enumerable.Repeat((byte)1, 32).ToArray()), 0,
                                             RealSigningCommitmentPair.ChannelId,
                                             WatchedOutpointPurpose.ResolutionOutput);
        watch.MarkSpent(spenderId, 400, new Hash(new byte[32]));
        _watches.Setup(w => w.GetAsync(watch.TransactionId, 0)).ReturnsAsync(watch);
        _broadcasts.Setup(b => b.GetByTransactionIdAsync(spenderId))
                   .ReturnsAsync(new BroadcastTransactionModel(new SignedTransaction(spenderId, spender.ToBytes()),
                                                               BroadcastPurpose.Penalty, null, 399));
        ServeBlock(400, spender);
        var dataSource = CreateDataSource();

        // Act
        var spend = await dataSource.GetSpendAsync(watch.TransactionId, 0, TestContext.Current.CancellationToken);
        var unspent = await dataSource.GetSpendAsync(new TxId(new byte[32]), 3, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(spend);
        Assert.Equal(spenderId, spend.SpendingTransactionId);
        Assert.Equal(spenderId, spend.SpendingTransaction!.TxId);
        Assert.Equal(400u, spend.Height);
        Assert.True(spend.ByUs);
        Assert.Null(unspent);
    }

    [Fact]
    public async Task Given_SpenderCannotBeFetched_When_GetSpend_Then_SpendWithTxIdAndNoTransaction()
    {
        // Arrange: the watched outpoint records a spender that neither its block nor txindex can serve
        var spenderId = new TxId(Enumerable.Repeat((byte)9, 32).ToArray());
        var watch = new WatchedOutpointModel(new TxId(Enumerable.Repeat((byte)1, 32).ToArray()), 0,
                                             RealSigningCommitmentPair.ChannelId,
                                             WatchedOutpointPurpose.ResolutionOutput);
        watch.MarkSpent(spenderId, 400, new Hash(new byte[32]));
        _watches.Setup(w => w.GetAsync(watch.TransactionId, 0)).ReturnsAsync(watch);
        _chain.Setup(c => c.GetBlockAsync(400)).ThrowsAsync(new HttpRequestException("pruned"));
        var dataSource = CreateDataSource();

        // Act
        var spend = await dataSource.GetSpendAsync(watch.TransactionId, 0, TestContext.Current.CancellationToken);

        // Assert: still a spend, not ours, with its txid, never a guess
        Assert.NotNull(spend);
        Assert.Equal(spenderId, spend.SpendingTransactionId);
        Assert.Null(spend.SpendingTransaction);
        Assert.Equal(400u, spend.Height);
        Assert.False(spend.ByUs);
    }

    [Fact]
    public async Task Given_FeeServiceFails_When_GetFeerate_Then_CachedRate()
    {
        // Arrange
        _fees.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
             .ThrowsAsync(new HttpRequestException("down"));
        _fees.Setup(f => f.GetCachedFeeRatePerKw()).Returns(LightningMoney.Satoshis(1_234));
        var dataSource = CreateDataSource();

        // Act
        var rate = await dataSource.GetFeeratePerKwAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1_234u, rate);
    }

    [Fact]
    public void Given_Registration_When_Resolved_Then_OneResolverServesTheRevokedKind()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<ISecretStorageServiceFactory>().Object);
        services.AddSingleton(new Mock<IFeeService>().Object);
        services.AddSingleton(new Mock<ICommitmentOutputMapper>().Object);
        services.AddSingleton<ISweepTransactionBuilder, SweepTransactionBuilder>();
        services.AddSingleton<IPenaltyTransactionBuilder, PenaltyTransactionBuilder>();
        services.AddSingleton(new Mock<ILightningSigner>().Object);
        services.AddSingleton(new Mock<IKeyDerivationService>().Object);
        services.AddRevokedCommitResolver();
        services.AddRevokedCommitResolver();

        // Act
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        var resolvers = provider.GetServices<IOutputResolver>().ToList();

        // Assert
        var resolver = Assert.Single(resolvers);
        Assert.Same(provider.GetRequiredService<RevokedCommitResolver>(), resolver);
        Assert.True(resolver.CanResolve(ChannelCloseKind.RevokedCommitment));
        Assert.IsType<RevokedCommitDataSource>(provider.GetRequiredService<IRevokedCommitDataSource>());
    }

    private static List<Secret> DeriveSecrets(RealSigningCommitmentPair pair, ulong count)
    {
        // The cheater's secrets 0..count-1, as its revoke_and_acks carry them (its signer releases only revoked ones)
        pair.Alice.Signer.AdvanceLocalCommitment(RealSigningCommitmentPair.ChannelId, count);
        var secrets = new List<Secret>();
        for (ulong n = 0; n < count; n++)
            secrets.Add(pair.Alice.Signer.RevealPerCommitmentSecret(RealSigningCommitmentPair.ChannelId, n));
        return secrets;
    }
}