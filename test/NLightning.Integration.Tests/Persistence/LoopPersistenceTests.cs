using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Crypto.KeyRing;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.KeyRing;
using Infrastructure.Bitcoin.Wallet.Imports;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Memory;

public class LoopPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Given_IssuedSwapKeys_When_RestartedWithTheMigratedDatabase_Then_LocatorsAndPublicLookupSurvive()
    {
        // Arrange: real migrations and fresh scopes, including the EF byte-array lookup translation.
        using var database = new SqliteTestDatabase();
        using var services = Provider(database);
        var keys = new Mock<ISecureKeyManager>();
        var master = ExtKey.CreateFromSeed(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        keys.Setup(k => k.GetKeyRingKeyAtIndex(It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int family, int index) => master.Derive(new KeyPath($"1017'/0'/{family}'/0/{index}")).ToBytes());
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        KeyRingKey first;
        using (var ring = new KeyRingService(keys.Object, scopes, Options.Create(new KeyRingOptions())))
        {
            first = await ring.DeriveNextAsync(42060, Ct);
            await ring.DeriveAsync(new KeyRingLocator(42060, 17), Ct);
        }

        // Act
        using var restarted = new KeyRingService(keys.Object, scopes, Options.Create(new KeyRingOptions()));
        var found = await restarted.FindAsync(first.PublicKey, Ct);
        var same = await restarted.DeriveAsync(first.Locator, Ct);
        var next = await restarted.DeriveNextAsync(42060, Ct);

        // Assert
        Assert.NotNull(found);
        Assert.Equal(first.Locator, found.Locator);
        Assert.Equal(first.PublicKey, same.PublicKey);
        Assert.Equal(18, next.Locator.Index);
        using var context = database.CreateContext();
        Assert.Equal(3, context.KeyRingKeys.Count());
    }

    [Fact]
    public async Task Given_ACachedWatchSnapshot_When_AnotherScriptIsImported_Then_ItRebuildsAndRejectsUnavailableTips()
    {
        using var database = new SqliteTestDatabase();
        using var services = Provider(database);
        using var firstKey = new Key();
        using var secondKey = new Key();
        var first = firstKey.PubKey.GetTaprootFullPubKey().ScriptPubKey;
        var second = secondKey.PubKey.GetTaprootFullPubKey().ScriptPubKey;
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        transaction.Outputs.Add(Money.Satoshis(500_000), first);
        transaction.Outputs.Add(Money.Satoshis(500_000), second);
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        block.Transactions.Add(transaction);
        block.UpdateMerkleRoot();
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetBlockAsync(1U)).ReturnsAsync(block);
        chain.Setup(c => c.GetBlockHashAsync(1U)).ReturnsAsync(block.GetHash());
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(1U);
        using var tracker = new ImportedTapscriptTracker(services.GetRequiredService<IServiceScopeFactory>(), chain.Object, monitor.Object);
        await tracker.ImportAsync(new ImportedTapscript(first.ToBytes(), firstKey.PubKey.ToBytes()[1..], [1], 1), Ct);
        Assert.Single((await tracker.SnapshotAsync(Ct)).Outputs);
        Assert.Single((await tracker.SnapshotAsync(Ct)).Outputs);
        chain.Verify(c => c.GetBlockAsync(1U), Times.Once());
        await tracker.ImportAsync(new ImportedTapscript(second.ToBytes(), secondKey.PubKey.ToBytes()[1..], [2], 1), Ct);
        Assert.Equal(2, (await tracker.SnapshotAsync(Ct)).Outputs.Count);
        chain.Verify(c => c.GetBlockAsync(1U), Times.Exactly(2));
        chain.Setup(c => c.GetBlockHashAsync(1U)).ThrowsAsync(new NBitcoin.RPC.RPCException(
            NBitcoin.RPC.RPCErrorCode.RPC_INVALID_PARAMETER, "Block height out of range", null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tracker.SnapshotAsync(Ct));
    }

    [Fact]
    public async Task Given_AnImportedDeposit_When_SpentRestartedAndReorged_Then_HistoryIsRecoveredWithoutSpendableUtxos()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        using var services = Provider(database);
        var chain = new Mock<IBitcoinChainService>();
        var monitor = new Mock<IBlockchainMonitor>();
        uint tip = 2;
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(() => tip);
        var blocks = new Dictionary<uint, Block>();
        Block Make(uint height, uint nonce)
        {
            var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
            block.Header.Nonce = nonce;
            block.Header.HashPrevBlock = height == 1 ? uint256.Zero : blocks[height - 1].GetHash();
            return block;
        }
        blocks[1] = Make(1, 1);
        blocks[2] = Make(2, 2);
        chain.Setup(c => c.GetBlockAsync(It.IsAny<uint>())).ReturnsAsync((uint h) => blocks.GetValueOrDefault(h));
        chain.Setup(c => c.GetBlockHashAsync(It.IsAny<uint>())).ReturnsAsync((uint h) => blocks[h].GetHash());
        using var key = new Key();
        var script = key.PubKey.GetTaprootFullPubKey().ScriptPubKey;
        var imported = new ImportedTapscript(script.ToBytes(), key.PubKey.ToBytes()[1..], [1, 2, 3], 1);
        var funding = Network.RegTest.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        funding.Outputs.Add(Money.Satoshis(500_000), script);
        blocks[2].Transactions.Add(funding);
        blocks[2].UpdateMerkleRoot();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        using (var tracker = new ImportedTapscriptTracker(scopes, chain.Object, monitor.Object))
        {
            await tracker.ImportAsync(imported, Ct);
            await tracker.ImportAsync(imported, Ct);
            Assert.Single((await tracker.SnapshotAsync(Ct)).Outputs);
            var cached = await tracker.SnapshotAsync(Ct);
            Assert.Single(cached.Outputs).Output.Value = Money.Satoshis(1);
            Assert.Equal(500_000, Assert.Single((await tracker.SnapshotAsync(Ct)).Outputs).Output.Value.Satoshi);
            chain.Verify(c => c.GetBlockAsync(It.IsAny<uint>()), Times.Exactly(2));
        }
        var spend = Network.RegTest.CreateTransaction();
        spend.Inputs.Add(new TxIn(new OutPoint(funding.GetHash(), 0)));
        spend.Outputs.Add(Money.Satoshis(499_000), new Script(OpcodeType.OP_TRUE));
        blocks[3] = Make(3, 3);
        blocks[3].Transactions.Add(spend);
        blocks[3].UpdateMerkleRoot();
        tip = 3;

        // Act: re-open the tracker against stored imports, then disconnect the spend and the deposit.
        using var restarted = new ImportedTapscriptTracker(scopes, chain.Object, monitor.Object);
        var spent = await restarted.SnapshotAsync(Ct);
        blocks[3] = Make(3, 4);
        var restored = await restarted.SnapshotAsync(Ct);
        blocks[2] = Make(2, 5);
        blocks[3] = Make(3, 6);
        var disconnected = await restarted.SnapshotAsync(Ct);

        // Assert: raw deposit/spend transactions and previous outpoints survive restart; reorgs remove history.
        Assert.Empty(spent.Outputs);
        Assert.Equal(2, spent.Transactions.Count);
        Assert.Equal(funding.ToBytes(), spent.Transactions[0].Transaction.ToBytes());
        Assert.Equal(new OutPoint(funding.GetHash(), 0), Assert.Single(spent.Transactions[1].SpentOutputs));
        Assert.Equal(-500_000, spent.Transactions[1].Amount);
        Assert.Single(restored.Outputs);
        Assert.Single(restored.Transactions);
        Assert.Empty(disconnected.Outputs);
        Assert.Empty(disconnected.Transactions);
        using var context = database.CreateContext();
        Assert.Single(context.ImportedTapscripts);
        Assert.Empty(context.Utxos);
    }

    [Fact]
    public async Task Given_ADurableImportedHistory_When_RestartedAndExtended_Then_OnlyNewBlocksAreRead()
    {
        using var database = new SqliteTestDatabase();
        var failure = new IndexSaveFailure();
        using var services = Provider(database, failure);
        using var key = new Key();
        var script = key.PubKey.GetTaprootFullPubKey().ScriptPubKey;
        var deposit = Network.RegTest.CreateTransaction();
        deposit.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        deposit.Outputs.Add(Money.Satoshis(500_000), script);
        var blocks = new Dictionary<uint, Block>();
        for (uint height = 1; height <= 3; height++)
        {
            var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
            block.Header.Nonce = height;
            block.Header.HashPrevBlock = height == 1 ? uint256.Zero : blocks[height - 1].GetHash();
            if (height == 1) block.Transactions.Add(deposit);
            block.UpdateMerkleRoot();
            blocks[height] = block;
        }
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetBlockAsync(It.IsAny<uint>())).ReturnsAsync((uint h) => blocks[h]);
        chain.Setup(c => c.GetBlockHashAsync(It.IsAny<uint>())).ReturnsAsync((uint h) => blocks[h].GetHash());
        uint tip = 2;
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(() => tip);
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        using (var tracker = new ImportedTapscriptTracker(scopes, chain.Object, monitor.Object))
        {
            await tracker.ImportAsync(new ImportedTapscript(script.ToBytes(), key.PubKey.ToBytes()[1..], [1], 1), Ct);
            Assert.Single((await tracker.SnapshotAsync(Ct)).Outputs);
        }
        using var restarted = new ImportedTapscriptTracker(scopes, chain.Object, monitor.Object);
        Assert.Single((await restarted.SnapshotAsync(Ct)).Outputs);
        chain.Verify(c => c.GetBlockAsync(1), Times.Once());
        chain.Verify(c => c.GetBlockAsync(2), Times.Once());
        tip = 3;
        failure.FailNext = true;
        await Assert.ThrowsAsync<IOException>(() => restarted.SnapshotAsync(Ct));
        using (var checkpoint = database.CreateContext())
            Assert.Equal(2U, checkpoint.ImportedWatchIndexes.Single().Height);
        monitor.Raise(m => m.OnNewBlockDetected += null!, new Domain.Bitcoin.Events.NewBlockEventArgs(3, blocks[3].GetHash().ToBytes()));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var context = database.CreateContext();
            if (context.ImportedWatchIndexes.Single().Height == 3) break;
            await Task.Delay(10, deadline.Token);
        }
        Assert.Single((await restarted.SnapshotAsync(Ct)).Outputs);
        chain.Verify(c => c.GetBlockAsync(1), Times.Once());
        chain.Verify(c => c.GetBlockAsync(2), Times.Once());
        chain.Verify(c => c.GetBlockAsync(3), Times.Exactly(2));
        using var finalContext = database.CreateContext();
        Assert.Empty(finalContext.Utxos);
    }

    private static ServiceProvider Provider(SqliteTestDatabase database, params IInterceptor[] interceptors)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new UtxoMemoryRepository());
        services.AddScoped<NLightningDbContext>(_ => database.CreateContext(interceptors));
        services.AddScoped<IUnitOfWork>(s => new UnitOfWork(s.GetRequiredService<NLightningDbContext>(),
            NullLogger<UnitOfWork>.Instance, new Sha256(), s.GetRequiredService<UtxoMemoryRepository>()));
        return services.BuildServiceProvider();
    }
    private sealed class IndexSaveFailure : SaveChangesInterceptor
    {
        public bool FailNext { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("checkpoint save failed");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}