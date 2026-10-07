using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Bitcoin.Wallet.Models;

public partial class WalletPsbtServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ConfirmationDepthLease_When_SpendConfirmsAndExpiryPasses_Then_HeldUntilRequiredDepthUsingPagedHistory(bool projected)
    {
        var (coin, outpoint, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var history = new Mock<Domain.Bitcoin.Wallet.Interfaces.IWalletTransactionDbRepository>();
        _unitOfWork.Setup(u => u.WalletTransactionDbRepository).Returns(history.Object);
        var spend = Network.RegTest.CreateTransaction();
        spend.Inputs.Add(new TxIn(outpoint));
        spend.Outputs.Add(Money.Satoshis(90_000), s_destination);
        var record = new WalletTransactionRecord(new TxId(spend.GetHash().ToBytes()), spend.ToBytes(), Height,
            new byte[32], DateTimeOffset.UnixEpoch, [], [new WalletTransactionInput(0, 100_000)]);
        var retained = record;
        if (projected)
            record = WalletTransactionHistory.Describe(spend, record.BlockHeight, record.BlockHash,
                record.Timestamp, record.OurOutputs, record.OurInputs) with
            { RawTransaction = [] };
        history.Setup(h => h.GetByIdsAsync(It.IsAny<IReadOnlyCollection<TxId>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([retained]);
        history.Setup(h => h.GetHistoryPageAsync(0, It.IsAny<uint>(), false, 0, 128, It.IsAny<CancellationToken>()))
            .ReturnsAsync((uint start, uint end, bool unconfirmed, int offset, int limit, CancellationToken token) =>
                end >= Height ? new[] { record } : Array.Empty<WalletTransactionRecord>());
        await _service.LeaseAsync(s_lockId, coin.TxId, coin.Index, TimeSpan.FromMinutes(1), 3, Ct);
        Assert.EndsWith(":3", Assert.Single(_stored).Purpose);
        _utxos.Spend(coin);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Single(await _service.ListLeasesAsync(Ct));
        Assert.True(_utxos.TryGetFeeReservation(coin.TxId, coin.Index, out _));
        _monitor.Setup(m => m.LastProcessedBlockHeight).Returns(Height + 2);
        Assert.Empty(await _service.ListLeasesAsync(Ct));
        Assert.False(_utxos.TryGetFeeReservation(coin.TxId, coin.Index, out _));
        history.Verify(h => h.GetHistoryAsync(It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("unconfirmed")]
    [InlineData("wrong-txid")]
    public async Task Given_IncompleteRetainedHistory_When_SweepingLease_Then_NoConfirmedSpendIsInferred(string mutation)
    {
        var (coin, outpoint, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var history = new Mock<Domain.Bitcoin.Wallet.Interfaces.IWalletTransactionDbRepository>();
        _unitOfWork.Setup(u => u.WalletTransactionDbRepository).Returns(history.Object);
        var spend = Network.RegTest.CreateTransaction();
        spend.Inputs.Add(new TxIn(outpoint));
        spend.Outputs.Add(Money.Satoshis(90_000), s_destination);
        var retained = WalletTransactionHistory.Describe(spend, Height - 2, new byte[32],
            DateTimeOffset.UnixEpoch, [], [new WalletTransactionInput(0, 100_000)]);
        var projection = retained with { RawTransaction = [] };
        var foreign = Network.RegTest.CreateTransaction();
        foreign.Inputs.Add(new TxIn(new OutPoint(outpoint.Hash, checked(outpoint.N + 1))));
        foreign.Outputs.Add(Money.Satoshis(90_000), s_destination);
        retained = mutation switch
        {
            "corrupt" => retained with { RawTransaction = [1, 2] },
            "unconfirmed" => retained with { BlockHeight = null, BlockHash = null },
            "wrong-txid" => retained with { RawTransaction = foreign.ToBytes() },
            _ => retained
        };
        history.Setup(h => h.GetHistoryPageAsync(0, It.IsAny<uint>(), false, 0, 128, It.IsAny<CancellationToken>()))
            .ReturnsAsync([projection]);
        history.Setup(h => h.GetByIdsAsync(It.IsAny<IReadOnlyCollection<TxId>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mutation == "missing" ? Array.Empty<WalletTransactionRecord>() : [retained]);
        await _service.LeaseAsync(s_lockId, coin.TxId, coin.Index, TimeSpan.FromHours(1), 3, Ct);
        _utxos.Spend(coin);
        Assert.Single(await _service.ListLeasesAsync(Ct));
        Assert.True(_utxos.TryGetFeeReservation(coin.TxId, coin.Index, out _));
        Assert.Single(_stored);
        history.Verify(h => h.GetByIdsAsync(It.Is<IReadOnlyCollection<TxId>>(ids => ids.Count == 1
            && ids.Contains(projection.TxId)), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Given_OptInUnconfirmedInput_When_FundedAndFinalized_Then_ExactLeasedInputSignsAndStaleParentIsRefused()
    {
        // Arrange
        var (coin, outpoint, prevout) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        _utxos.Spend(coin);
        var transient = new UtxoModel(coin.TxId, coin.Index, coin.Amount, 0, coin.WalletAddress!);
        _utxos.AddUnconfirmed(transient);
        var catalogue = new Mock<IWalletMempoolCatalog>();
        catalogue.Setup(c => c.RefreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync([transient]);
        catalogue.Setup(c => c.RefreshParentsAsync(It.IsAny<IReadOnlyCollection<TxId>>(), It.IsAny<CancellationToken>())).ReturnsAsync([transient]);
        using var service = MempoolService(catalogue.Object);
        // Act
        var funded = await service.FundPsbtAsync(Request(20_000) with
        { Inputs = [(coin.TxId, coin.Index)], MinConfirmations = 0, SpendUnconfirmed = true }, Ct);
        var finalized = await service.FinalizePsbtAsync(funded.Psbt, Ct);
        // Assert: actual signatures over the live parent output, no confirmed custody or reserve promotion.
        var signed = Transaction.Load(finalized.RawFinalTx, Network.RegTest);
        Assert.Equal(outpoint, Assert.Single(signed.Inputs).PrevOut);
        Assert.True(signed.CreateValidator([prevout]).ValidateInput(0).Error is null or ScriptError.OK);
        Assert.Empty(_utxos.GetUnreservedUtxos());
        Assert.Single(await service.ListLeasesAsync(Ct));
        catalogue.Setup(c => c.RefreshParentsAsync(It.IsAny<IReadOnlyCollection<TxId>>(), It.IsAny<CancellationToken>())).Returns((IReadOnlyCollection<TxId> parents, CancellationToken token) =>
        {
            _utxos.RemoveUnconfirmed(coin.TxId, coin.Index);
            return Task.FromResult<IReadOnlyList<UtxoModel>>([]);
        });
        var error = await Assert.ThrowsAsync<WalletPsbtException>(() => service.FinalizePsbtAsync(funded.Psbt, Ct));
        Assert.Equal(WalletPsbtError.FailedPrecondition, error.Error);
        Assert.True(_utxos.TryGetFeeReservation(coin.TxId, coin.Index, out _));
        Assert.Single(await service.ListLeasesAsync(Ct));
    }

    [Fact]
    public async Task Given_UnconfirmedChangeOnly_When_AnchorsReserveExists_Then_FundingCannotTreatItAsConfirmedReserve()
    {
        // Arrange
        var (coin, _, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        _utxos.Spend(coin);
        var transient = new UtxoModel(coin.TxId, coin.Index, coin.Amount, 0, coin.WalletAddress!);
        _utxos.AddUnconfirmed(transient);
        var catalogue = new Mock<IWalletMempoolCatalog>();
        catalogue.Setup(c => c.RefreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync([transient]);
        catalogue.Setup(c => c.RefreshParentsAsync(It.IsAny<IReadOnlyCollection<TxId>>(), It.IsAny<CancellationToken>())).ReturnsAsync([transient]);
        _reserveSat = 10_000;
        using var service = MempoolService(catalogue.Object);
        // Act / Assert
        await Assert.ThrowsAsync<WalletPsbtException>(() => service.FundPsbtAsync(Request(20_000) with
        { Inputs = [(coin.TxId, coin.Index)], MinConfirmations = 0, SpendUnconfirmed = true }, Ct));
        Assert.Empty(_stored);
    }

    [Fact]
    public void Given_Core31AncestorPackage_When_PricingCpfp_Then_AllAncestorsAndChildRelayFloorAreIncluded()
    {
        // Arrange: base fee 100 sat, ancestors total 300 sat in 300 virtual bytes.
        var package = BitcoinChainService.ParseWalletMempoolEntry(JObject.Parse("""
            {"vsize":100,"ancestorcount":3,"ancestorsize":300,"fees":{"base":0.00000100,"ancestor":0.00000300}}
            """));
        // Act / Assert: at 4 sat/vB (1000 sat/kw), 400 wu child plus 1200 wu ancestors require 1600−300=1300 sat.
        Assert.Equal(1_300, WalletPsbtService.CpfpFee(package, 400, 1_000));
        Assert.Equal(400, WalletPsbtService.CpfpFee(package with { AncestorFeeSat = 10_000 }, 400, 1_000));
        Assert.Throws<FormatException>(() => BitcoinChainService.ParseWalletMempoolEntry(JObject.Parse("""
            {"vsize":100,"ancestorcount":3,"ancestorsize":50,"fees":{"base":0.00000100,"ancestor":0.00000300}}
            """)));
    }

    [Fact]
    public async Task Given_PublishedWalletCpfp_When_BumpedAgain_Then_ReplacementPreservesExactInputAndChangeWithHigherPackageFee()
    {
        // Arrange
        var (coin, outpoint, prevout) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        _utxos.Spend(coin);
        var transient = new UtxoModel(coin.TxId, coin.Index, coin.Amount, 0, coin.WalletAddress!);
        _utxos.AddUnconfirmed(transient);
        var catalogue = new Mock<IWalletMempoolCatalog>();
        catalogue.Setup(c => c.RefreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync([transient]);
        _chain.Setup(c => c.GetMempoolEntryAsync(outpoint.Hash)).ReturnsAsync(new WalletMempoolEntry(100, 100, 100, 100, 1));
        _chain.Setup(c => c.GetIncrementalRelayFeeRatePerKwAsync()).ReturnsAsync(250u);
        var change = GetP2WpkhExtKey(50, true).Neuter().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        _unitOfWork.Setup(u => u.WalletAddressesDbRepository.GetAllAddresses()).Returns([
            coin.WalletAddress!, new WalletAddressModel(AddressType.P2Wpkh, 50, true, change.ToString())]);
        _broadcasts.Setup(b => b.Add(It.IsAny<Domain.Onchain.Models.BroadcastTransactionModel>()))
            .Callback<Domain.Onchain.Models.BroadcastTransactionModel>(_published.Add);
        _broadcasts.Setup(b => b.MarkReplacedAsync(It.IsAny<TxId>())).ReturnsAsync(true);
        using var service = MempoolService(catalogue.Object);
        // Act: first child pays complete ancestor package, second request increases the same child's fee.
        var firstId = await service.BumpOutputAsync(coin.TxId, coin.Index, 1_000, 10_000, Ct);
        var first = Assert.Single(_published);
        var firstTx = Transaction.Load(first.RawTransaction, Network.RegTest);
        _chain.Setup(c => c.GetMempoolSpendersAsync(It.IsAny<IReadOnlyCollection<OutPoint>>()))
            .ReturnsAsync(new Dictionary<OutPoint, uint256> { [outpoint] = firstTx.GetHash() });
        var replacementId = await service.BumpOutputAsync(coin.TxId, coin.Index, 2_000, 10_000, Ct);
        // Assert: both real signatures validate; replacement lineage is durable and no other wallet input appears.
        var replacement = Assert.Single(_published, row => row.TransactionId == replacementId);
        var replacementTx = Transaction.Load(replacement.RawTransaction, Network.RegTest);
        Assert.Equal(firstId, replacement.ReplacesTransactionId);
        Assert.Equal(outpoint, Assert.Single(firstTx.Inputs).PrevOut);
        Assert.Equal(outpoint, Assert.Single(replacementTx.Inputs).PrevOut);
        Assert.Equal(Assert.Single(firstTx.Outputs).ScriptPubKey, Assert.Single(replacementTx.Outputs).ScriptPubKey);
        Assert.True(firstTx.CreateValidator([prevout]).ValidateInput(0).Error is null or ScriptError.OK);
        Assert.True(replacementTx.CreateValidator([prevout]).ValidateInput(0).Error is null or ScriptError.OK);
        Assert.True(replacement.Fee!.Satoshi > first.Fee!.Satoshi);
        Assert.True(_utxos.TryGetFeeReservation(coin.TxId, coin.Index, out _));
        _broadcasts.Verify(b => b.MarkReplacedAsync(firstId), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_InitialCpfp_When_SaveOrPublicationFails_Then_OnlyCommittedIntentRetainsLease(bool failSave)
    {
        var (coin, _, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        _utxos.Spend(coin);
        var transient = new UtxoModel(coin.TxId, coin.Index, coin.Amount, 0, coin.WalletAddress!);
        _utxos.AddUnconfirmed(transient);
        var catalogue = new Mock<IWalletMempoolCatalog>();
        catalogue.Setup(c => c.RefreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync([transient]);
        _chain.Setup(c => c.GetMempoolEntryAsync(It.IsAny<uint256>()))
            .ReturnsAsync(new WalletMempoolEntry(100, 100, 100, 100, 1));
        Domain.Onchain.Models.BroadcastTransactionModel? staged = null;
        var committed = false;
        var saveFailed = false;
        _broadcasts.Setup(b => b.Add(It.IsAny<Domain.Onchain.Models.BroadcastTransactionModel>()))
            .Callback<Domain.Onchain.Models.BroadcastTransactionModel>(row => staged = row);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            if (staged is not null && failSave && !saveFailed)
            {
                saveFailed = true;
                return Task.FromException(new IOException("intent save failed"));
            }
            if (staged is not null && !failSave) committed = true;
            return Task.CompletedTask;
        });
        _monitor.Setup(m => m.PublishAsync(It.IsAny<Domain.Onchain.Models.BroadcastTransactionModel>()))
            .Returns((Domain.Onchain.Models.BroadcastTransactionModel row) =>
            {
                Assert.True(committed); // Core must never be called before the intent save completes.
                Assert.Same(staged, row);
                Assert.Equal("wallet CPFP", row.Label);
                Assert.True(row.Fee!.Satoshi > 0);
                return Task.FromException<bool>(new IOException("ambiguous publication"));
            });
        using var service = MempoolService(catalogue.Object);
        await Assert.ThrowsAsync<IOException>(() => service.BumpOutputAsync(coin.TxId, coin.Index, 1_000, 10_000, Ct));
        Assert.Equal(!failSave, _utxos.TryGetFeeReservation(coin.TxId, coin.Index, out _));
        _monitor.Verify(m => m.PublishAsync(It.IsAny<Domain.Onchain.Models.BroadcastTransactionModel>()),
            failSave ? Times.Never() : Times.Once());
        _monitor.Verify(m => m.SaveAndPublishAsync(It.IsAny<Domain.Onchain.Models.BroadcastTransactionModel>()), Times.Never);
    }

    private WalletPsbtService MempoolService(IWalletMempoolCatalog catalogue)
    {
        Mock.Get(catalogue).Setup(c => c.RefreshParentsAsync(It.IsAny<IReadOnlyCollection<TxId>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyCollection<TxId> _, CancellationToken token) => catalogue.RefreshAsync(token));
        var services = new ServiceCollection().AddScoped(_ => _unitOfWork.Object).AddScoped(_ => _walletService.Object);
        var provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var options = Microsoft.Extensions.Options.Options.Create(new NodeOptions { BitcoinNetwork = "regtest" });
        var selector = new FeeInputSelector(_utxos, scopes, options, NullLogger<FeeInputSelector>.Instance, mempoolCatalog: catalogue);
        var signer = new LocalLightningSigner(Mock.Of<IFundingOutputBuilder>(), Mock.Of<IKeyDerivationService>(),
            NullLogger<LocalLightningSigner>.Instance, options.Value, _keyManager.Object, _utxos);
        return new WalletPsbtService(selector, _anchorReserve.Object, _utxos, signer, _monitor.Object, scopes, options,
            _logger, _chain.Object, _time, _keyManager.Object, catalogue);
    }
}