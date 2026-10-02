using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Accounting.Labels;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// <see cref="WalletSpendService"/> (<c>withdraw</c>, ClientCommand 25) over the real <see cref="FeeInputSelector"/> and
/// <see cref="LocalLightningSigner"/>, an in-memory UTXO set, a mocked unit of work and a mocked anchors reserve. Every
/// signed transaction is checked here with NBitcoin's script interpreter, independently of the service's own check.
/// </summary>
public class WalletSpendServiceTests
{
    private const uint Height = 200;
    private const long FeeRatePerKw = 1_000;

    private static readonly ExtKey s_masterKey =
        ExtKey.CreateFromSeed(Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"));

    private static readonly BitcoinAddress s_destination =
        new Key(Enumerable.Repeat((byte)7, 32).ToArray()).PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);

    private readonly FakeWalletUtxoRepository _utxos = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IFeeInputReservationDbRepository> _reservations = new();
    private readonly Mock<IBroadcastTransactionDbRepository> _broadcasts = new();
    private readonly Mock<IBitcoinWalletService> _walletService = new();
    private readonly Mock<IAnchorReserveService> _anchorReserve = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IFeeService> _feeService = new();
    private readonly Mock<ISecureKeyManager> _keyManager = new();
    private readonly List<FeeInputReservation> _stored = [];
    private readonly List<BroadcastTransactionModel> _published = [];
    private readonly BitcoinAddress _changeAddress;
    private readonly FeeInputSelector _selector;
    private readonly WalletSpendService _service;
    private readonly LocalLightningSigner _signer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NodeOptions _nodeOptions;
    private long _reserveSat;

    public WalletSpendServiceTests()
    {
        _changeAddress = GetP2WpkhExtKey(50, true).Neuter().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);

