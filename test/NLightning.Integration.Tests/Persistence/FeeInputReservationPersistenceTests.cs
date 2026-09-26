using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Bitcoin;
using Infrastructure.Repositories.Memory;

/// <summary>
/// Fee input reservations (BOLT 5 plan O7-T1, migration <c>AddFeeInputReservations</c>) on the real SQLite schema: the
/// round trip, a restart that restores them, the database key that refuses an outpoint in two reservations, release and
/// confirmation.
/// </summary>
public class FeeInputReservationPersistenceTests
{
    private static readonly LightningMoney s_feeRate = LightningMoney.Satoshis(2_500);

    [Fact]
    public async Task Given_AReservation_When_Reloaded_Then_EveryFieldRoundTrips()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var node = await WalletNode.CreateAsync(database, 60_000, 25_000);

        // Act
        var reservation = await node.Selector.ReserveAsync(LightningMoney.Satoshis(70_000), s_feeRate, 700,
                                                           "cpfp:round-trip", TestContext.Current.CancellationToken);

        // Assert
        await using var context = database.CreateContext();
        var repository = new FeeInputReservationDbRepository(context);
        var reloaded = await repository.GetByIdAsync(reservation.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("cpfp:round-trip", reloaded.Purpose);
        Assert.Equal(reservation.Inputs, reloaded.Inputs);
        Assert.Equal(85_000, reloaded.Total.Satoshi);
        Assert.Equal(reservation.Fee.Satoshi, reloaded.Fee.Satoshi);
        Assert.Equal(reservation.ChangeAmount.Satoshi, reloaded.ChangeAmount.Satoshi);
        Assert.Equal(reservation.ChangeScript, reloaded.ChangeScript);
        Assert.Equal(reservation.Id, Assert.Single(await repository.GetAllAsync()).Id);
        Assert.Equal(2, (await repository.GetReservedOutpointsAsync()).Count);
    }

