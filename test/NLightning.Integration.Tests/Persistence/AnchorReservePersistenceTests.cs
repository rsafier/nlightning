using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Outputs;
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
/// spend) over the real <see cref="UtxoMemoryRepository"/> and the SQLite schema: the reserve math against the wallet
/// (the funding transaction's fee included), refusals as opener and fundee, a funding that cannot dip below the reserve,
/// a fee bump that may, pending opens counted, and the restart case.
/// </summary>
/// <remarks>
/// Every channel here has a feerate of 253 sat/kw: a worst-case funding fee (P2WPKH inputs, P2TR change) of 166 sat
/// with one input and 235 sat with two.
/// </remarks>
public class AnchorReservePersistenceTests
{
    private const uint Height = 200;
    private const long OneInputFeeSat = 166;
    private const long TwoInputFeeSat = 235;
    private static readonly LightningMoney s_feeRate = LightningMoney.Satoshis(2_500);

    [Fact]
    public void Given_TheTestFeerate_When_TheWorstCaseFundingFeeIsEstimated_Then_ItMatchesTheConstantsTheTestsUse()
    {
        // Arrange
        var feeRate = CreateChannel(true, ChannelState.V1Opening).ChannelParams.FeeRateAmountPerKw;

        // Act / Assert
        Assert.Equal(OneInputFeeSat, FundingFeeEstimator.EstimateWorstCaseFee(1, feeRate).Satoshi);
        Assert.Equal(TwoInputFeeSat, FundingFeeEstimator.EstimateWorstCaseFee(2, feeRate).Satoshi);
    }

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
        var channel = NewChannel(true);