        _unitOfWork.Setup(u => u.FeeInputReservationDbRepository).Returns(_reservations.Object);
        _unitOfWork.Setup(u => u.BroadcastTransactionDbRepository).Returns(_broadcasts.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);
        // The stored withdrawals are the pending broadcasts, as in the chain monitor's table
        _broadcasts.Setup(b => b.GetPendingAsync()).ReturnsAsync(() => _published.ToList());
        _reservations.Setup(r => r.Add(It.IsAny<FeeInputReservation>(), It.IsAny<DateTimeOffset>()))
                     .Callback<FeeInputReservation, DateTimeOffset>((r, _) => _stored.Add(r));
        _reservations.Setup(r => r.GetAllAsync()).ReturnsAsync(() => _stored.ToList());
        _reservations.Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
                     .ReturnsAsync((Guid id) => _stored.FirstOrDefault(r => r.Id == id));
        _reservations.Setup(r => r.DeleteAsync(It.IsAny<Guid>()))
                     .ReturnsAsync((Guid id) => _stored.RemoveAll(r => r.Id == id) > 0);
        _walletService.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, true))
                      .ReturnsAsync(new WalletAddressModel(AddressType.P2Wpkh, 50, true, _changeAddress.ToString()));

        // The reserve is backed by the unreserved outputs three blocks deep, as AnchorReserveService computes it
        _anchorReserve.Setup(a => a.GetRequiredReserve(It.IsAny<int>()))
                      .Returns(() => LightningMoney.Satoshis(_reserveSat));
        _anchorReserve.Setup(a => a.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            var available = _utxos.GetUnreservedUtxos().Where(u => u.BacksAnchorReserve(Height))
                                  .Sum(u => u.Amount.Satoshi);
            return new AnchorReserveStatus(_reserveSat > 0 ? 1 : 0, LightningMoney.Satoshis(_reserveSat),
                                           LightningMoney.Satoshis(available),
                                           LightningMoney.Satoshis(Math.Max(0, available - _reserveSat)));
        });

        _monitor.Setup(m => m.LastProcessedBlockHeight).Returns(Height);
        _monitor.Setup(m => m.SaveAndPublishAsync(It.IsAny<BroadcastTransactionModel>()))
                .Callback<BroadcastTransactionModel>(_published.Add)
                .ReturnsAsync(true);
        _feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(LightningMoney.Satoshis(FeeRatePerKw));

        _keyManager.Setup(k => k.GetDepositP2WpkhKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()))
                   .Returns((uint index, bool isChange) => GetP2WpkhExtKey(index, isChange).ToBytes());
        _keyManager.Setup(k => k.GetDepositP2TrKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()))
                   .Returns((uint index, bool isChange) => GetP2TrExtKey(index, isChange).ToBytes());

        var services = new ServiceCollection();
        services.AddScoped(_ => _unitOfWork.Object);
        services.AddScoped(_ => _walletService.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var nodeOptions = new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest };

        _selector = new FeeInputSelector(_utxos, scopeFactory, Microsoft.Extensions.Options.Options.Create(nodeOptions),
                                         NullLogger<FeeInputSelector>.Instance);
        _signer = new LocalLightningSigner(Mock.Of<IFundingOutputBuilder>(), Mock.Of<IKeyDerivationService>(),
                                           NullLogger<LocalLightningSigner>.Instance, nodeOptions,
                                           _keyManager.Object, _utxos);
        _scopeFactory = scopeFactory;
        _nodeOptions = nodeOptions;
        _service = CreateService();
    }

    /// <summary>A service over the same wallet and database: a restarted node's.</summary>
    private WalletSpendService CreateService() =>
        new(_selector, _anchorReserve.Object, _utxos, _signer, _monitor.Object, _feeService.Object, _scopeFactory,
            Microsoft.Extensions.Options.Options.Create(_nodeOptions), NullLogger<WalletSpendService>.Instance);

    [Fact]
    public async Task Given_ALabelAndTags_When_Withdrawing_Then_TheWalletSendRowCarriesThem()
    {
        // Arrange (NL-602 A3-T1, withdraw --label/--tag): the WalletSent event copies them at confirmation
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var request = Request(40_000) with { Labels = SourceLabels.Create("cold storage", ["category=savings"]) };

        // Act
        await _service.WithdrawAsync(request, TestContext.Current.CancellationToken);

        // Assert
        var row = Assert.Single(_published);
        Assert.Equal("cold storage", row.Label);
        Assert.Equal("category=savings", row.Tags);
    }

    [Fact]
    public async Task Given_OneP2WpkhOutput_When_WithdrawingAnAmount_Then_TheSignedSpendPaysTheAddressAndTheChange()
    {
        // Arrange
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);

        // Act
        var result = await _service.WithdrawAsync(Request(40_000), TestContext.Current.CancellationToken);

        // Assert
        var tx = AssertPublishedAndValid(utxo.TxOut);
        Assert.Equal(2, tx.Outputs.Count);
        Assert.Equal(40_000, tx.Outputs[0].Value.Satoshi);
        Assert.Equal(s_destination.ScriptPubKey, tx.Outputs[0].ScriptPubKey);
        Assert.Equal(_changeAddress.ScriptPubKey, tx.Outputs[1].ScriptPubKey);
        Assert.Equal(100_000, 40_000 + result.Change.Satoshi + result.Fee.Satoshi);
        Assert.Equal(tx.Outputs[1].Value.Satoshi, result.Change.Satoshi);
        Assert.Equal(new TxId(tx.GetHash().ToBytes()), result.TxId);
        Assert.True(result.Published);
        Assert.Equal(1, result.InputCount);
        Assert.Equal(WalletSpendService.GetWeight(tx), result.Weight);

        // The fee pays at least the requested rate over the signed weight (the estimate assumes 72-byte signatures)
        Assert.True(result.Fee.Satoshi * 1000 >= FeeRatePerKw * result.Weight);
        Assert.True(result.Fee.Satoshi * 1000 <= FeeRatePerKw * (result.Weight + 8) + 1000);

        // The inputs stay reserved for the pending broadcast
        var reservation = Assert.Single(_stored);
        Assert.Equal(WalletSpendService.ReservationPurpose, reservation.Purpose);
        Assert.True(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out var id));
        Assert.Equal(reservation.Id, id);
        var row = Assert.Single(_published);
        Assert.Equal(BroadcastPurpose.WalletSend, row.Purpose);
        Assert.Null(row.ChannelId);
        Assert.Equal((uint)FeeRatePerKw, row.FeeratePerKw);
        Assert.Equal(BroadcastState.Pending, row.State);

        // The absolute fee is stored with the row (NL-604; the accounting feed records it at confirmation, NL-602)
        Assert.NotNull(row.Fee);
        Assert.Equal(result.Fee.MilliSatoshi, row.Fee.MilliSatoshi);
        Assert.Equal(100_000 - 40_000 - result.Change.Satoshi, row.Fee.Satoshi);
    }

    [Fact]
    public async Task Given_P2TrAndP2WpkhOutputs_When_WithdrawingMoreThanEither_Then_BothInputsVerify()
    {
        // Arrange
        var p2Tr = AddWalletUtxo(AddressType.P2Tr, 3, 60_000);
        var p2Wpkh = AddWalletUtxo(AddressType.P2Wpkh, 4, 50_000);

        // Act
        var result = await _service.WithdrawAsync(Request(90_000), TestContext.Current.CancellationToken);

        // Assert: largest first, so the P2TR input comes first
        AssertPublishedAndValid(p2Tr.TxOut, p2Wpkh.TxOut);
        Assert.Equal(2, result.InputCount);
    }

    [Fact]
    public async Task Given_NoAnchorsChannel_When_WithdrawingAll_Then_EverythingGoesToTheAddressWithoutChange()
    {
        // Arrange
        var first = AddWalletUtxo(AddressType.P2Wpkh, 0, 70_000);
        var second = AddWalletUtxo(AddressType.P2Wpkh, 1, 30_000);

        // Act
        var result = await _service.WithdrawAsync(Request(null), TestContext.Current.CancellationToken);

        // Assert
        var tx = AssertPublishedAndValid(first.TxOut, second.TxOut);
        var output = Assert.Single(tx.Outputs);
        Assert.Equal(s_destination.ScriptPubKey, output.ScriptPubKey);
        Assert.Equal(100_000 - result.Fee.Satoshi, output.Value.Satoshi);
        Assert.Equal(0, result.Change.Satoshi);
        Assert.True(result.Fee.Satoshi * 1000 >= FeeRatePerKw * result.Weight);
    }

    [Fact]
    public async Task Given_AnAnchorsReserve_When_WithdrawingAll_Then_TheReserveComesBackAsChange()
    {
        // Arrange
        _reserveSat = 10_000;
        var first = AddWalletUtxo(AddressType.P2Wpkh, 0, 70_000);
        var second = AddWalletUtxo(AddressType.P2Wpkh, 1, 30_000);

        // Act
        var result = await _service.WithdrawAsync(Request(null), TestContext.Current.CancellationToken);

        // Assert
        var tx = AssertPublishedAndValid(first.TxOut, second.TxOut);
        Assert.Equal(2, tx.Outputs.Count);
        Assert.Equal(10_000, tx.Outputs[1].Value.Satoshi);
        Assert.Equal(_changeAddress.ScriptPubKey, tx.Outputs[1].ScriptPubKey);
        Assert.Equal(100_000 - 10_000 - result.Fee.Satoshi, result.Amount.Satoshi);
        Assert.Equal(10_000, result.AnchorReserve.Satoshi);
    }

    [Fact]
    public async Task Given_AnAnchorsReserve_When_TheAmountLeavesLessThanIt_Then_RefusedAndNothingStaysReserved()
    {
        // Arrange: 100,000 - 95,000 - fee leaves under 5,000 sat of change
        _reserveSat = 10_000;
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);

        // Act
        var exception = await Assert.ThrowsAsync<AnchorReserveException>(
            () => _service.WithdrawAsync(Request(95_000), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(10_000, exception.RequiredReserve.Satoshi);
        Assert.Contains("anchors reserve", exception.Message);
        Assert.False(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
        Assert.Empty(_stored);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Given_AnAnchorsReserve_When_ChangeCoversIt_Then_TheWithdrawalGoesThrough()
    {
        // Arrange: the change (about 15,000 sat) keeps the reserve, as for a channel funding
        _reserveSat = 10_000;
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);

        // Act
        var result = await _service.WithdrawAsync(Request(85_000), TestContext.Current.CancellationToken);

        // Assert
        AssertPublishedAndValid(utxo.TxOut);
        Assert.True(result.Change.Satoshi >= 10_000);
    }

    [Fact]
    public async Task Given_AnAnchorsReserveAndATooSmallWallet_When_WithdrawingAll_Then_Refused()
    {
        // Arrange
        _reserveSat = 10_000;
        AddWalletUtxo(AddressType.P2Wpkh, 0, 10_200);

        // Act / Assert
        await Assert.ThrowsAsync<AnchorReserveException>(
            () => _service.WithdrawAsync(Request(null), TestContext.Current.CancellationToken));
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Given_TooLittleMoney_When_Withdrawing_Then_InsufficientFundsAndNothingReserved()
    {
        // Arrange
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 20_000);

        // Act / Assert
        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => _service.WithdrawAsync(Request(20_000), TestContext.Current.CancellationToken));
        Assert.False(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Given_AnEmptyWallet_When_WithdrawingAll_Then_InsufficientFunds()
    {
        // Act / Assert
        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => _service.WithdrawAsync(Request(null), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_OutputsLockedToAFundingOrUnconfirmed_When_Withdrawing_Then_TheyAreNeverSpent()
    {
        // Arrange
        var locked = AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        locked.Model.LockedToChannelId = ChannelId.Zero;
        AddWalletUtxo(AddressType.P2Wpkh, 1, 400_000, blockHeight: 0);
        var usable = AddWalletUtxo(AddressType.P2Wpkh, 2, 50_000);

        // Act
        await _service.WithdrawAsync(Request(null), TestContext.Current.CancellationToken);

        // Assert
        var tx = AssertPublishedAndValid(usable.TxOut);
        Assert.Equal(usable.OutPoint, Assert.Single(tx.Inputs).PrevOut);
    }

    [Theory]
    [InlineData(293)]
    [InlineData(0)]
    public async Task Given_ADustAmount_When_Withdrawing_Then_RefusedAsDust(long amountSat)
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);

        // Act
        var exception = await Assert.ThrowsAsync<WalletSpendException>(
            () => _service.WithdrawAsync(Request(amountSat), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(WalletSpendError.DustAmount, exception.Error);
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Given_AMainnetAddress_When_WithdrawingOnRegtest_Then_RefusedAsWrongNetwork()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var mainnet = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();

        // Act
        var exception = await Assert.ThrowsAsync<WalletSpendException>(
            () => _service.WithdrawAsync(new WalletWithdrawRequest(mainnet, LightningMoney.Satoshis(10_000), null),
                                         TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(WalletSpendError.WrongNetwork, exception.Error);
        Assert.Contains("Main", exception.Message);
        Assert.Empty(_stored);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("")]
    [InlineData("bcrt1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq")]
    public async Task Given_TextThatIsNoAddress_When_Withdrawing_Then_RefusedAsInvalidAddress(string address)
    {
        // Act
        var exception = await Assert.ThrowsAsync<WalletSpendException>(
            () => _service.WithdrawAsync(new WalletWithdrawRequest(address, LightningMoney.Satoshis(10_000), null),
                                         TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(WalletSpendError.InvalidAddress, exception.Error);
    }

    [Fact]
    public async Task Given_HaltedChainProcessing_When_Withdrawing_Then_Refused()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        _monitor.Setup(m => m.IsChainProcessingHalted).Returns(true);

        // Act
        var exception = await Assert.ThrowsAsync<WalletSpendException>(
            () => _service.WithdrawAsync(Request(10_000), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(WalletSpendError.ChainProcessingHalted, exception.Error);
        Assert.Empty(_stored);
    }

    [Theory]
    [InlineData(252, WalletSpendError.FeeRateTooLow)]
    [InlineData(250_001, WalletSpendError.FeeRateTooHigh)]
    public async Task Given_AFeeRateOutOfBounds_When_Withdrawing_Then_Refused(long feeRatePerKw,
                                                                             WalletSpendError expected)
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);

        // Act
        var exception = await Assert.ThrowsAsync<WalletSpendException>(
            () => _service.WithdrawAsync(Request(10_000, feeRatePerKw), TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(expected, exception.Error);
    }

    [Fact]
    public async Task Given_ARequestedFeeRate_When_Withdrawing_Then_ItIsUsedInsteadOfTheEstimate()
    {
        // Arrange
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);

        // Act
        var result = await _service.WithdrawAsync(Request(10_000, 5_000), TestContext.Current.CancellationToken);

        // Assert
        AssertPublishedAndValid(utxo.TxOut);
        Assert.Equal(5_000, result.FeeRatePerKw.Satoshi);
        Assert.True(result.Fee.Satoshi * 1000 >= 5_000L * result.Weight);
        _feeService.Verify(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_TheSendIsRefused_When_Withdrawing_Then_StoredNotPublishedAndTheInputsStayReserved()
    {
        // Arrange
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        _monitor.Setup(m => m.SaveAndPublishAsync(It.IsAny<BroadcastTransactionModel>())).ReturnsAsync(false);

        // Act
        var result = await _service.WithdrawAsync(Request(10_000), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Published);
        Assert.True(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
        Assert.Single(_stored);
    }

    [Fact]
    public async Task Given_TheRowSaveFails_When_Withdrawing_Then_TheReservationIsReleased()
    {
        // Arrange
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        _monitor.Setup(m => m.SaveAndPublishAsync(It.IsAny<BroadcastTransactionModel>()))
                .ThrowsAsync(new InvalidOperationException("disk full"));
        _broadcasts.Setup(b => b.GetByTransactionIdAsync(It.IsAny<TxId>()))
                   .ReturnsAsync((BroadcastTransactionModel?)null);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.WithdrawAsync(Request(10_000), TestContext.Current.CancellationToken));

        // Assert
        Assert.False(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Given_AnEarlierWithdrawalWhoseInputsLeftTheWallet_When_WithdrawingAgain_Then_ItsReservationEnds()
    {
        // Arrange: the first withdrawal's input is spent in a processed block
        var first = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        await _service.WithdrawAsync(Request(10_000), TestContext.Current.CancellationToken);
        var firstReservation = Assert.Single(_stored);
        _utxos.Spend(first.Model);
        AddWalletUtxo(AddressType.P2Wpkh, 1, 100_000);

        // Act
        await _service.WithdrawAsync(Request(10_000), TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(_stored, r => r.Id == firstReservation.Id);
        Assert.Single(_stored);
    }

    [Fact]
    public async Task Given_AWithdrawReservationACrashLeftWithoutItsRow_When_TheNodeStarts_Then_ItIsReleased()
    {
        // Arrange: the reservation was persisted, the process died before the WalletSend row was saved
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        await _selector.ReserveAsync(LightningMoney.Satoshis(10_000), LightningMoney.Satoshis(FeeRatePerKw), 200,
                                     WalletSpendService.ReservationPurpose, TestContext.Current.CancellationToken);
        Assert.True(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
        var restarted = CreateService();

        // Act
        var released = await restarted.ReleaseOrphanedReservationsAsync(TestContext.Current.CancellationToken);

        // Assert: the output is selectable again (for fundings, CPFP and the reserve too)
        Assert.Equal(1, released);
        Assert.Empty(_stored);
        Assert.False(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
        Assert.Contains(_utxos.GetUnreservedUtxos(), u => u.TxId.Equals(utxo.Model.TxId));
    }

    [Fact]
    public async Task Given_AnOrphanedWithdrawReservationOnTheOnlyOutput_When_WithdrawingAgain_Then_ItSpendsThatOutput()
    {
        // Arrange: a failed save that could not be verified (IsStoredAsync kept the reservation) left no row
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var orphan = await _selector.ReserveAsync(LightningMoney.Satoshis(10_000),
                                                  LightningMoney.Satoshis(FeeRatePerKw), 200,
                                                  WalletSpendService.ReservationPurpose,
                                                  TestContext.Current.CancellationToken);

        // Act
        await _service.WithdrawAsync(Request(20_000), TestContext.Current.CancellationToken);

        // Assert
        var tx = AssertPublishedAndValid(utxo.TxOut);
        Assert.Equal(utxo.OutPoint, Assert.Single(tx.Inputs).PrevOut);
        Assert.DoesNotContain(_stored, r => r.Id == orphan.Id);
        Assert.Single(_stored);
    }

    [Fact]
    public async Task Given_AStoredPendingWithdrawal_When_OrphansAreReleased_Then_ItsReservationIsKept()
    {
        // Arrange
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        await _service.WithdrawAsync(Request(10_000), TestContext.Current.CancellationToken);
        var reservation = Assert.Single(_stored);

        // Act
        var released = await CreateService().ReleaseOrphanedReservationsAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, released);
        Assert.Equal(reservation.Id, Assert.Single(_stored).Id);
        Assert.True(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
    }

    [Fact]
    public async Task Given_ThePendingBroadcastsCannotBeRead_When_OrphansAreReleased_Then_NothingIsReleased()
    {
        // Arrange
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        await _selector.ReserveAsync(LightningMoney.Satoshis(10_000), LightningMoney.Satoshis(FeeRatePerKw), 200,
                                     WalletSpendService.ReservationPurpose, TestContext.Current.CancellationToken);
        _broadcasts.Setup(b => b.GetPendingAsync()).ThrowsAsync(new InvalidOperationException("database is locked"));

        // Act
        var released = await _service.ReleaseOrphanedReservationsAsync(TestContext.Current.CancellationToken);

        // Assert: unknown, so the reservation stays (it never double-spends)
        Assert.Equal(0, released);
        Assert.Single(_stored);
        Assert.True(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
    }

    [Fact]
    public async Task Given_AReservationOfAnotherPurpose_When_OrphansAreReleased_Then_ItIsLeftAlone()
    {
        // Arrange: a CPFP child's reservation is its own service's business
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        await _selector.ReserveAsync(LightningMoney.Satoshis(10_000), LightningMoney.Satoshis(FeeRatePerKw), 200,
                                     "cpfp:test", TestContext.Current.CancellationToken);

        // Act
        var released = await _service.ReleaseOrphanedReservationsAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, released);
        Assert.True(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
    }

    [Theory]
    [InlineData(ScriptPubKeyType.Segwit, 294)]
    [InlineData(ScriptPubKeyType.TaprootBIP86, 330)]
    [InlineData(ScriptPubKeyType.Legacy, 546)]
    [InlineData(ScriptPubKeyType.SegwitP2SH, 540)]
    public void Given_AnOutputType_When_GettingTheDustThreshold_Then_ItIsBitcoinCores(ScriptPubKeyType type,
                                                                                       long expected)
    {
        // Arrange
        var script = new Key().PubKey.GetScriptPubKey(type);

        // Act / Assert
        Assert.Equal(expected, WalletSpendService.GetDustThreshold(script));
    }

    [Fact]
    public void Given_AP2WshOutput_When_GettingTheDustThreshold_Then_330()
    {
        // Arrange
        var script = new Script(OpcodeType.OP_1).WitHash.ScriptPubKey;

        // Act / Assert
        Assert.Equal(330, WalletSpendService.GetDustThreshold(script));
        Assert.Equal(43 * 4, WalletSpendService.GetOutputWeight(script));
    }

    private static WalletWithdrawRequest Request(long? amountSat, long? feeRatePerKw = null) =>
        new(s_destination.ToString(), amountSat is { } a ? LightningMoney.Satoshis(a) : null,
            feeRatePerKw is { } f ? LightningMoney.Satoshis(f) : null);

    private Transaction AssertPublishedAndValid(params TxOut[] spentOutputs)
    {
        var row = Assert.Single(_published);
        var tx = Transaction.Load(row.RawTransaction, Network.RegTest);
        Assert.Equal(new TxId(tx.GetHash().ToBytes()), row.TransactionId);
        Assert.Equal(spentOutputs.Length, tx.Inputs.Count);
        Assert.All(tx.Inputs, i => Assert.Equal(0xFFFFFFFDu, (uint)i.Sequence));
        var validator = tx.CreateValidator(spentOutputs);
        for (var i = 0; i < tx.Inputs.Count; i++)
            Assert.True(validator.ValidateInput(i).Error is null or ScriptError.OK, $"input {i} does not verify");

        var fee = spentOutputs.Sum(o => o.Value.Satoshi) - tx.Outputs.Sum(o => o.Value.Satoshi);
        Assert.True(fee > 0);
        return tx;
    }

    private static ExtKey GetP2WpkhExtKey(uint index, bool isChange) =>
        s_masterKey.Derive(isChange ? 1u : 0u).Derive(index);

    private static ExtKey GetP2TrExtKey(uint index, bool isChange) =>
        s_masterKey.Derive(isChange ? 3u : 2u).Derive(index);

    private (UtxoModel Model, OutPoint OutPoint, TxOut TxOut) AddWalletUtxo(AddressType type, uint index,
                                                                           long amountSat, uint blockHeight = 100)
    {
        var pubKey = type == AddressType.P2Wpkh
                         ? GetP2WpkhExtKey(index, false).Neuter().PubKey
                         : GetP2TrExtKey(index, false).Neuter().PubKey;
        var address = pubKey.GetAddress(type == AddressType.P2Wpkh
                                            ? ScriptPubKeyType.Segwit
                                            : ScriptPubKeyType.TaprootBIP86, Network.RegTest);
        var outPoint = new OutPoint(RandomUtils.GetUInt256(), index);
        var model = new UtxoModel(new TxId(outPoint.Hash.ToBytes()), outPoint.N, LightningMoney.Satoshis(amountSat),
                                  blockHeight, new WalletAddressModel(type, index, false, address.ToString()));
        _utxos.Add(model);
        return (model, outPoint, new TxOut(Money.Satoshis(amountSat), address.ScriptPubKey));
    }
}