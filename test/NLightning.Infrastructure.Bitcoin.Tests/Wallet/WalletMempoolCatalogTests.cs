using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Bitcoin.Wallet.Models;

public class WalletMempoolCatalogTests
{
    [Fact]
    public async Task Given_MempoolCustodyAndDurableLease_When_ParentEvictsRestartsAndConfirms_Then_TransientOwnershipNeverBecomesConfirmedByGuess()
    {
        // Arrange: public ownership catalogue, Core-proven unconfirmed parent and a durable lease identity.
        using var key = new Key();
        var address = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var owner = new WalletAddressModel(AddressType.P2Wpkh, 0, false, address.ToString());
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(RandomUtils.GetUInt256(), 1)));
        tx.Outputs.Add(Money.Satoshis(50_000), address);
        var id = tx.GetHash();
        var txid = new TxId(id.ToBytes());
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.WalletAddressesDbRepository.GetAllAddresses()).Returns([owner]);
        uow.Setup(u => u.BroadcastTransactionDbRepository.GetPendingAsync()).ReturnsAsync([]);
        uow.Setup(u => u.FeeInputReservationDbRepository.GetAllAsync()).ReturnsAsync([]);
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetMempoolTransactionIdsAsync()).ReturnsAsync([id]);
        chain.Setup(c => c.GetTransactionAsync(id)).ReturnsAsync(tx);
        chain.Setup(c => c.GetMempoolEntryAsync(id)).ReturnsAsync(new WalletMempoolEntry(100, 100, 100, 100, 1));
        chain.Setup(c => c.GetMempoolUnspentOutputAsync(new OutPoint(id, 0))).ReturnsAsync(tx.Outputs[0]);
        var services = new ServiceCollection().AddScoped(_ => uow.Object);
        using var provider = services.BuildServiceProvider();
        var wallet = new FakeWalletUtxoRepository();
        var source = new WalletMempoolCatalog(provider.GetRequiredService<IServiceScopeFactory>(), wallet, chain.Object,
            Microsoft.Extensions.Options.Options.Create(new NodeOptions { BitcoinNetwork = "regtest" }));
        // Act / Assert: transient entry is reservable, but ordinary selection never sees it.
        Assert.Single(await source.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Empty(wallet.GetUnreservedUtxos());
        var reservation = Guid.NewGuid();
        Assert.True(wallet.TryReserveForFee([(txid, 0)], reservation));
        chain.Setup(c => c.GetMempoolEntryAsync(id)).ReturnsAsync((WalletMempoolEntry?)null);
        Assert.Empty(await source.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.False(wallet.TryGetUtxo(txid, 0, out _));
        Assert.True(wallet.TryGetFeeReservation(txid, 0, out var retained));
        Assert.Equal(reservation, retained);
        // Restart rebuilds from RPC and persisted lease; no transient coin is loaded from the durable custody table.
        var restarted = new FakeWalletUtxoRepository();
        restarted.LoadFeeReservations([(txid, 0, reservation)]);
        chain.Setup(c => c.GetMempoolEntryAsync(id)).ReturnsAsync(new WalletMempoolEntry(100, 100, 100, 100, 1));
        var fresh = new WalletMempoolCatalog(provider.GetRequiredService<IServiceScopeFactory>(), restarted, chain.Object,
            Microsoft.Extensions.Options.Options.Create(new NodeOptions { BitcoinNetwork = "regtest" }));
        Assert.Single(await fresh.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.False(restarted.TryReserveForFee([(txid, 0)], Guid.NewGuid()));
        restarted.Add(new UtxoModel(txid, 0, LightningMoney.Satoshis(50_000), 201, owner));
        Assert.Empty(restarted.GetUnconfirmedUtxos());
        Assert.True(restarted.TryGetUtxo(txid, 0, out var confirmed));
        Assert.Equal(201u, confirmed.BlockHeight);
        Assert.Empty(await fresh.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.True(restarted.TryGetFeeReservation(txid, 0, out retained));
        Assert.Equal(reservation, retained);
    }

    [Fact]
    public async Task Given_CoreDisagreesWithParentsOutput_When_CatalogueRefreshes_Then_NoSignableCustodyIsImported()
    {
        // Arrange
        using var key = new Key();
        var address = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(RandomUtils.GetUInt256(), 0)));
        tx.Outputs.Add(Money.Satoshis(50_000), address);
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.WalletAddressesDbRepository.GetAllAddresses()).Returns([new WalletAddressModel(AddressType.P2Wpkh, 0, false, address.ToString())]);
        uow.Setup(u => u.BroadcastTransactionDbRepository.GetPendingAsync()).ReturnsAsync([]);
        uow.Setup(u => u.FeeInputReservationDbRepository.GetAllAsync()).ReturnsAsync([]);
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetMempoolTransactionIdsAsync()).ReturnsAsync([tx.GetHash()]);
        chain.Setup(c => c.GetTransactionAsync(tx.GetHash())).ReturnsAsync(tx);
        chain.Setup(c => c.GetMempoolEntryAsync(tx.GetHash())).ReturnsAsync(new WalletMempoolEntry(100, 100, 100, 100, 1));
        chain.Setup(c => c.GetMempoolUnspentOutputAsync(It.IsAny<OutPoint>())).ReturnsAsync(new TxOut(Money.Satoshis(49_000), address.ScriptPubKey));
        using var provider = new ServiceCollection().AddScoped(_ => uow.Object).BuildServiceProvider();
        var wallet = new FakeWalletUtxoRepository();
        var source = new WalletMempoolCatalog(provider.GetRequiredService<IServiceScopeFactory>(), wallet, chain.Object,
            Microsoft.Extensions.Options.Options.Create(new NodeOptions { BitcoinNetwork = "regtest" }));
        // Act / Assert
        Assert.Empty(await source.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Empty(wallet.GetUnconfirmedUtxos());
    }

    [Fact]
    public async Task Given_MoreOwnedParentsThanDiscoveryBatch_When_Retried_Then_OwnedProgressSurvivesFailClosedCustodyClear()
    {
        // Arrange: an all-owned batch exposed a starvation bug when only foreign IDs were cached.
        using var key = new Key();
        var address = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var owner = new WalletAddressModel(AddressType.P2Wpkh, 0, false, address.ToString());
        var parents = Enumerable.Range(0, WalletMempoolCatalog.DiscoveryBatchSize + 1).Select(index =>
        {
            var tx = Network.RegTest.CreateTransaction();
            tx.Inputs.Add(new TxIn(new OutPoint(new uint256((ulong)index + 1), 0)));
            tx.Outputs.Add(Money.Satoshis(50_000), address);
            return tx;
        }).ToDictionary(tx => tx.GetHash());
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.WalletAddressesDbRepository.GetAllAddresses()).Returns([owner]);
        uow.Setup(u => u.BroadcastTransactionDbRepository.GetPendingAsync()).ReturnsAsync([]);
        uow.Setup(u => u.FeeInputReservationDbRepository.GetAllAsync()).ReturnsAsync([]);
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetMempoolTransactionIdsAsync()).ReturnsAsync(parents.Keys.ToArray());
        chain.Setup(c => c.GetTransactionAsync(It.IsAny<uint256>())).ReturnsAsync((uint256 id) => parents[id]);
        chain.Setup(c => c.GetMempoolEntryAsync(It.IsAny<uint256>())).ReturnsAsync(new WalletMempoolEntry(100, 100, 100, 100, 1));
        chain.Setup(c => c.GetMempoolUnspentOutputAsync(It.IsAny<OutPoint>())).ReturnsAsync((OutPoint point) => parents[point.Hash].Outputs[(int)point.N]);
        using var provider = new ServiceCollection().AddScoped(_ => uow.Object).BuildServiceProvider();
        var wallet = new FakeWalletUtxoRepository();
        var source = new WalletMempoolCatalog(provider.GetRequiredService<IServiceScopeFactory>(), wallet, chain.Object,
            Microsoft.Extensions.Options.Options.Create(new NodeOptions { BitcoinNetwork = "regtest" }));
        // Act / Assert: initial incomplete discovery cannot sign from a partial catalogue.
        await Assert.ThrowsAsync<Domain.Exceptions.WalletPsbtException>(() => source.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Empty(wallet.GetUnconfirmedUtxos());
        // Previously this retry rediscovered the same owned batch forever. Every owned parent now survives as progress.
        var found = await source.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WalletMempoolCatalog.DiscoveryBatchSize + 1, found.Count);
        Assert.Equal(found.Count, wallet.GetUnconfirmedUtxos().Count);
        Assert.Empty(wallet.GetUnreservedUtxos());
        // Eviction prunes retained discovery progress before enforcing the validation bound.
        chain.Setup(c => c.GetMempoolTransactionIdsAsync()).ReturnsAsync([]);
        chain.Setup(c => c.GetTransactionAsync(It.IsAny<uint256>())).ReturnsAsync((Transaction?)null);
        Assert.Empty(await source.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Empty(wallet.GetUnconfirmedUtxos());
        chain.Verify(c => c.GetMempoolEntryAsync(It.IsAny<uint256>()), Times.Exactly(2_001));
    }

}