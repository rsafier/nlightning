using System.Buffers.Binary;
using SHA256 = System.Security.Cryptography.SHA256;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NBitcoin;
using NBitcoin.Secp256k1;
using NLightning.Tests.Utils.Mocks;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace NLightning.Application.Tests.Bitcoin.SilentPayments;

using Application.Bitcoin.SilentPayments;
using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.SilentPayments.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Crypto.SilentPayments;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Bitcoin.Wallet.SilentPayments;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;
using TestUtils;

public sealed class SilentPaymentServiceTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nltg-sp-recovery-{Guid.NewGuid():N}.db");
    private readonly Dictionary<uint, Block> _blocks = [];
    private readonly Dictionary<OutPoint, (TxOut Output, uint Height)> _unspent = [];
    private readonly HashSet<OutPoint> _mempoolSpends = [];
    private readonly Mock<IBitcoinChainService> _chain = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IBlockPrevoutSource> _prevouts = new();
    private readonly FixtureKeys _keys = new();
    private readonly SilentPaymentsOptions _options = new() { Enabled = true, RecoveryLabelCount = 0, MinReceiveSat = 0 };
    private readonly SilentPaymentCrypto _crypto = new();
    private ServiceProvider _provider = null!;
    private SilentPaymentScanner _scanner = null!;

    public async ValueTask InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Database:Provider"] = "sqlite", ["Database:ConnectionString"] = $"Data Source={_path}" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISha256, Sha256>();
        services.AddPersistenceInfrastructureServices(configuration);
        services.AddRepositoriesInfrastructureServices();
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database.MigrateAsync(TestContext.Current.CancellationToken);
        for (uint height = 0; height <= 5; height++)
            _blocks[height] = NewBlock(height);
        _chain.Setup(chain => chain.GetCurrentBlockHeightAsync()).ReturnsAsync(5u);
        _chain.Setup(chain => chain.GetBlockHashAsync(It.IsAny<uint>())).ReturnsAsync((uint height) => _blocks[height].GetHash());
        _chain.Setup(chain => chain.GetBlockAsync(It.IsAny<uint>())).ReturnsAsync((uint height) => _blocks[height]);
        _chain.Setup(chain => chain.GetConfirmedUnspentOutputAsync(It.IsAny<OutPoint>()))
            .ReturnsAsync((OutPoint point) => _unspent.TryGetValue(point, out var value) ? value : ((TxOut, uint)?)null);
        _chain.Setup(chain => chain.GetUnspentOutputAsync(It.IsAny<OutPoint>()))
            .ReturnsAsync((OutPoint point) => !_mempoolSpends.Contains(point) && _unspent.TryGetValue(point, out var value)
                ? value : ((TxOut, uint)?)null);
        _monitor.SetupGet(monitor => monitor.LastProcessedBlockHeight).Returns(5u);
        _prevouts.SetupGet(source => source.Source).Returns(SilentPaymentPrevoutSource.GetRawTransaction);
        _prevouts.Setup(source => source.ProbeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _prevouts.Setup(source => source.ValidateHeightAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _prevouts.Setup(source => source.GetPrevoutsAsync(It.IsAny<BitcoinBlock>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BitcoinBlock block, uint _, CancellationToken _) => Prevouts(block));
        _scanner = new SilentPaymentScanner(_prevouts.Object, _crypto, _keys, MsOptions.Create(_options),
            NullLogger<SilentPaymentScanner>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        SqliteTestPools.Clear(_path);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            File.Delete(_path + suffix);
    }

    [Fact]
    public async Task Given_ConcurrentLabelRequests_When_AllocatedAndRestarted_Then_NamesRemainUniqueAndIndexesMonotonic()
    {
        // Arrange
        using var first = CreateService();

        // Act
        var labels = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => first.GetAddressAsync($"customer-{i}", TestContext.Current.CancellationToken)));
        using var restarted = CreateService();
        var repeated = await restarted.GetAddressAsync("customer-0", TestContext.Current.CancellationToken);
        var next = await restarted.GetAddressAsync("next", TestContext.Current.CancellationToken);
        var listed = await restarted.ListLabelsAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(12, labels.Select(label => label.Label).Distinct().Count());
        Assert.Equal(labels[0], repeated);
        Assert.Equal(13u, next.Label);
        Assert.Equal(14, listed.Count);
        Assert.True(listed[0].IsChange);
        Assert.Equal(0u, listed[0].Label);
    }

    [Fact]
    public async Task Given_PrunedHistory_When_RescanRequested_Then_NoCheckpointIsCommitted()
    {
        // Arrange
        using var service = CreateService();
        await service.GetAddressAsync(cancellationToken: TestContext.Current.CancellationToken);
        _prevouts.Setup(source => source.ValidateHeightAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("prune height is 3"));

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartRescanAsync(1, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False((await service.GetStatusAsync(TestContext.Current.CancellationToken)).IsRescanning);
    }

    [Fact]
    public async Task Given_RestoredSeedAndHistoricallySpentReceipt_When_RescanRestarts_Then_OnlyCurrentCoinsBecomeSpendableAndJournalNetsCorrectly()
    {
        // Arrange
        var spent = AddReceipt(1, 20_000);
        AddSpend(2, spent);
        var held = AddReceipt(3, 30_000);
        using var service = CreateService();
        await service.GetAddressAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.StartRescanAsync(1, 2, TestContext.Current.CancellationToken);

        // Act: checkpoint a historical deposit, then simulate process restart.
        Assert.True(await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await UnspentAsync());
        using var restarted = CreateService();
        Assert.Equal(2u, (await restarted.GetStatusAsync(TestContext.Current.CancellationToken)).RecoveryLabelCount);
        for (var i = 0; i < 4; i++)
            Assert.True(await restarted.ProcessNextBlockAsync(TestContext.Current.CancellationToken));

        // Assert
        var current = Assert.Single(await UnspentAsync());
        Assert.Equal(new TxId(held.Hash.ToBytes()), current.TxId);
        Assert.Equal(30_000L, current.Amount.Satoshi);
        Assert.False((await restarted.GetStatusAsync(TestContext.Current.CancellationToken)).IsRescanning);
        using var scope = _provider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var receipts = await uow.SilentPaymentDbRepository.GetOutputsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, receipts.Count);
        Assert.NotNull(receipts.Single(output => output.TransactionId == new TxId(spent.Hash.ToBytes())).SpentByTransactionId);
        var events = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync("wallet:", TestContext.Current.CancellationToken);
        Assert.Equal(3, events.Count);
        Assert.Equal(30_000_000, events.Sum(fact => fact.AmountMsat));
        Assert.All(events, fact => Assert.Equal("silent_payment", fact.Details["receiptSource"]));
        Assert.Equal(AccountingDetailKeys.ExternalSource, events.Single(fact => fact.Kind == AccountingEventKind.WalletReceived &&
            fact.TxId == new TxId(held.Hash.ToBytes())).Details[AccountingDetailKeys.Source]);
        var settlements = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync("wsend:", TestContext.Current.CancellationToken);
        var settlement = Assert.Single(settlements);
        Assert.Equal(-10_000_000L, settlement.AmountMsat);
        Assert.Equal(10_000_000L, settlement.FeeMsat);
        Assert.Equal("true", settlement.Details["purposeUnknown"]);
        AssertRecoveredBooks(events.Concat(settlements), 30_000_000, -50_000_000, 10_000_000, 10_000_000);
        Assert.False(await restarted.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        Assert.Single(await uow.AccountingEventDbRepository.GetByKeyPrefixAsync("wsend:", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_LiveSpendObservedBeforeHistoricalDiscovery_When_RecoveryFinishes_Then_FinalAuditFindsSpenderWithoutAdvancingGlobalWalletCursor()
    {
        // Arrange: live boundary=5, so the spend at 5 ran before receipt metadata existed.
        var received = AddReceipt(1, 21_000);
        AddSpend(5, received);
        using var service = CreateService();
        await service.GetAddressAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.StartRescanAsync(1, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        for (var i = 0; i < 5; i++)
            Assert.True(await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        using var restarted = CreateService();
        Assert.False(await restarted.ProcessNextBlockAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Empty(await UnspentAsync());
        using var scope = _provider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var output = Assert.Single(await uow.SilentPaymentDbRepository.GetOutputsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(5u, output.SpentAtHeight);
        var events = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync("wallet:", TestContext.Current.CancellationToken);
        Assert.Equal(2, events.Count);
        Assert.Equal(0, events.Sum(fact => fact.AmountMsat));
        Assert.Equal(_blocks[5].Header.BlockTime, events.Single(fact => fact.Kind == AccountingEventKind.WalletOutputSpent).OccurredAt);
        var settlements = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync("wsend:", TestContext.Current.CancellationToken);
        var settlement = Assert.Single(settlements);
        Assert.Equal(_blocks[5].Header.BlockTime, settlement.OccurredAt);
        AssertRecoveredBooks(events.Concat(settlements), 0, -21_000_000, 10_000_000, 11_000_000);
    }

    [Fact]
    public async Task Given_HistoricalSpendWithUnknownSharedInput_When_Recovered_Then_AccountingAndCursorFailAtomically()
    {
        // Arrange: neither a reusable-address label nor one owned input proves ownership of the whole transaction.
        var receipt = AddReceipt(1, 20_000);
        AddSpend(2, receipt);
        _blocks[2].Transactions.Last().Inputs.Add(new TxIn(new OutPoint(new uint256(12345), 0)));
        _blocks[2].UpdateMerkleRoot();
        using var service = CreateService();
        await service.GetAddressAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.StartRescanAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken));

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProcessNextBlockAsync(TestContext.Current.CancellationToken));

        // Assert: no fabricated withdrawal/fee, no spent marker, no cursor advance or selectable historical coin.
        Assert.Contains("provenance", error.Message);
        using var scope = _provider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        Assert.Equal(1u, (await uow.SilentPaymentDbRepository.GetScanStateAsync(TestContext.Current.CancellationToken))!.RescanCursorHeight);
        Assert.Null(Assert.Single(await uow.SilentPaymentDbRepository.GetOutputsAsync(TestContext.Current.CancellationToken)).SpentByTransactionId);
        Assert.Empty(await uow.AccountingEventDbRepository.GetByKeyPrefixAsync("wsend:", TestContext.Current.CancellationToken));
        Assert.Single(await uow.AccountingEventDbRepository.GetByKeyPrefixAsync("wallet:", TestContext.Current.CancellationToken));
        Assert.Empty(await UnspentAsync());
    }

    [Fact]
    public async Task Given_CancelledRecovery_When_Restarted_Then_DiscoveriesRemainAndTheJobDoesNotResume()
    {
        // Arrange
        AddReceipt(1, 20_000);
        using var service = CreateService();
        await service.GetAddressAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.StartRescanAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken);

        // Act
        var stopped = await service.CancelRescanAsync(TestContext.Current.CancellationToken);
        using var restarted = CreateService();

        // Assert
        Assert.False(stopped.IsRescanning);
        Assert.Equal(1, stopped.FoundOutputs);
        Assert.False(await restarted.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await UnspentAsync());
    }

    [Fact]
    public async Task Given_ReorganizedRecoveryCursor_When_Resuming_Then_NothingAdvancesBeforeCanonicalRollback()
    {
        // Arrange
        using var service = CreateService();
        await service.GetAddressAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.StartRescanAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken);
        _blocks[1].Header.Nonce++;

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1u, (await service.GetStatusAsync(TestContext.Current.CancellationToken)).RescanCursorHeight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_FailedRecoverySave_When_Retried_Then_CoinsJournalAndCheckpointCommitTogether(bool finalization)
    {
        // Arrange
        AddReceipt(1, 20_000);
        using var service = CreateService();
        await service.GetAddressAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.StartRescanAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        if (finalization)
            for (var i = 0; i < 4; i++)
                await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken);
        using var crashing = CreateService(new CrashingScopeFactory(_provider.GetRequiredService<IServiceScopeFactory>()));

        // Act
        await Assert.ThrowsAsync<SimulatedCrashException>(() => crashing.ProcessNextBlockAsync(TestContext.Current.CancellationToken));

        // Assert: neither a cursor nor a selectable coin escapes its failed database save.
        var failed = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.True(failed.IsRescanning);
        Assert.Equal(finalization ? 4u : 0u, failed.RescanCursorHeight);
        Assert.Equal(finalization ? 1 : 0, failed.FoundOutputs);
        Assert.Empty(await UnspentAsync());
        Assert.Empty(_provider.GetRequiredService<IUtxoMemoryRepository>().GetUnreservedUtxos());
        Assert.True(await crashing.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        if (finalization)
        {
            Assert.Single(await UnspentAsync());
            Assert.Single(_provider.GetRequiredService<IUtxoMemoryRepository>().GetUnreservedUtxos());
            Assert.False((await service.GetStatusAsync(TestContext.Current.CancellationToken)).IsRescanning);
        }
        else
        {
            Assert.Equal(1u, (await service.GetStatusAsync(TestContext.Current.CancellationToken)).RescanCursorHeight);
            Assert.Empty(await UnspentAsync());
        }
        using var scope = _provider.CreateScope();
        var journal = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingEventDbRepository
            .GetByKeyPrefixAsync("wallet:", TestContext.Current.CancellationToken);
        Assert.Single(journal);
    }

    [Fact]
    public async Task Given_RecoveredCoinSpentInMempool_When_Finalized_Then_ItIsNotSelectableAndRecoveryWaitsForResolution()
    {
        // Arrange
        var received = AddReceipt(1, 20_000);
        using var service = CreateService();
        await service.GetAddressAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.StartRescanAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        for (var i = 0; i < 4; i++)
            await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken);
        _mempoolSpends.Add(received);

        // Act / Assert
        Assert.False(await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await UnspentAsync());
        Assert.True((await service.GetStatusAsync(TestContext.Current.CancellationToken)).IsRescanning);
        _mempoolSpends.Remove(received);
        Assert.True(await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        Assert.Single(await UnspentAsync());
        Assert.False((await service.GetStatusAsync(TestContext.Current.CancellationToken)).IsRescanning);
    }

    [Fact]
    public async Task Given_CompletedHistory_When_RescannedAgain_Then_ReceiptsSpendsAndSpendableCoinsAreNotDuplicated()
    {
        // Arrange
        var spent = AddReceipt(1, 20_000);
        AddSpend(2, spent);
        AddReceipt(3, 30_000);
        using var service = CreateService();
        await service.GetAddressAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.StartRescanAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        for (var i = 0; i < 5; i++)
            await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken);

        // Act
        await service.StartRescanAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        for (var i = 0; i < 5; i++)
            await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(await UnspentAsync());
        using var scope = _provider.CreateScope();
        var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        Assert.Equal(2, (await work.SilentPaymentDbRepository.GetOutputsAsync(TestContext.Current.CancellationToken)).Count);
        var events = await work.AccountingEventDbRepository.GetByKeyPrefixAsync("wallet:", TestContext.Current.CancellationToken);
        Assert.Equal(3, events.Count);
        Assert.Equal(30_000_000, events.Sum(fact => fact.AmountMsat));
    }

    [Fact]
    public async Task Given_TipAdvanceDuringFinalProof_When_RecoveryFinishes_Then_NoStaleCoinOrCompletedJobIsPublished()
    {
        // Arrange
        AddReceipt(1, 20_000);
        using var service = CreateService();
        await service.GetAddressAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.StartRescanAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        for (var i = 0; i < 4; i++)
            await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken);
        _chain.SetupSequence(chain => chain.GetCurrentBlockHeightAsync()).ReturnsAsync(5u).ReturnsAsync(6u);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await UnspentAsync());
        Assert.Empty(_provider.GetRequiredService<IUtxoMemoryRepository>().GetUnreservedUtxos());
        Assert.True((await service.GetStatusAsync(TestContext.Current.CancellationToken)).IsRescanning);
        _chain.Setup(chain => chain.GetCurrentBlockHeightAsync()).ReturnsAsync(5u);
        Assert.True(await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        Assert.Single(await UnspentAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_PolicyIgnoredReceipt_When_ThresholdLoweredAndRescanned_Then_ActualHistoricalOwnershipIsRecovered(bool alreadySpent)
    {
        // Arrange: under the old policy a match is retained as ignored metadata, without wallet accounting.
        _options.MinReceiveSat = 1_000;
        var received = AddReceipt(1, 500);
        if (alreadySpent)
            AddSpend(2, received);
        using var service = CreateService();
        await service.GetAddressAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.StartRescanAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        for (var i = 0; i < 5; i++)
            await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, (await service.GetStatusAsync(TestContext.Current.CancellationToken)).IgnoredOutputs);
        Assert.Empty(await UnspentAsync());

        // Act: rescan with the operator's new minimum; a historical receipt still cannot become selectable early.
        _options.MinReceiveSat = 0;
        await service.StartRescanAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await UnspentAsync());
        Assert.Empty(_provider.GetRequiredService<IUtxoMemoryRepository>().GetUnreservedUtxos());
        for (var i = 0; i < 4; i++)
            await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken);

        // Assert
        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.False(status.IsRescanning);
        Assert.Equal(0, status.IgnoredOutputs);
        Assert.Equal(1, status.FoundOutputs);
        Assert.Equal(alreadySpent ? 0 : 1, (await UnspentAsync()).Length);
        using var scope = _provider.CreateScope();
        var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var output = Assert.Single(await work.SilentPaymentDbRepository.GetOutputsAsync(TestContext.Current.CancellationToken));
        Assert.False(output.Ignored);
        Assert.Equal(alreadySpent, output.SpentByTransactionId is not null);
        var events = await work.AccountingEventDbRepository.GetByKeyPrefixAsync("wallet:", TestContext.Current.CancellationToken);
        Assert.Equal(alreadySpent ? 2 : 1, events.Count);
        Assert.Equal(alreadySpent ? 0 : 500_000L, events.Sum(fact => fact.AmountMsat));
    }

    [Fact]
    public async Task Given_MoreThanOneRecoveryBatch_When_RestartedAndTipRaces_Then_RpcBudgetAndCommittedProgressArePreserved()
    {
        // Arrange
        AddReceiptGroup(1, 205, 100);
        using var service = CreateService();
        await service.GetAddressAsync(cancellationToken: TestContext.Current.CancellationToken);
        await service.StartRescanAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        for (var i = 0; i < 4; i++)
            await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken);
        _chain.Invocations.Clear();

        // Act: materialize only the first bounded batch, then restart with the rest still durable.
        Assert.True(await service.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        Assert.Equal(100, (await UnspentAsync()).Length);
        Assert.True((await service.GetStatusAsync(TestContext.Current.CancellationToken)).IsRescanning);
        Assert.Equal(100, _chain.Invocations.Count(call => call.Method.Name == nameof(IBitcoinChainService.GetConfirmedUnspentOutputAsync)));
        using var restarted = CreateService();
        _chain.SetupSequence(chain => chain.GetCurrentBlockHeightAsync()).ReturnsAsync(5u).ReturnsAsync(6u);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        Assert.Equal(100, (await UnspentAsync()).Length);
        _chain.Setup(chain => chain.GetCurrentBlockHeightAsync()).ReturnsAsync(5u);
        _chain.Invocations.Clear();
        Assert.True(await restarted.ProcessNextBlockAsync(TestContext.Current.CancellationToken));
        Assert.Equal(200, (await UnspentAsync()).Length);
        Assert.Equal(100, _chain.Invocations.Count(call => call.Method.Name == nameof(IBitcoinChainService.GetConfirmedUnspentOutputAsync)));
        using var last = CreateService();
        _chain.Invocations.Clear();
        Assert.True(await last.ProcessNextBlockAsync(TestContext.Current.CancellationToken));

        // Assert: already committed coins require no repeat RPC proofs or journal facts.
        Assert.Equal(205, (await UnspentAsync()).Length);
        Assert.Equal(5, _chain.Invocations.Count(call => call.Method.Name == nameof(IBitcoinChainService.GetConfirmedUnspentOutputAsync)));
        Assert.False((await last.GetStatusAsync(TestContext.Current.CancellationToken)).IsRescanning);
        using var scope = _provider.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingEventDbRepository
            .GetByKeyPrefixAsync("wallet:", TestContext.Current.CancellationToken);
        Assert.Equal(205, events.Count);
        Assert.Equal(20_500_000L, events.Sum(fact => fact.AmountMsat));
    }

    private SilentPaymentService CreateService(IServiceScopeFactory? scopes = null) => new(scopes ?? _provider.GetRequiredService<IServiceScopeFactory>(), _chain.Object,
        _monitor.Object, _prevouts.Object, _scanner, _keys, _crypto, MsOptions.Create(_options),
        MsOptions.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }), NullLogger<SilentPaymentService>.Instance);

    private async Task<UtxoModel[]> UnspentAsync()
    {
        using var scope = _provider.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().UtxoDbRepository.GetUnspentAsync()).ToArray();
    }

    private OutPoint AddReceipt(uint height, long amount)
    {
        using var sender = new Key(FixtureKeys.Secret(1));
        var previous = new OutPoint(new uint256(height), 0);
        var output = Assert.Single(_crypto.DeriveOutputs([new SilentPaymentSenderInput(previous.ToBytes(), sender.ToBytes(), false)],
            [new SilentPaymentRecipient(_keys.ScanPubKey, _keys.SpendPubKey)]));
        var transaction = Transaction.Create(Network.RegTest);
        transaction.Inputs.Add(new TxIn(previous) { WitScript = new WitScript([new byte[64], sender.PubKey.ToBytes()]) });
        var script = new Script(new byte[] { 0x51, 0x20 }.Concat(output.OutputKey32).ToArray());
        transaction.Outputs.Add(new TxOut(Money.Satoshis(amount), script));
        _blocks[height].Transactions.Add(transaction);
        _blocks[height].UpdateMerkleRoot();
        var point = new OutPoint(transaction.GetHash(), 0);
        _unspent[point] = (transaction.Outputs[0], height);
        return point;
    }

    private void AddReceiptGroup(uint height, int count, long amount)
    {
        using var sender = new Key(FixtureKeys.Secret(1));
        var previous = new OutPoint(new uint256(height), 0);
        var recipients = Enumerable.Range(0, count).Select(_ => new SilentPaymentRecipient(_keys.ScanPubKey, _keys.SpendPubKey)).ToArray();
        var outputs = _crypto.DeriveOutputs([new SilentPaymentSenderInput(previous.ToBytes(), sender.ToBytes(), false)], recipients);
        var transaction = Transaction.Create(Network.RegTest);
        transaction.Inputs.Add(new TxIn(previous) { WitScript = new WitScript([new byte[64], sender.PubKey.ToBytes()]) });
        foreach (var output in outputs)
            transaction.Outputs.Add(new TxOut(Money.Satoshis(amount), new Script(new byte[] { 0x51, 0x20 }.Concat(output.OutputKey32).ToArray())));
        _blocks[height].Transactions.Add(transaction);
        _blocks[height].UpdateMerkleRoot();
        for (uint index = 0; index < transaction.Outputs.Count; index++)
            _unspent[new OutPoint(transaction.GetHash(), index)] = (transaction.Outputs[(int)index], height);
    }

    private static void AssertRecoveredBooks(IEnumerable<Domain.Accounting.Models.AccountingEventModel> facts,
        long wallet, long transfersIn, long transfersOut, long fees)
    {
        var balances = facts.SelectMany(fact => AccountingPostingRules.Post(fact, _ => null))
            .GroupBy(posting => posting.Account).ToDictionary(group => group.Key, group => group.Sum(posting => posting.AmountMsat));
        Assert.Equal(wallet, balances.GetValueOrDefault(AccountRole.Wallet));
        Assert.Equal(0L, balances.GetValueOrDefault(AccountRole.Clearing));
        Assert.Equal(transfersIn, balances.GetValueOrDefault(AccountRole.TransfersIn));
        Assert.Equal(transfersOut, balances.GetValueOrDefault(AccountRole.TransfersOut));
        Assert.Equal(fees, balances.GetValueOrDefault(AccountRole.FeeWithdraw));
        Assert.Equal(0L, balances.Values.Sum());
    }

    private void AddSpend(uint height, OutPoint point)
    {
        var transaction = Transaction.Create(Network.RegTest);
        transaction.Inputs.Add(new TxIn(point));
        transaction.Outputs.Add(new TxOut(Money.Satoshis(Math.Min(10_000, _unspent[point].Output.Value.Satoshi - 1)), Script.Empty));
        _blocks[height].Transactions.Add(transaction);
        _blocks[height].UpdateMerkleRoot();
        _unspent.Remove(point);
    }

    private static Block NewBlock(uint height)
    {
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        block.Header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + height);
        block.Header.Nonce = height;
        return block;
    }

    private static IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>> Prevouts(BitcoinBlock block)
    {
        using var sender = new Key(FixtureKeys.Secret(1));
        return Block.Load(block.BlockData, Network.Main).Transactions.ToDictionary(transaction => new TxId(transaction.GetHash().ToBytes()),
            transaction => (IReadOnlyList<BitcoinPrevout>)transaction.Inputs.Select(_ =>
                new BitcoinPrevout(100_000, new BitcoinScript(sender.PubKey.WitHash.ScriptPubKey.ToBytes()))).ToArray());
    }

    private sealed class CrashingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _scopes;
        public IServiceScope CreateScope()
        {
            var scope = inner.CreateScope();
            return Interlocked.Increment(ref _scopes) == 2 ? new CrashingScope(scope) : scope;
        }

        private sealed class CrashingScope(IServiceScope inner) : IServiceScope, IServiceProvider
        {
            private CrashingUnitOfWork? _unitOfWork;
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) => serviceType == typeof(IUnitOfWork)
                ? _unitOfWork ??= new CrashingUnitOfWork(inner.ServiceProvider.GetRequiredService<IUnitOfWork>(), 1)
                : inner.ServiceProvider.GetService(serviceType);
            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class FixtureKeys : ISilentPaymentKeySource
    {
        public CompactPubKey ScanPubKey => PublicKey(Secret(3));
        public CompactPubKey SpendPubKey => PublicKey(Secret(4));
        public bool RecoverableElsewhere => true;

        public void ComputeScanSharedSecret(ReadOnlySpan<byte> tweakedInputKey, Span<byte> point33)
        {
            ECPubKey.TryCreate(tweakedInputKey, Context.Instance, out _, out var point);
            using var scan = ECPrivKey.Create(Secret(3), Context.Instance);
            point!.GetSharedPubkey(scan).WriteToSpan(true, point33, out _);
        }

        public void GetLabelTweak(uint label, Span<byte> scalar32)
        {
            var tag = SHA256.HashData(Encoding.ASCII.GetBytes("BIP0352/Label"));
            var data = new byte[100];
            tag.CopyTo(data, 0);
            tag.CopyTo(data, 32);
            Secret(3).CopyTo(data, 64);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(96), label);
            SHA256.HashData(data).CopyTo(scalar32);
        }

        public CompactPubKey GetLabelPoint(uint label)
        {
            var tweak = new byte[32];
            GetLabelTweak(label, tweak);
            return PublicKey(tweak);
        }

        public static byte[] Secret(byte value) => [.. new byte[31], value];

        private static CompactPubKey PublicKey(byte[] secret)
        {
            using var key = new Key(secret);
            return new CompactPubKey(key.PubKey.ToBytes());
        }
    }
}