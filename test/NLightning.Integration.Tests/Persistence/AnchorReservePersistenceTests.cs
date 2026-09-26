using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Onchain;
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-379 (the on-chain reserve of anchors channels) and NL-385 (channel funding skips outputs our pending broadcasts
/// spend) over the real <see cref="UtxoMemoryRepository"/> and the SQLite schema: the reserve math against the wallet,
/// refusals as opener and fundee, a funding that cannot dip below the reserve, a fee bump that may, and the restart
/// case.
/// </summary>
public class AnchorReservePersistenceTests
{
    private const uint Height = 200;
    private static readonly LightningMoney s_feeRate = LightningMoney.Satoshis(2_500);

    [Fact]
    public async Task Given_TwoAnchorsChannels_When_StatusRead_Then_ReserveAndSpendableFollowTheWallet()
    {
        // Arrange: two anchors channels, a legacy one and a closed anchors one (only the first two count)
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [(true, ChannelState.Open), (true, ChannelState.Failed),
                                                            (false, ChannelState.Open), (true, ChannelState.Closed)],
                                                 60_000, 25_000);

        // Act
        var status = await node.Reserve.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, status.AnchorsChannelCount);
        Assert.Equal(20_000, status.RequiredReserve.Satoshi);
        Assert.Equal(85_000, status.AvailableBalance.Satoshi);
        Assert.Equal(65_000, status.SpendableBalance.Satoshi);
        Assert.False(status.IsBelowReserve);
    }

    [Fact]
    public async Task Given_AFundingThatWouldDipBelowTheReserve_When_Locked_Then_ItIsRefusedAndNothingIsLocked()
    {
        // Arrange: one anchors channel, the new one makes two: 20,000 sat reserve, 85,000 sat in the wallet
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [(true, ChannelState.Open)], 60_000, 25_000);
        var channelId = NewChannelId();

        // Act / Assert: 70,000 leaves 15,000 < 20,000
        await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.EnsureCanFundAsync(LightningMoney.Satoshis(70_000), true,
                                                  TestContext.Current.CancellationToken));
        var exception = await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(70_000), channelId, true,
                                                     TestContext.Current.CancellationToken));
        Assert.Equal(20_000, exception.RequiredReserve.Satoshi);
        Assert.Empty(node.Utxos.GetLockedUtxosForChannel(channelId));
        Assert.Equal(2, node.Utxos.GetUnreservedUtxos().Count);

        // A funding that keeps the reserve goes through: 60,000 leaves 25,000
        await node.Reserve.EnsureCanFundAsync(LightningMoney.Satoshis(60_000), true,
                                              TestContext.Current.CancellationToken);
        var locked = await node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(60_000), channelId, true,
                                                              TestContext.Current.CancellationToken);
        Assert.Equal(60_000, Assert.Single(locked).Amount.Satoshi);
    }

    [Fact]
    public async Task Given_ALegacyChannelFunding_When_Locked_Then_OnlyTheExistingAnchorsChannelsReserveIsKept()
    {
        // Arrange: one anchors channel (10,000 sat reserve); the new channel has no anchors
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [(true, ChannelState.Open)], 60_000, 25_000);

        // Act / Assert: 75,000 leaves exactly 10,000; 75,001 would not
        await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(75_001), NewChannelId(), false,
                                                     TestContext.Current.CancellationToken));
        var locked = await node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(75_000), NewChannelId(), false,
                                                              TestContext.Current.CancellationToken);
        Assert.Equal(85_000, locked.Sum(u => u.Amount.Satoshi));
    }

    [Fact]
    public async Task Given_NoAnchorsChannels_When_AFundingTakesTheWholeWallet_Then_NoReserveIsKept()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [], 60_000, 25_000);

        // Act
        var locked = await node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(85_000), NewChannelId(), false,
                                                              TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, locked.Count);
    }

    [Fact]
    public async Task Given_AFundingThatLeftOnlyTheReserve_When_AFeeBumpReserves_Then_ItMaySpendTheReserve()
    {
        // Arrange: the funding keeps 25,000 sat (>= the 20,000 sat reserve) out of reach of fundings
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [(true, ChannelState.Open)], 60_000, 25_000);
        await node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(60_000), NewChannelId(), true,
                                                 TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(16_000), NewChannelId(), false,
                                                     TestContext.Current.CancellationToken));

        // Act: a CPFP child's fee inputs come from the reserve
        var reservation = await node.Selector.ReserveAsync(LightningMoney.Satoshis(10_000), s_feeRate, 700,
                                                           "cpfp:reserve", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(25_000, Assert.Single(reservation.Inputs).Amount.Satoshi);
        var status = await node.Reserve.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.True(status.IsBelowReserve);
        Assert.True(status.SpendableBalance.IsZero);
    }

    [Fact]
    public async Task Given_AWalletBelowTheReserve_When_AnAnchorsChannelIsOffered_Then_TheFundeeRefusesIt()
    {
        // Arrange: two anchors channels already; a third needs 30,000 sat, the wallet has 25,000
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [(true, ChannelState.Open), (true, ChannelState.Open)],
                                                 25_000);

        // Act / Assert
        var exception = await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.EnsureCanAcceptAnchorsChannelAsync(TestContext.Current.CancellationToken));
        Assert.Equal(30_000, exception.RequiredReserve.Satoshi);
        Assert.Equal(25_000, exception.Available.Satoshi);
    }

    [Fact]
    public async Task Given_AWalletCoveringTheReserve_When_AnAnchorsChannelIsOffered_Then_TheFundeeAcceptsIt()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [(true, ChannelState.Open)], 20_000);

        // Act / Assert (does not throw: 20,000 covers two channels' reserve)
        await node.Reserve.EnsureCanAcceptAnchorsChannelAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_UnconfirmedOutputs_When_StatusRead_Then_OnlyConfirmedOnesBackTheReserve()
    {
        // Arrange: a 50,000 sat output mined two blocks ago (not confirmed by the wallet's three-block rule)
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [(true, ChannelState.Open)], 5_000);
        await node.AddUtxoAsync(database, 50_000, Height - 1);

        // Act
        var status = await node.Reserve.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(5_000, status.AvailableBalance.Satoshi);
        Assert.True(status.IsBelowReserve);
        await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.EnsureCanAcceptAnchorsChannelAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_APendingBroadcast_When_TheNodeRestartsAndFundsAChannel_Then_ItsInputIsNotPicked()
    {
        // Arrange (NL-385): our funding transaction spends the largest output and is broadcast but not mined; its
        // channel lock lived in memory only, so after the restart only its BroadcastTransactions row says it is spent
        using var database = new SqliteTestDatabase();
        var before = await ReserveNode.CreateAsync(database, [], 500_000, 40_000);
        var largest = before.Utxos.GetUnreservedUtxos().MaxBy(u => u.Amount.Satoshi)!;
        var funding = Network.RegTest.CreateTransaction();
        funding.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])largest.TxId), largest.Index)));
        funding.Outputs.Add(Money.Satoshis(499_000), new Key().PubKey.WitHash.ScriptPubKey);
        await using (var context = database.CreateContext())
        {
            new BroadcastTransactionDbRepository(context).Add(new BroadcastTransactionModel(
                                                                  new SignedTransaction(
                                                                      funding.GetHash().ToBytes(), funding.ToBytes()),
                                                                  BroadcastPurpose.Funding, null, 100));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var after = await ReserveNode.RestartAsync(database, []);

        // Act
        var locked = await after.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(30_000), NewChannelId(), false,
                                                               TestContext.Current.CancellationToken);

        // Assert: the smaller output funds it, the pending broadcast's input is neither locked nor counted
        var input = Assert.Single(locked);
        Assert.Equal(40_000, input.Amount.Satoshi);
        Assert.Null(after.Utxos.GetUnreservedUtxos().Single(u => u.TxId.Equals(largest.TxId)).LockedToChannelId);
        Assert.True((await after.Reserve.GetStatusAsync(TestContext.Current.CancellationToken)).AvailableBalance.IsZero);
        Assert.Throws<InvalidOperationException>(
            () => after.Utxos.LockUtxosToSpendOnChannel(LightningMoney.Satoshis(100_000), NewChannelId(),
                                                        LightningMoney.Zero,
                                                        new HashSet<(TxId, uint)> { (largest.TxId, largest.Index) }));
    }

    private static ChannelId NewChannelId() => new(RandomUtils.GetBytes(32));

    /// <summary>A wallet (UTXO memory set on SQLite), its channels and the reserve service over them.</summary>
    private sealed class ReserveNode
    {
        public required UtxoMemoryRepository Utxos { get; init; }
        public required AnchorReserveService Reserve { get; init; }
        public required FeeInputSelector Selector { get; init; }

        private int _nextIndex = 100;

        public static async Task<ReserveNode> CreateAsync(SqliteTestDatabase database,
                                                          (bool Anchors, ChannelState State)[] channels,
                                                          params long[] amounts)
        {
            var node = Create(database, channels);
            foreach (var amount in amounts)
                await node.AddUtxoAsync(database, amount, 100);
            return node;
        }

        public static async Task<ReserveNode> RestartAsync(SqliteTestDatabase database,
                                                           (bool Anchors, ChannelState State)[] channels)
        {
            var node = Create(database, channels);
            using var uow = CreateUnitOfWork(database, node.Utxos);
            node.Utxos.Load((await uow.UtxoDbRepository.GetUnspentAsync(includeWalletAddress: true)).ToList());
            node.Utxos.LoadFeeReservations(await uow.FeeInputReservationDbRepository.GetReservedOutpointsAsync());
            return node;
        }

        public async Task AddUtxoAsync(SqliteTestDatabase database, long amount, uint blockHeight)
        {
            using var uow = CreateUnitOfWork(database, Utxos);
            var index = (uint)_nextIndex++;
            var address = new WalletAddressModel(AddressType.P2Wpkh, index, false,
                                                 new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest)
                                                          .ToString());
            uow.WalletAddressesDbRepository.AddRange([address]);
            uow.AddUtxo(new UtxoModel(new TxId(RandomUtils.GetBytes(32)), 0, LightningMoney.Satoshis(amount),
                                      blockHeight, address));
            await uow.SaveChangesAsync();
        }

        private static ReserveNode Create(SqliteTestDatabase database, (bool Anchors, ChannelState State)[] channels)
        {
            var utxos = new UtxoMemoryRepository();
            var walletService = new Mock<IBitcoinWalletService>();
            var change = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
            walletService.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, true))
                         .ReturnsAsync(new WalletAddressModel(AddressType.P2Wpkh, 0, true, change));

            var services = new ServiceCollection();
            services.AddScoped<IUnitOfWork>(_ => CreateUnitOfWork(database, utxos));
            services.AddScoped(_ => walletService.Object);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var models = channels.Select(c => CreateChannel(c.Anchors, c.State)).ToList();
            var channelMemory = new Mock<IChannelMemoryRepository>();
            channelMemory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                         .Returns((Func<ChannelModel, bool> predicate) => models.Where(predicate).ToList());
            var monitor = new Mock<IBlockchainMonitor>();
            monitor.Setup(m => m.LastProcessedBlockHeight).Returns(Height);

            var nodeOptions = Microsoft.Extensions.Options.Options.Create(
                new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest });
            return new ReserveNode
            {
                Utxos = utxos,
                Reserve = new AnchorReserveService(utxos, channelMemory.Object, monitor.Object, scopeFactory,
                                                   nodeOptions, NullLogger<AnchorReserveService>.Instance),
                Selector = new FeeInputSelector(utxos, scopeFactory, nodeOptions,
                                                NullLogger<FeeInputSelector>.Instance)
            };
        }

        private static ChannelModel CreateChannel(bool anchors, ChannelState state)
        {
            var pubKey = new CompactPubKey(new Key().PubKey.ToBytes());
            var party = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(1_000),
                                         LightningMoney.Satoshis(1), 30, LightningMoney.Satoshis(100_000), 144, null);
            var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(253), 3, anchors,
                                                  FeatureSupport.No);
            var keySet = new ChannelKeySetModel(0, pubKey, pubKey, pubKey, pubKey, pubKey, pubKey);
            return new ChannelModel(channelParams, NewChannelId(), null, null, true, null, null,
                                    LightningMoney.Satoshis(100_000), keySet, 0, 0, LightningMoney.Zero, null, 0,
                                    pubKey, 0, state, ChannelVersion.V1);
        }

        private static UnitOfWork CreateUnitOfWork(SqliteTestDatabase database, UtxoMemoryRepository utxos) =>
            new(database.CreateContext(), new Mock<ILogger<UnitOfWork>>().Object, new Mock<ISha256>().Object, utxos);
    }
}