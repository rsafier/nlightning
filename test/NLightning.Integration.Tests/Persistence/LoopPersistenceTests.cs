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
        monitor.Raise(m => m.OnWalletTransactionsProcessed += null!, new Domain.Bitcoin.Events.NewBlockEventArgs(3, blocks[3].GetHash().ToBytes()));
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

    [Fact]
    public async Task Given_ImportedAndCanonicalOwnership_When_CommittedReorgedAndRetried_Then_OneMergedImmutableNoticePerState()
    {
        using var database = new SqliteTestDatabase();
        var failure = new IndexSaveFailure();
        using var services = Provider(database, failure);
        using var key = new Key();
        var script = key.PubKey.GetTaprootFullPubKey().ScriptPubKey;
        var deposit = Network.RegTest.CreateTransaction();
        deposit.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        deposit.Outputs.Add(Money.Satoshis(500_000), script);
        deposit.Outputs.Add(Money.Satoshis(10_000), new Key().PubKey.WitHash.ScriptPubKey);
        var blocks = new Dictionary<uint, Block>();
        Block Make(uint height, uint nonce, Transaction? transaction = null)
        {
            var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
            block.Header.Nonce = nonce;
            block.Header.HashPrevBlock = height == 1 ? uint256.Zero : blocks[height - 1].GetHash();
            if (transaction is not null) block.Transactions.Add(transaction);
            block.UpdateMerkleRoot();
            return block;
        }
        blocks[1] = Make(1, 1);
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetBlockAsync(It.IsAny<uint>())).ReturnsAsync((uint h) => blocks[h]);
        chain.Setup(c => c.GetBlockHashAsync(It.IsAny<uint>())).ReturnsAsync((uint h) => blocks[h].GetHash());
        uint tip = 1;
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(() => tip);
        using var tracker = new ImportedTapscriptTracker(services.GetRequiredService<IServiceScopeFactory>(), chain.Object, monitor.Object);
        await tracker.ImportAsync(new ImportedTapscript(script.ToBytes(), key.PubKey.ToBytes()[1..], [1], 1), Ct);
        await tracker.SnapshotAsync(Ct);
        var observed = new List<Domain.Bitcoin.Events.WalletTransactionEventArgs>();
        tracker.OnTransactionObserved += (_, e) =>
        {
            using var context = database.CreateContext();
            Assert.Equal(tip, context.ImportedWatchIndexes.Single().Height);
            observed.Add(e);
        };
        Domain.Bitcoin.Events.WalletTransactionEventArgs Canonical(uint height, string hash) =>
            new(deposit.ToHex(), 10_000, 0, height, hash, DateTimeOffset.UnixEpoch, "mixed", [1], [], deposit.GetHash().ToString(), isReorg: height == 0);
        blocks[2] = Make(2, 2, deposit);
        tip = 2;
        failure.FailNext = true;
        await Assert.ThrowsAsync<IOException>(() => tracker.SnapshotAsync(Ct));
        Assert.Empty(observed);
        var snapshot = await tracker.SnapshotAsync(Ct);
        Assert.Empty(observed); // RPC index commit preceded the monitor's canonical wallet batch.
        monitor.Raise(m => m.OnWalletTransactionObserved += null!, Canonical(2, blocks[2].GetHash().ToString()));
        monitor.Raise(m => m.OnWalletTransactionsProcessed += null!,
            new Domain.Bitcoin.Events.NewBlockEventArgs(2, blocks[2].GetHash().ToBytes()));
        await tracker.SnapshotAsync(Ct);
        var confirmed = Assert.Single(observed);
        Assert.Equal(510_000, confirmed.AmountSat);
        Assert.Equal([0u, 1u], confirmed.OurOutputs);
        // Mutable RPC snapshots cannot alter an already queued notice.
        snapshot.Transactions[0].Transaction.Outputs[0].Value = Money.Satoshis(1);
        Assert.Equal(500_000, Transaction.Parse(confirmed.RawTransactionHex, Network.RegTest).Outputs[0].Value.Satoshi);
        await tracker.SnapshotAsync(Ct);
        Assert.Single(observed);

        var oldHash = blocks[2].GetHash().ToString();
        monitor.Raise(m => m.OnWalletTransactionsProcessing += null!, EventArgs.Empty);
        monitor.Raise(m => m.OnWalletTransactionObserved += null!, Canonical(0, ""));
        blocks[2] = Make(2, 3, deposit);
        monitor.Raise(m => m.OnWalletTransactionObserved += null!, Canonical(2, blocks[2].GetHash().ToString()));
        await tracker.SnapshotAsync(Ct);
        Assert.Single(observed);
        monitor.Raise(m => m.OnWalletTransactionsProcessed += null!,
            new Domain.Bitcoin.Events.NewBlockEventArgs(2, blocks[2].GetHash().ToBytes()));
        await tracker.SnapshotAsync(Ct);
        Assert.Equal([2u, 0u, 2u], observed.Select(o => o.BlockHeight));
        Assert.All(observed, o => Assert.Equal(510_000, o.AmountSat));
        Assert.NotEqual(oldHash, observed[2].BlockHash);
        chain.Verify(c => c.GetBlockAsync(1), Times.Exactly(2));
        // The index worker can lag a committed confirmation and its rewind. The queued old branch
        // must never reappear when the replacement chain later reaches that same height.
        var later = deposit.Clone();
        later.Outputs[1].Value = Money.Satoshis(9_000);
        var laterHash = later.GetHash().ToString();
        monitor.Raise(m => m.OnWalletTransactionsProcessing += null!, EventArgs.Empty);
        monitor.Raise(m => m.OnWalletTransactionObserved += null!,
            new Domain.Bitcoin.Events.WalletTransactionEventArgs(later.ToHex(), 9_000, 0, 3, new string('a', 64),
                DateTimeOffset.UnixEpoch, "lagged", [1], [], laterHash));
        monitor.Raise(m => m.OnWalletTransactionObserved += null!,
            new Domain.Bitcoin.Events.WalletTransactionEventArgs(later.ToHex(), 9_000, 0, 0, "",
                DateTimeOffset.UnixEpoch, "lagged", [1], [], laterHash, isReorg: true));
        await tracker.SnapshotAsync(Ct);
        Assert.DoesNotContain(observed, o => o.TxHash == laterHash);
        monitor.Raise(m => m.OnWalletTransactionsProcessed += null!,
            new Domain.Bitcoin.Events.NewBlockEventArgs(2, blocks[2].GetHash().ToBytes()));
        await tracker.SnapshotAsync(Ct);
        blocks[3] = Make(3, 4);
        tip = 3;
        await tracker.SnapshotAsync(Ct);
        monitor.Raise(m => m.OnWalletTransactionsProcessed += null!,
            new Domain.Bitcoin.Events.NewBlockEventArgs(3, blocks[3].GetHash().ToBytes()));
        await tracker.SnapshotAsync(Ct);
        Assert.DoesNotContain(observed, o => o.TxHash == laterHash && o.BlockHeight == 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Given_ImportedDepositAndSpend_When_Restarted_Then_OnlyNewCommittedStatesArePublished(int mode)
    {
        using var database = new SqliteTestDatabase();
        using var services = Provider(database);
        using var key = new Key();
        var script = key.PubKey.GetTaprootFullPubKey().ScriptPubKey;
        var deposit = Network.RegTest.CreateTransaction();
        deposit.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        deposit.Outputs.Add(Money.Satoshis(500_000), script);
        var blocks = new Dictionary<uint, Block>();
        for (uint h = 1; h <= 3; h++)
        {
            var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
            block.Header.Nonce = h;
            block.Header.HashPrevBlock = h == 1 ? uint256.Zero : blocks[h - 1].GetHash();
            blocks[h] = block;
        }
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetBlockAsync(It.IsAny<uint>())).ReturnsAsync((uint h) => blocks[h]);
        chain.Setup(c => c.GetBlockHashAsync(It.IsAny<uint>())).ReturnsAsync((uint h) => blocks[h].GetHash());
        uint tip = 1;
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(() => tip);
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        using (var tracker = new ImportedTapscriptTracker(scopes, chain.Object, monitor.Object))
        {
            await tracker.ImportAsync(new ImportedTapscript(script.ToBytes(), key.PubKey.ToBytes()[1..], [1], 1), Ct);
            await tracker.SnapshotAsync(Ct);
            var observed = new List<Domain.Bitcoin.Events.WalletTransactionEventArgs>();
            tracker.OnTransactionObserved += (_, e) => observed.Add(e);
            blocks[2].Transactions.Add(deposit);
            blocks[2].UpdateMerkleRoot();
            tip = 2;
            await tracker.SnapshotAsync(Ct);
            monitor.Raise(m => m.OnWalletTransactionsProcessed += null!,
                new Domain.Bitcoin.Events.NewBlockEventArgs(2, blocks[2].GetHash().ToBytes()));
            await tracker.SnapshotAsync(Ct);
            Assert.Equal(500_000, Assert.Single(observed).AmountSat);
        }
        blocks[3].Header.HashPrevBlock = blocks[2].GetHash();
        var spend = Network.RegTest.CreateTransaction();
        spend.Inputs.Add(new TxIn(new OutPoint(deposit.GetHash(), 0)));
        spend.Outputs.Add(Money.Satoshis(499_000), new Script(OpcodeType.OP_TRUE));
        blocks[3].Transactions.Add(spend);
        blocks[3].UpdateMerkleRoot();
        using var restarted = new ImportedTapscriptTracker(scopes, chain.Object, monitor.Object);
        var updates = new List<Domain.Bitcoin.Events.WalletTransactionEventArgs>();
        if (mode == 2) tip = 3;
        await restarted.SubscribeAsync((_, e) => updates.Add(e), Ct);
        Assert.Empty(updates);
        if (mode == 2)
        {
            var next = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
            next.Header.Nonce = 4;
            next.Header.HashPrevBlock = blocks[3].GetHash();
            blocks[4] = next;
            tip = 4;
            monitor.Raise(m => m.OnWalletTransactionsProcessed += null!,
                new Domain.Bitcoin.Events.NewBlockEventArgs(4, next.GetHash().ToBytes()));
            await restarted.SnapshotAsync(Ct);
            Assert.Empty(updates); // catch-up before attachment remains baseline after the next block
            return;
        }
        if (mode == 1)
        {
            var replacement = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
            replacement.Header.Nonce = 22;
            replacement.Header.HashPrevBlock = blocks[1].GetHash();
            blocks[2] = replacement;
            monitor.Raise(m => m.OnWalletTransactionObserved += null!,
                new Domain.Bitcoin.Events.WalletTransactionEventArgs(deposit.ToHex(), 500_000, 0, 0, "",
                    DateTimeOffset.UnixEpoch, "", [0], [], deposit.GetHash().ToString(), isReorg: true));
            Assert.Empty(updates);
            await restarted.SnapshotAsync(Ct);
            Assert.Empty(updates);
            monitor.Raise(m => m.OnWalletTransactionsProcessed += null!,
                new Domain.Bitcoin.Events.NewBlockEventArgs(2, replacement.GetHash().ToBytes()));
            await restarted.SnapshotAsync(Ct);
            var rewind = Assert.Single(updates);
            Assert.Equal(0u, rewind.BlockHeight);
            Assert.Equal(500_000, rewind.AmountSat); // overlapping canonical/imported output counted once
            return;
        }
        tip = 3;
        await restarted.SnapshotAsync(Ct);
        monitor.Raise(m => m.OnWalletTransactionsProcessed += null!,
            new Domain.Bitcoin.Events.NewBlockEventArgs(3, blocks[3].GetHash().ToBytes()));
        await restarted.SnapshotAsync(Ct);
        var spent = Assert.Single(updates);
        Assert.Equal(-500_000, spent.AmountSat);
        Assert.Equal(1_000, spent.FeeSat);
        Assert.Equal([0u], spent.OurInputs);
        chain.Verify(c => c.GetBlockAsync(1), Times.Once());
        chain.Verify(c => c.GetBlockAsync(2), Times.Once());
        chain.Verify(c => c.GetBlockAsync(3), Times.Once());
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