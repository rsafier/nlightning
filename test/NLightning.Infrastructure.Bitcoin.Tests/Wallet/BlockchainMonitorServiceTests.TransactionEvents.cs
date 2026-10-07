using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;

public partial class BlockchainMonitorServiceTests
{
    [Fact]
    public async Task Given_AWalletDeposit_When_MempoolThenConfirmed_Then_OneLiveDiscoveryAndACommittedConfirmation()
    {
        await _service.StartAsync(0, TestContext.Current.CancellationToken);
        var transaction = WatchTransactionDeposit();
        var observations = new List<WalletTransactionEventArgs>();
        _service.OnWalletTransactionObserved += (_, observed) =>
        {
            if (observed.BlockHeight > 0)
                Assert.Equal("save", _steps.Last());
            observations.Add(observed);
        };

        _service.ProcessMempoolTransaction(transaction);
        _service.ProcessMempoolTransaction(transaction);
        await _service.ProcessNewBlockAsync(_chain.Mine(transaction), 111);
        await _service.StopAsync();

        Assert.Equal([0u, 111u], observations.Select(o => o.BlockHeight));
        Assert.All(observations, o => Assert.Equal(40_000, o.AmountSat));
        Assert.All(observations, o => Assert.Equal(transaction.GetHash().ToString(), o.TxHash));
        Assert.All(observations, o => Assert.Equal([0u], o.OurOutputs));
        Assert.Empty(observations[0].BlockHash);
        Assert.Equal(_chain[111].GetHash().ToString(), observations[1].BlockHash);
        Assert.Equal(transaction.ToHex(), observations[1].RawTransactionHex);
    }

