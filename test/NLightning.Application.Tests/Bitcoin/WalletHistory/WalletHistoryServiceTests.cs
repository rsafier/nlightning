using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace NLightning.Application.Tests.Bitcoin.WalletHistory;

using Application.Bitcoin.WalletHistory;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.Hashes;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;
using TestUtils;

public sealed class WalletHistoryServiceTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nltg-history-{Guid.NewGuid():N}.db");
    private readonly Dictionary<uint, Block> _blocks = [];
    private readonly Mock<IBitcoinChainService> _chain = new();
    private readonly Mock<IBlockPrevoutSource> _prevouts = new();
    private readonly Mock<ISilentPaymentRecoveryAddressSource> _addresses = new();
    private readonly WalletHistoryGate _gate = new();
    private ServiceProvider _provider = null!;
    private Transaction _deposit = null!, _send = null!;
    private Script _walletScript = null!;

    public async ValueTask InitializeAsync()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Database:Provider"] = "sqlite", ["Database:ConnectionString"] = $"Data Source={_path}" }).Build();
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton<ISha256, Sha256>();
        services.AddPersistenceInfrastructureServices(config); services.AddRepositoriesInfrastructureServices();
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database.MigrateAsync(TestContext.Current.CancellationToken);
        using var key = new Key();
        var address = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        _walletScript = address.ScriptPubKey;
        _addresses.Setup(source => source.StageAddressesAsync(It.IsAny<IUnitOfWork>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new WalletAddressModel(AddressType.P2Wpkh, 0, false, address.ToString()) });
        _deposit = Network.RegTest.CreateTransaction();
        _deposit.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        _deposit.Outputs.Add(Money.Satoshis(100_000), _walletScript);
        _send = Network.RegTest.CreateTransaction();
        _send.Inputs.Add(new TxIn(new OutPoint(_deposit.GetHash(), 0)));
        _send.Outputs.Add(Money.Satoshis(40_000), _walletScript);
        _send.Outputs.Add(Money.Satoshis(50_000), Script.Empty);
        _blocks[1] = CreateBlock(1, _deposit); _blocks[2] = CreateBlock(2, _send);
        _chain.Setup(chain => chain.GetCurrentBlockHeightAsync()).ReturnsAsync(2u);
        _chain.Setup(chain => chain.GetBlockDataStartHeightAsync()).ReturnsAsync(0u);
        _chain.Setup(chain => chain.GetBlockAsync(It.IsAny<uint>())).ReturnsAsync((uint height) => _blocks[height]);
        _chain.Setup(chain => chain.GetBlockHashAsync(It.IsAny<uint>())).ReturnsAsync((uint height) => _blocks[height].GetHash());
        _prevouts.Setup(source => source.ValidateHeightAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _prevouts.Setup(source => source.GetAllPrevoutsAsync(It.IsAny<BitcoinBlock>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BitcoinBlock _, uint height, CancellationToken _) => new Dictionary<TxId, IReadOnlyList<BitcoinPrevout>>
            {
                [new TxId((height == 1 ? _deposit : _send).GetHash().ToBytes())] =
                    [new BitcoinPrevout(100_000, new BitcoinScript(height == 1 ? [0x00, 0x14, .. new byte[20]] : _walletScript.ToBytes()))]
            });
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync(); SqliteTestPools.Clear(_path);
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
    }

    private WalletHistoryService Service() => new(_provider.GetRequiredService<IServiceScopeFactory>(), _chain.Object,
        _prevouts.Object, _gate, _addresses.Object, MsOptions.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
        NullLogger<WalletHistoryService>.Instance);

    [Fact]
    public async Task Given_PreMigrationSpentOutputs_When_RescanRestarts_Then_RawHistoryAndCursorResumeWithoutAccounting()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var first = Service();
        await first.StartRescanAsync(1, cancellationToken: ct);
        await first.ProcessNextBlockAsync(ct);
        Assert.Equal(1u, (await first.GetStatusAsync(ct))!.CursorHeight);

        // Act: a new worker uses only the persisted job, not the first worker's memory.
        using var restarted = Service();
        await restarted.ProcessNextBlockAsync(ct);

        // Assert
        Assert.False((await restarted.GetStatusAsync(ct))!.IsActive);
        using var scope = _provider.CreateScope();
        var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var rows = await work.WalletTransactionDbRepository.GetHistoryAsync(0, 2, false, ct);
        Assert.Equal(2, rows.Count);
        var send = Assert.Single(rows, row => row.TxId == new TxId(_send.GetHash().ToBytes()));
        Assert.Equal(_send.ToBytes(), send.RawTransaction);
        Assert.Equal(_blocks[2].GetHash().ToBytes(), send.BlockHash);
        Assert.Equal(new[] { new WalletTransactionInput(0, 100_000) }, send.OurInputs);
        Assert.Equal(new uint[] { 0 }, send.OurOutputs);
        Assert.Empty(await work.UtxoDbRepository.GetUnspentAsync());
        Assert.Empty(await work.AccountingEventDbRepository.GetByKeyPrefixAsync("wallet:", ct));
        Assert.Null(await work.BlockchainStateDbRepository.GetStateAsync());
    }

    [Fact]
    public async Task Given_ParentBeforeRequestedBirthday_When_SendIsRecovered_Then_UndoProvesInputsWithoutTxindex()
    {
        // Arrange / Act
        var ct = TestContext.Current.CancellationToken;
        using var service = Service();
        await service.StartRescanAsync(2, cancellationToken: ct);
        await service.ProcessNextBlockAsync(ct);

        // Assert: held change is a send, never mislabeled as a positive deposit.
        using var scope = _provider.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().WalletTransactionDbRepository.GetHistoryAsync(0, 2, false, ct);
        var row = Assert.Single(rows);
        Assert.Equal(100_000, Assert.Single(row.OurInputs).AmountSat);
        _chain.Verify(chain => chain.GetTransactionAsync(It.IsAny<uint256>()), Times.Never);
    }

    [Fact]
    public async Task Given_PrunedBirthday_When_Requested_Then_NoCheckpointIsWrittenUnlessPartialIsExplicit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _chain.Setup(chain => chain.GetBlockDataStartHeightAsync()).ReturnsAsync(2u);
        using var service = Service();

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartRescanAsync(1, cancellationToken: ct));
        Assert.Null(await service.GetStatusAsync(ct));
        var partial = await service.StartRescanAsync(1, allowPartial: true, cancellationToken: ct);
        Assert.True(partial.IsPartial); Assert.Equal(1u, partial.RequestedFromHeight); Assert.Equal(2u, partial.AvailableFromHeight);
    }

    [Fact]
    public async Task Given_ChainChangesDuringPreparation_When_BlockCannotCommit_Then_RowsAndCursorRemainUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var service = Service();
        await service.StartRescanAsync(1, cancellationToken: ct);
        _chain.SetupSequence(chain => chain.GetBlockHashAsync(1)).ReturnsAsync(_blocks[1].GetHash()).ReturnsAsync(uint256.One);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProcessNextBlockAsync(ct));
        Assert.Equal(0u, (await service.GetStatusAsync(ct))!.CursorHeight);
        using var scope = _provider.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().WalletTransactionDbRepository
            .GetByIdAsync(new TxId(_deposit.GetHash().ToBytes()), ct));
    }

    [Fact]
    public async Task Given_IndexedHistoryAndIndependentLabel_When_PagedAndReorged_Then_RawProjectionIsDeferredAndLabelSurvives()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var service = Service(); await service.StartRescanAsync(1, cancellationToken: ct);
        await service.ProcessNextBlockAsync(ct); await service.ProcessNextBlockAsync(ct);
        var id = new TxId(_send.GetHash().ToBytes());
        using (var scope = _provider.CreateScope())
        {
            var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            Assert.True(await work.WalletTransactionDbRepository.StageLabelAsync(id, "Recovered withdrawal", false, ct));
            await work.SaveChangesAsync();
        }

        // Act / Assert
        using var read = _provider.CreateScope(); var unit = read.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var page = await unit.WalletTransactionDbRepository.GetHistoryPageAsync(0, 2, false, 0, 1, ct);
        Assert.Single(page); Assert.Empty(page[0].RawTransaction); Assert.NotNull(page[0].OwnershipSummary);
        Assert.False(await unit.WalletTransactionDbRepository.StageLabelAsync(id, "Replace", false, ct));
        await unit.WalletTransactionDbRepository.UnconfirmAboveAsync(1); await unit.SaveChangesAsync();
        Assert.Equal("Recovered withdrawal", await unit.WalletTransactionDbRepository.GetLabelAsync(id, ct));
        Assert.Null((await unit.WalletTransactionDbRepository.GetByIdAsync(id, ct))!.BlockHeight);
        Assert.Equal(_send.ToBytes(), (await unit.WalletTransactionDbRepository.GetByIdAsync(id, ct))!.RawTransaction);
    }

    [Fact]
    public async Task Given_LegacyInputOwnership_When_ReplayKnowsOnlyOutputs_Then_IncompleteProjectionKeepsRawFallback()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = new TxId(_send.GetHash().ToBytes());
        using (var scope = _provider.CreateScope())
        {
            var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await work.WalletTransactionDbRepository.StageConfirmedAsync(new WalletTransactionRecord(id, _send.ToBytes(),
                2, _blocks[2].GetHash().ToBytes(), _blocks[2].Header.BlockTime, [0], [new WalletTransactionInput(0, 100_000)]));
            await work.SaveChangesAsync();
        }

        // Act: a replay describes the held output but cannot rediscover its already-removed input.
        using var replay = _provider.CreateScope();
        var unit = replay.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unit.WalletTransactionDbRepository.StageConfirmedAsync(WalletTransactionHistory.Describe(_send, 2,
            _blocks[2].GetHash().ToBytes(), _blocks[2].Header.BlockTime, [0], []));
        await unit.SaveChangesAsync();

        // Assert: raw history still carries all original ownership, rather than displaying a positive deposit.
        var row = Assert.Single(await unit.WalletTransactionDbRepository.GetHistoryPageAsync(0, 2, false, 0, 1, ct));
        Assert.Null(row.OwnershipSummary);
        Assert.Equal(_send.ToBytes(), row.RawTransaction);
        Assert.Equal(new[] { new WalletTransactionInput(0, 100_000) }, row.OurInputs);
    }

    private static Block CreateBlock(uint height, Transaction transaction)
    {
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        var coinbase = Network.RegTest.CreateTransaction();
        coinbase.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, uint.MaxValue))); coinbase.Outputs.Add(Money.Coins(50), Script.Empty);
        block.Transactions.Add(coinbase); block.Transactions.Add(transaction);
        block.Header.BlockTime = DateTimeOffset.FromUnixTimeSeconds(height); block.UpdateMerkleRoot();
        return block;
    }
}