    [Fact]
    public async Task Given_AReservationWithoutChange_When_Reloaded_Then_ItHasNoChangeScript()
    {
        // Arrange: 4,000 sat pays 1,000 + 2,430 without change, but with a change output only 260 sat (< dust) is left
        using var database = new SqliteTestDatabase();
        var node = await WalletNode.CreateAsync(database, 4_000);

        // Act
        var reservation = await node.Selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 700,
                                                           "cpfp:no-change", TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(reservation.ChangeScript);
        await using var context = database.CreateContext();
        var reloaded = await new FeeInputReservationDbRepository(context).GetByIdAsync(reservation.Id);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded.ChangeScript);
        Assert.Equal(0, reloaded.ChangeAmount.Satoshi);
        Assert.Equal(4_000, reloaded.Fee.Satoshi);
    }

    [Fact]
    public async Task Given_AReservation_When_TheNodeRestarts_Then_ItsOutputsStayReserved()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var before = await WalletNode.CreateAsync(database, 60_000, 25_000);
        var reservation = await before.Selector.ReserveAsync(LightningMoney.Satoshis(10_000), s_feeRate, 700,
                                                             "cpfp:restart", TestContext.Current.CancellationToken);
        var reserved = Assert.Single(reservation.Inputs);

        // Act: a new process loads the UTXO set and the reservations as the chain monitor does at startup
        var after = await WalletNode.RestartAsync(database);

        // Assert: neither a fee reservation nor a channel funding picks the reserved output
        Assert.True(after.Utxos.TryGetFeeReservation(reserved.TxId, reserved.Index, out var id));
        Assert.Equal(reservation.Id, id);
        var second = await after.Selector.ReserveAsync(LightningMoney.Satoshis(10_000), s_feeRate, 700,
                                                       "cpfp:second", TestContext.Current.CancellationToken);
        Assert.DoesNotContain(second.Inputs, i => i.TxId.Equals(reserved.TxId) && i.Index == reserved.Index);
        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => after.Selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 700, "cpfp:third",
                                              TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(
            () => after.Utxos.LockUtxosToSpendOnChannel(LightningMoney.Satoshis(1_000), ChannelId.Zero));

        // And a release after the restart frees it
        await after.Selector.ReleaseAsync(reservation.Id, TestContext.Current.CancellationToken);
        Assert.False(after.Utxos.TryGetFeeReservation(reserved.TxId, reserved.Index, out _));
        var again = await after.Selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 700, "cpfp:again",
                                                      TestContext.Current.CancellationToken);
        Assert.Equal(reserved.TxId, Assert.Single(again.Inputs).TxId);
    }

    [Fact]
    public async Task Given_AnOutpointAlreadyReservedInTheDatabase_When_AStaleNodeReservesIt_Then_TheSaveFailsAndMemoryIsFree()
    {
        // Arrange: two nodes on one database, the second loaded before the first reserved (the key is the last guard)
        using var database = new SqliteTestDatabase();
        var first = await WalletNode.CreateAsync(database, 60_000);
        var stale = await WalletNode.RestartAsync(database);
        var reservation = await first.Selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 700,
                                                            "cpfp:first", TestContext.Current.CancellationToken);
        var input = Assert.Single(reservation.Inputs);

        // Act / Assert
        await Assert.ThrowsAsync<DbUpdateException>(
            () => stale.Selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 700, "cpfp:stale",
                                              TestContext.Current.CancellationToken));
        Assert.False(stale.Utxos.TryGetFeeReservation(input.TxId, input.Index, out _));
        await using var context = database.CreateContext();
        Assert.Equal(reservation.Id,
                     Assert.Single(await new FeeInputReservationDbRepository(context).GetAllAsync()).Id);
    }

    [Fact]
    public async Task Given_ConcurrentReservations_When_Reserving_Then_EachOutputIsInOneReservation()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var node = await WalletNode.CreateAsync(database, Enumerable.Range(1, 8).Select(i => 20_000L + i).ToArray());

        // Act
        var reservations = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(
            () => node.Selector.ReserveAsync(LightningMoney.Satoshis(1_000), s_feeRate, 700, $"cpfp:{i}",
                                             TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken)));

        // Assert
        var outpoints = reservations.SelectMany(r => r.Inputs).Select(i => (i.TxId, i.Index)).ToList();
        Assert.Equal(8, outpoints.Distinct().Count());
        await using var context = database.CreateContext();
        Assert.Equal(8, (await new FeeInputReservationDbRepository(context).GetReservedOutpointsAsync()).Count);
    }

    [Fact]
    public async Task Given_AReservation_When_Confirmed_Then_ItsRowsAndItsUtxosAreGone()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var node = await WalletNode.CreateAsync(database, 60_000, 25_000);
        var reservation = await node.Selector.ReserveAsync(LightningMoney.Satoshis(70_000), s_feeRate, 700,
                                                           "cpfp:confirm", TestContext.Current.CancellationToken);

        // Act
        await node.Selector.ConfirmAsync(reservation.Id, TestContext.Current.CancellationToken);

        // Assert
        await using var context = database.CreateContext();
        Assert.Empty(await context.FeeInputReservations.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await context.FeeInputReservationInputs.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await context.Utxos.ToListAsync(TestContext.Current.CancellationToken));
        foreach (var input in reservation.Inputs)
        {
            Assert.False(node.Utxos.TryGetUtxo(input.TxId, input.Index, out _));
            Assert.False(node.Utxos.TryGetFeeReservation(input.TxId, input.Index, out _));
        }
    }

    /// <summary>One node's wallet: its UTXO memory set, a selector and the services it resolves per scope.</summary>
    private sealed class WalletNode
    {
        public required UtxoMemoryRepository Utxos { get; init; }
        public required FeeInputSelector Selector { get; init; }

        public static async Task<WalletNode> CreateAsync(SqliteTestDatabase database, params long[] amounts)
        {
            var node = Create(database);
            using var uow = CreateUnitOfWork(database, node.Utxos);
            for (var i = 0; i < amounts.Length; i++)
            {
                var key = new Key();
                var address = new WalletAddressModel(AddressType.P2Wpkh, (uint)i, false,
                                                     key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest)
                                                        .ToString());
                uow.WalletAddressesDbRepository.AddRange([address]);
                uow.AddUtxo(new UtxoModel(new TxId(RandomUtils.GetBytes(32)), (uint)i,
                                          LightningMoney.Satoshis(amounts[i]), 100, address));
            }

            await uow.SaveChangesAsync();
            return node;
        }

        public static async Task<WalletNode> RestartAsync(SqliteTestDatabase database)
        {
            var node = Create(database);
            using var uow = CreateUnitOfWork(database, node.Utxos);
            node.Utxos.Load((await uow.UtxoDbRepository.GetUnspentAsync(includeWalletAddress: true)).ToList());
            node.Utxos.LoadFeeReservations(await uow.FeeInputReservationDbRepository.GetReservedOutpointsAsync());
            return node;
        }

        private static WalletNode Create(SqliteTestDatabase database)
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

            var nodeOptions = new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest };
            var selector = new FeeInputSelector(utxos, provider.GetRequiredService<IServiceScopeFactory>(),
                                                Microsoft.Extensions.Options.Options.Create(nodeOptions),
                                                NullLogger<FeeInputSelector>.Instance);
            return new WalletNode { Utxos = utxos, Selector = selector };
        }

        private static UnitOfWork CreateUnitOfWork(SqliteTestDatabase database, UtxoMemoryRepository utxos) =>
            new(database.CreateContext(), new Mock<ILogger<UnitOfWork>>().Object, new Mock<ISha256>().Object, utxos);
    }
}