        // Act / Assert: 70,000 plus its two-input fee leaves 14,765 < 20,000
        await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.EnsureCanFundAsync(LightningMoney.Satoshis(70_000), channel,
                                                  TestContext.Current.CancellationToken));
        var exception = await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(70_000), channel,
                                                     TestContext.Current.CancellationToken));
        Assert.Equal(20_000, exception.RequiredReserve.Satoshi);
        Assert.Empty(node.Utxos.GetLockedUtxosForChannel(channel.ChannelId));
        Assert.Equal(2, node.Utxos.GetUnreservedUtxos().Count);

        // A funding that keeps the reserve goes through: 59,000 from the 60,000 output leaves 25,000 and its change
        await node.Reserve.EnsureCanFundAsync(LightningMoney.Satoshis(59_000), channel,
                                              TestContext.Current.CancellationToken);
        var locked = await node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(59_000), channel,
                                                              TestContext.Current.CancellationToken);
        Assert.Equal(60_000, Assert.Single(locked).Amount.Satoshi);
    }

    [Fact]
    public async Task Given_AFundingLeavingExactlyTheReserveBeforeItsFee_When_Checked_Then_TheFeeMakesItRefused()
    {
        // Arrange: one anchors channel (10,000 sat reserve), a legacy funding; 85,000 - 75,000 = 10,000 exactly, but
        // the funding transaction pays its fee from the wallet too
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [(true, ChannelState.Open)], 60_000, 25_000);
        var channel = NewChannel(false);

        // Act / Assert
        await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.EnsureCanFundAsync(LightningMoney.Satoshis(75_000), channel,
                                                  TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(75_000), channel,
                                                     TestContext.Current.CancellationToken));
        Assert.Empty(node.Utxos.GetLockedUtxosForChannel(channel.ChannelId));
    }

    [Fact]
    public async Task Given_ALegacyChannelFunding_When_Locked_Then_ItKeepsTheReserveAfterItsFee()
    {
        // Arrange: one anchors channel (10,000 sat reserve); the new channel has no anchors
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [(true, ChannelState.Open)], 60_000, 25_000);
        const long mostSat = 85_000 - TwoInputFeeSat - 10_000;

        // Act / Assert: the change after the two-input fee must be at least 10,000
        await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(mostSat + 1), NewChannel(false),
                                                     TestContext.Current.CancellationToken));
        var channel = NewChannel(false);
        await node.Reserve.EnsureCanFundAsync(LightningMoney.Satoshis(mostSat), channel,
                                              TestContext.Current.CancellationToken);
        var locked = await node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(mostSat), channel,
                                                              TestContext.Current.CancellationToken);
        Assert.Equal(85_000, locked.Sum(u => u.Amount.Satoshi));

        // The factory that builds the funding transaction leaves exactly the reserve as change
        var fundingKey = channel.LocalKeySet.FundingCompactPubKey;
        channel.AddFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(mostSat), fundingKey, fundingKey));
        var change = new WalletAddressModel(AddressType.P2Tr, 1, true, "change");
        var funding = new FundingTransactionModelFactory().Create(channel, locked, change);
        Assert.True(funding.ChangeAmount!.Satoshi >= 10_000);
    }

    [Fact]
    public async Task Given_NoAnchorsChannels_When_AFundingTakesTheWholeWallet_Then_NoReserveIsKeptButTheFeeIs()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [], 60_000, 25_000);

        // Act / Assert: the whole wallet cannot also pay the fee; the wallet minus the fee can
        Assert.Throws<InvalidOperationException>(
            () => node.Utxos.LockUtxosToSpendOnChannel(LightningMoney.Satoshis(85_000), NewChannelId(),
                                                       LightningMoney.Zero, new HashSet<(TxId, uint)>(),
                                                       LightningMoney.Satoshis(253), Height));
        var locked = await node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(85_000 - TwoInputFeeSat),
                                                              NewChannel(false),
                                                              TestContext.Current.CancellationToken);
        Assert.Equal(2, locked.Count);
    }

    [Fact]
    public async Task Given_OnlyUnconfirmedOutputsLeft_When_AFundingIsLocked_Then_TheyDoNotBackTheReserve()
    {
        // Arrange: one anchors channel (10,000 sat reserve), a confirmed 60,000 output and a 30,000 one mined a block
        // ago (below the three-block rule, so the CPFP fee selector's reserve can't count on it yet)
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [(true, ChannelState.Open)], 60_000);
        await node.AddUtxoAsync(database, 30_000, Height - 1);

        // Act: 55,000 from the 60,000 output leaves only the unconfirmed 30,000 and 4,834 sat of change
        var exception = await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(55_000), NewChannel(false),
                                                     TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(10_000, exception.RequiredReserve.Satoshi);
        Assert.Equal(60_000, exception.Available.Satoshi);
        Assert.All(node.Utxos.GetUnreservedUtxos(), u => Assert.Null(u.LockedToChannelId));
    }

    [Fact]
    public async Task Given_AFundingThatLeftOnlyTheReserve_When_AFeeBumpReserves_Then_ItMaySpendTheReserve()
    {
        // Arrange: the funding keeps 25,000 sat (>= the 20,000 sat reserve) out of reach of fundings
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [(true, ChannelState.Open)], 60_000, 25_000);
        await node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(59_000), NewChannel(true),
                                                 TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(16_000), NewChannel(false),
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
            () => node.Reserve.EnsureCanAcceptAnchorsChannelAsync(NewChannel(true),
                                                                  TestContext.Current.CancellationToken));
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
        await node.Reserve.EnsureCanAcceptAnchorsChannelAsync(NewChannel(true), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_ConcurrentAnchorsOpensFromPeers_When_Accepted_Then_OnlyAsManyAsTheWalletBacksPass()
    {
        // Arrange: no channels yet and 15,000 sat: one anchors channel's reserve, not two
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [], 15_000);

        // Act: two open_channel messages (distinct temporary ids) checked at once
        var results = await Task.WhenAll(
            TryAsync(() => node.Reserve.EnsureCanAcceptAnchorsChannelAsync(NewChannel(true),
                                                                           TestContext.Current.CancellationToken)),
            TryAsync(() => node.Reserve.EnsureCanAcceptAnchorsChannelAsync(NewChannel(true),
                                                                           TestContext.Current.CancellationToken)));

        // Assert: the second one sees the first counted
        Assert.Single(results, r => r is null);
        Assert.IsType<AnchorReserveException>(Assert.Single(results, r => r is not null));
        Assert.Equal(1, node.Reserve.CountAnchorsChannels());
    }

    [Fact]
    public async Task Given_TwoAnchorsOpensAsOpener_When_BothAreFunded_Then_TheSecondMustKeepTheReserveOfBoth()
    {
        // Arrange: no channels, 50,000 + 50,000 + 5,000 sat. Two 40,000 sat anchors fundings each fit alone (the
        // remaining 50,000 + 5,000 + change cover one channel's reserve), but the second one leaves 5,000 + 9,834 sat,
        // enough for one channel's 10,000 sat reserve and not for the 20,000 of both
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [], 50_000, 50_000, 5_000);
        var first = NewChannel(true);
        var second = NewChannel(true);
        var amount = LightningMoney.Satoshis(40_000);

        // Act: both admitted before either locks its inputs (two concurrent openchannel calls)
        await node.Reserve.EnsureCanFundAsync(amount, first, TestContext.Current.CancellationToken);
        await node.Reserve.EnsureCanFundAsync(amount, second, TestContext.Current.CancellationToken);
        await node.Reserve.LockFundingUtxosAsync(amount, first, TestContext.Current.CancellationToken);

        // Assert
        var exception = await Assert.ThrowsAsync<AnchorReserveException>(
            () => node.Reserve.LockFundingUtxosAsync(amount, second, TestContext.Current.CancellationToken));
        Assert.Equal(20_000, exception.RequiredReserve.Satoshi);
        Assert.Empty(node.Utxos.GetLockedUtxosForChannel(second.ChannelId));

        // Once the second open is given up, a third one only has to keep the first channel's reserve again
        node.Reserve.ReleasePendingChannel(second.ChannelId);
        Assert.Equal(1, node.Reserve.CountAnchorsChannels());
    }

    [Fact]
    public async Task Given_APendingOpen_When_TimePasses_Then_ItCountsOnlyWhileStoredAndUntilTheTimeout()
    {
        // Arrange
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [], 100_000);
        var stored = NewChannel(true);
        var neverStored = NewChannel(true);
        await node.Reserve.EnsureCanAcceptAnchorsChannelAsync(stored, TestContext.Current.CancellationToken);
        await node.Reserve.EnsureCanAcceptAnchorsChannelAsync(neverStored, TestContext.Current.CancellationToken);
        node.StoredTemporaryChannels.Add(stored.ChannelId);

        // Act / Assert: both count at first
        Assert.Equal(2, node.Reserve.CountAnchorsChannels());

        // Past the store grace only the stored temporary channel counts
        node.Clock.Now += AnchorReserveService.StoreGrace;
        Assert.Equal(1, node.Reserve.CountAnchorsChannels());
        Assert.Equal(10_000, node.Reserve.GetRequiredReserve().Satoshi);

        // Past the pending open timeout an abandoned open stops holding reserve
        node.Clock.Now += new AnchorReserveOptions().PendingOpenTimeout;
        Assert.Equal(0, node.Reserve.CountAnchorsChannels());
    }

    [Fact]
    public async Task Given_AnAdmittedOpenThatIsFunded_When_Counted_Then_ItCountsOnceAsAChannel()
    {
        // Arrange: the fundee admitted an anchors open and stored its temporary channel
        using var database = new SqliteTestDatabase();
        var node = await ReserveNode.CreateAsync(database, [], 100_000);
        var channel = NewChannel(true);
        await node.Reserve.EnsureCanAcceptAnchorsChannelAsync(channel, TestContext.Current.CancellationToken);
        node.StoredTemporaryChannels.Add(channel.ChannelId);
        Assert.Equal(1, node.Reserve.CountAnchorsChannels());

        // Act: funding_created gives the same model its real id and stores it as a channel, within the store grace
        node.StoredTemporaryChannels.Remove(channel.ChannelId);
        channel.UpdateChannelId(NewChannelId());
        node.Channels.Add(channel);

        // Assert: not counted twice
        Assert.Equal(1, node.Reserve.CountAnchorsChannels());
        Assert.Equal(10_000, node.Reserve.GetRequiredReserve().Satoshi);
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
            () => node.Reserve.EnsureCanAcceptAnchorsChannelAsync(NewChannel(true),
                                                                  TestContext.Current.CancellationToken));
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
        var locked = await after.Reserve.LockFundingUtxosAsync(LightningMoney.Satoshis(30_000), NewChannel(false),
                                                               TestContext.Current.CancellationToken);

        // Assert: the smaller output funds it, the pending broadcast's input is neither locked nor counted
        var input = Assert.Single(locked);
        Assert.Equal(40_000, input.Amount.Satoshi);
        Assert.Null(after.Utxos.GetUnreservedUtxos().Single(u => u.TxId.Equals(largest.TxId)).LockedToChannelId);
        Assert.True((await after.Reserve.GetStatusAsync(TestContext.Current.CancellationToken)).AvailableBalance.IsZero);
        Assert.Throws<InvalidOperationException>(
            () => after.Utxos.LockUtxosToSpendOnChannel(LightningMoney.Satoshis(100_000), NewChannelId(),
                                                        LightningMoney.Zero,
                                                        new HashSet<(TxId, uint)> { (largest.TxId, largest.Index) },
                                                        LightningMoney.Satoshis(253), Height));
    }

    private static ChannelId NewChannelId() => new(RandomUtils.GetBytes(32));

    private static ChannelModel NewChannel(bool anchors) => CreateChannel(anchors, ChannelState.V1Opening);

    private static async Task<Exception?> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
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

    private delegate bool TryGetChannel(ChannelId channelId, out ChannelModel? channel);

    private delegate bool TryGetTemporaryChannelState(CompactPubKey peer, ChannelId channelId, out ChannelState state);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A wallet (UTXO memory set on SQLite), its channels and the reserve service over them.</summary>
    private sealed class ReserveNode
    {
        public required UtxoMemoryRepository Utxos { get; init; }
        public required AnchorReserveService Reserve { get; init; }
        public required FeeInputSelector Selector { get; init; }
        public required ManualTimeProvider Clock { get; init; }
        public required HashSet<ChannelId> StoredTemporaryChannels { get; init; }
        public required List<ChannelModel> Channels { get; init; }

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
            var stored = new HashSet<ChannelId>();
            var channelMemory = new Mock<IChannelMemoryRepository>();
            channelMemory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                         .Returns((Func<ChannelModel, bool> predicate) => models.Where(predicate).ToList());
            channelMemory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
                         .Returns(new TryGetChannel((ChannelId id, out ChannelModel? channel) =>
                                                    {
                                                        channel = models.FirstOrDefault(c => c.ChannelId.Equals(id));
                                                        return channel is not null;
                                                    }));
            channelMemory.Setup(m => m.TryGetTemporaryChannelState(It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                                                   out It.Ref<ChannelState>.IsAny))
                         .Returns(new TryGetTemporaryChannelState((CompactPubKey _, ChannelId id,
                                                                   out ChannelState state) =>
                                                                  {
                                                                      state = ChannelState.V1Opening;
                                                                      return stored.Contains(id);
                                                                  }));
            var monitor = new Mock<IBlockchainMonitor>();
            monitor.Setup(m => m.LastProcessedBlockHeight).Returns(Height);

            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
            var nodeOptions = Microsoft.Extensions.Options.Options.Create(
                new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest });
            return new ReserveNode
            {
                Utxos = utxos,
                Reserve = new AnchorReserveService(utxos, channelMemory.Object, monitor.Object, scopeFactory,
                                                   nodeOptions, NullLogger<AnchorReserveService>.Instance, clock),
                Selector = new FeeInputSelector(utxos, scopeFactory, nodeOptions,
                                                NullLogger<FeeInputSelector>.Instance),
                Clock = clock,
                StoredTemporaryChannels = stored,
                Channels = models
            };
        }

        private static UnitOfWork CreateUnitOfWork(SqliteTestDatabase database, UtxoMemoryRepository utxos) =>
            new(database.CreateContext(), new Mock<ILogger<UnitOfWork>>().Object, new Mock<ISha256>().Object, utxos);
    }
}