    [Fact]
    public async Task Given_AnAcceptedWalletBroadcast_When_ZmqAlsoArrives_Then_OnlyOneUnconfirmedObservation()
    {
        var transaction = WatchTransactionDeposit();
        var observations = new List<WalletTransactionEventArgs>();
        _service.OnWalletTransactionObserved += (_, observed) => observations.Add(observed);
        var row = new BroadcastTransactionModel(ToSigned(transaction), BroadcastPurpose.WalletSend, null, 1, 0)
        { Label = "external wallet" };

        Assert.True(await _service.SaveAndPublishAsync(row));
        _service.ProcessMempoolTransaction(transaction);

        var observed = Assert.Single(observations);
        Assert.Equal("external wallet", observed.Label);
        Assert.Equal(0u, observed.BlockHeight);
        _mockUnitOfWork.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Given_AWalletDeposit_When_TheBlockSaveFails_Then_NoCommittedObservationEscapes()
    {
        await _service.StartAsync(0, TestContext.Current.CancellationToken);
        var transaction = WatchTransactionDeposit();
        var observations = new List<WalletTransactionEventArgs>();
        _service.OnWalletTransactionObserved += (_, observed) => observations.Add(observed);
        _mockUnitOfWork.Setup(x => x.SaveChangesAsync()).ThrowsAsync(new InvalidOperationException("db down"));
        _service.MaxBlockProcessingAttempts = 1;

        await _service.ProcessNewBlockAsync(_chain.Mine(transaction), 111);
        await _service.StopAsync();

        Assert.Empty(observations);
        Assert.True(_service.IsChainProcessingHalted);
    }

    [Fact]
    public async Task Given_AConfirmedWalletDeposit_When_ReorgedAndReconfirmed_Then_StreamUnconfirmsAfterRewindSave()
    {
        await _service.StartAsync(0, TestContext.Current.CancellationToken);
        var transaction = WatchTransactionDeposit();
        var observations = new List<WalletTransactionEventArgs>();
        _service.OnWalletTransactionObserved += (_, observed) => observations.Add(observed);
        await _service.ProcessNewBlockAsync(_chain.Mine(transaction), 111);
        _mockWatchedTransactionRepository.Setup(x => x.GetCompletedFirstSeenAboveAsync(It.IsAny<uint>()))
                                         .ReturnsAsync([]);
        var replacement = _chain.Reorg(110, 1, transaction);

        await _service.ProcessNewBlockAsync(replacement[0], 111);
        await _service.StopAsync();

        Assert.Equal([111u, 0u, 111u], observations.Select(o => o.BlockHeight));
        Assert.Equal(observations[0].AmountSat, observations[1].AmountSat);
        Assert.Empty(observations[1].BlockHash);
        Assert.NotEqual(observations[0].BlockHash, observations[2].BlockHash);
    }

    [Fact]
    public void Given_AThrowingWalletSubscriber_When_AWalletTransactionArrives_Then_OtherSubscribersStillReceiveIt()
    {
        var transaction = WatchTransactionDeposit();
        var received = 0;
        _service.OnWalletTransactionObserved += (_, _) => throw new InvalidOperationException("consumer down");
        _service.OnWalletTransactionObserved += (_, _) => received++;

        _service.ProcessMempoolTransaction(transaction);

        Assert.Equal(1, received);
    }

    [Fact]
    public async Task Given_AWalletSendWithChange_When_Confirmed_Then_NetAmountIncludesFeeAndInputOwnershipIsRetained()
    {
        await _service.StartAsync(0, TestContext.Current.CancellationToken);
        var deposit = WatchTransactionDeposit();
        var walletAddress = new WalletAddressModel(AddressType.P2Wpkh, 0, false,
            deposit.Outputs[0].ScriptPubKey.GetDestinationAddress(Network.RegTest)!.ToString());
        var known = new UtxoModel(new TxId(deposit.GetHash().ToBytes()), 0,
                                 LightningMoney.Satoshis(40_000), 100, walletAddress);
        var utxos = Mock.Get((IUtxoMemoryRepository)_fakeServiceProvider.GetService(typeof(IUtxoMemoryRepository))!);
        utxos.Setup(x => x.TryGetUtxo(known.TxId, 0, out known)).Returns(true);
        var send = Network.RegTest.CreateTransaction();
        send.Inputs.Add(new OutPoint(deposit.GetHash(), 0));
        send.Outputs.Add(Money.Satoshis(30_000), new Key().PubKey.WitHash.ScriptPubKey);
        send.Outputs.Add(Money.Satoshis(9_000), deposit.Outputs[0].ScriptPubKey);
        var observations = new List<WalletTransactionEventArgs>();
        _service.OnWalletTransactionObserved += (_, observed) => observations.Add(observed);

        await _service.ProcessNewBlockAsync(_chain.Mine(send), 111);
        await _service.StopAsync();

        var observed = Assert.Single(observations);
        Assert.Equal(-31_000, observed.AmountSat);
        Assert.Equal(1_000, observed.FeeSat);
        Assert.Equal([1u], observed.OurOutputs);
        Assert.Equal([0u], observed.OurInputs);
    }

    [Fact]
    public async Task Given_ACommittedWalletRewind_When_TheFollowingTipRpcFails_Then_TheUnconfirmationAlreadyEscaped()
    {
        var chain = TransactionEventChain();
        var service = CreateService(chain.Object);
        await service.StartAsync(0, TestContext.Current.CancellationToken);
        var transaction = WatchTransactionDeposit();
        // Watch the same address on the alternate monitor using the same wallet fixture.
        service.WatchBitcoinAddress(new WalletAddressModel(AddressType.P2Wpkh, 0, false,
            transaction.Outputs[0].ScriptPubKey.GetDestinationAddress(Network.RegTest)!.ToString()));
        var observations = new List<WalletTransactionEventArgs>();
        service.OnWalletTransactionObserved += (_, observed) => observations.Add(observed);
        await service.ProcessNewBlockAsync(_chain.Mine(transaction), 111);
        _mockWatchedTransactionRepository.Setup(x => x.GetCompletedFirstSeenAboveAsync(It.IsAny<uint>()))
                                         .ReturnsAsync([]);
        _mockUnitOfWork.Setup(x => x.SaveChangesAsync()).Callback(() =>
            chain.Setup(x => x.GetCurrentBlockHeightAsync()).ThrowsAsync(new InvalidOperationException("tip RPC down")))
            .Returns(Task.CompletedTask);

        await service.ProcessNewBlockAsync(_chain.Reorg(110, 1)[0], 111);
        await service.StopAsync();

        Assert.Equal([111u, 0u], observations.Select(o => o.BlockHeight));
        Assert.Equal(110u, service.LastProcessedBlockHeight);
        Assert.True(service.IsChainProcessingHalted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ACachedWalletConfirmation_When_OldBlocksAreUnavailableOrSubscriberJoinsDuringRewind_Then_CacheStillUnconfirms(
        bool joinDuringSave)
    {
        var chain = TransactionEventChain();
        var service = CreateService(chain.Object);
        await service.StartAsync(0, TestContext.Current.CancellationToken);
        var transaction = WatchTransactionDeposit();
        service.WatchBitcoinAddress(new WalletAddressModel(AddressType.P2Wpkh, 0, false,
            transaction.Outputs[0].ScriptPubKey.GetDestinationAddress(Network.RegTest)!.ToString()));
        var observations = new List<WalletTransactionEventArgs>();
        EventHandler<WalletTransactionEventArgs> observed = (_, e) => observations.Add(e);
        if (!joinDuringSave)
            service.OnWalletTransactionObserved += observed;
        await service.ProcessNewBlockAsync(_chain.Mine(transaction), 111);
        _mockWatchedTransactionRepository.Setup(x => x.GetCompletedFirstSeenAboveAsync(It.IsAny<uint>()))
                                         .ReturnsAsync([]);
        chain.Setup(x => x.GetBlockAsync(It.IsAny<uint256>())).ThrowsAsync(new InvalidOperationException("pruned"));
        if (joinDuringSave)
            _mockUnitOfWork.Setup(x => x.SaveChangesAsync()).Callback(() =>
                service.OnWalletTransactionObserved += observed).Returns(Task.CompletedTask);

        await service.ProcessNewBlockAsync(_chain.Reorg(110, 1)[0], 111);
        await service.StopAsync();

        var unconfirmed = Assert.Single(observations, o => o.BlockHeight == 0);
        Assert.Equal(40_000, unconfirmed.AmountSat);
        Assert.Equal(transaction.ToHex(), unconfirmed.RawTransactionHex);
        Assert.Equal([0u], unconfirmed.OurOutputs);
    }

    [Fact]
    public async Task Given_AZeroInputSyntheticWalletTransaction_When_Processed_Then_CachingNeverReparsesAfterCommit()
    {
        await _service.StartAsync(0, TestContext.Current.CancellationToken);
        var transaction = WatchTransactionDeposit();
        transaction.Inputs.Clear();
        var observations = new List<WalletTransactionEventArgs>();
        _service.OnWalletTransactionObserved += (_, observed) => observations.Add(observed);

        await _service.ProcessNewBlockAsync(_chain.Mine(transaction), 111);
        await _service.ProcessNewBlockAsync(_chain.Mine(), 112);
        await _service.StopAsync();

        Assert.False(_service.IsChainProcessingHalted, _service.ChainProcessingHaltReason);
        Assert.Equal(112u, _service.LastProcessedBlockHeight);
        Assert.Equal(transaction.GetHash().ToString(), Assert.Single(observations).TxHash);
    }

    private Mock<IBitcoinChainService> TransactionEventChain()
    {
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(x => x.GetCurrentBlockHeightAsync()).Returns(() => _chain.GetCurrentBlockHeightAsync());
        chain.Setup(x => x.GetBlockAsync(It.IsAny<uint>())).Returns<uint>(h => _chain.GetBlockAsync(h));
        chain.Setup(x => x.GetBlockAsync(It.IsAny<uint256>())).Returns<uint256>(h => _chain.GetBlockAsync(h));
        chain.Setup(x => x.GetBlockHashAsync(It.IsAny<uint>())).Returns<uint>(h => _chain.GetBlockHashAsync(h));
        chain.Setup(x => x.GetTransactionAsync(It.IsAny<uint256>())).Returns<uint256>(h => _chain.GetTransactionAsync(h));
        return chain;
    }

    private Transaction WatchTransactionDeposit()
    {
        var address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        _service.WatchBitcoinAddress(new WalletAddressModel(AddressType.P2Wpkh, 0, false, address.ToString()));
        _fakeServiceProvider.AddService(typeof(IUtxoMemoryRepository), new Mock<IUtxoMemoryRepository>().Object);
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(1), 0));
        transaction.Outputs.Add(Money.Satoshis(40_000), address);
        return transaction;
    }
}