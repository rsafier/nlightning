using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

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
/// <see cref="WalletPsbtService"/> (LND's walletrpc PSBT and lease subset, NL-1184) over the real
/// <see cref="FeeInputSelector"/> and <see cref="LocalLightningSigner"/>, an in-memory UTXO set and a mocked unit of
/// work: leases are reservations, only leased wallet inputs are signed, and every signature is checked here with
/// NBitcoin's script interpreter.
/// </summary>
public class WalletPsbtServiceTests : IDisposable
{
    private const uint Height = 200;

    private static readonly ExtKey s_masterKey =
        ExtKey.CreateFromSeed(Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"));

    private static readonly BitcoinAddress s_destination =
        new Key(Enumerable.Repeat((byte)7, 32).ToArray()).PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);

    private static readonly byte[] s_lockId = Enumerable.Repeat((byte)0x11, 32).ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeWalletUtxoRepository _utxos = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IFeeInputReservationDbRepository> _reservations = new();
    private readonly Mock<IBroadcastTransactionDbRepository> _broadcasts = new();
    private readonly Mock<IBitcoinWalletService> _walletService = new();
    private readonly Mock<IAnchorReserveService> _anchorReserve = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IBitcoinChainService> _chain = new();
    private readonly Mock<ISecureKeyManager> _keyManager = new();
    private readonly List<FeeInputReservation> _stored = [];
    private readonly List<BroadcastTransactionModel> _published = [];
    private readonly SettableTime _time = new();
    private readonly FeeInputSelector _selector;
    private readonly WalletPsbtService _service;
    private long _reserveSat;

    public WalletPsbtServiceTests()
    {
        var changeAddress = GetP2WpkhExtKey(50, true).Neuter().PubKey.GetAddress(ScriptPubKeyType.Segwit,
                                                                                 Network.RegTest);
        _unitOfWork.Setup(u => u.FeeInputReservationDbRepository).Returns(_reservations.Object);
        _unitOfWork.Setup(u => u.BroadcastTransactionDbRepository).Returns(_broadcasts.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);
        _broadcasts.Setup(b => b.GetPendingAsync()).ReturnsAsync(() => _published.ToList());
        _reservations.Setup(r => r.Add(It.IsAny<FeeInputReservation>(), It.IsAny<DateTimeOffset>()))
                     .Callback<FeeInputReservation, DateTimeOffset>((r, _) => _stored.Add(r));
        _reservations.Setup(r => r.GetAllAsync()).ReturnsAsync(() => _stored.ToList());
        _reservations.Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
                     .ReturnsAsync((Guid id) => _stored.FirstOrDefault(r => r.Id == id));
        _reservations.Setup(r => r.DeleteAsync(It.IsAny<Guid>()))
                     .ReturnsAsync((Guid id) => _stored.RemoveAll(r => r.Id == id) > 0);
        _walletService.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, true))
                      .ReturnsAsync(new WalletAddressModel(AddressType.P2Wpkh, 50, true, changeAddress.ToString()));
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
        _keyManager.Setup(k => k.GetDepositP2WpkhKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()))
                   .Returns((uint index, bool isChange) => GetP2WpkhExtKey(index, isChange).ToBytes());
        _keyManager.Setup(k => k.GetDepositP2TrKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()))
                   .Returns((uint index, bool isChange) => GetP2TrExtKey(index, isChange).ToBytes());

        var services = new ServiceCollection();
        services.AddScoped(_ => _unitOfWork.Object);
        services.AddScoped(_ => _walletService.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var nodeOptions = Microsoft.Extensions.Options.Options.Create(
            new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest });
        _selector = new FeeInputSelector(_utxos, scopeFactory, nodeOptions, NullLogger<FeeInputSelector>.Instance);
        var signer = new LocalLightningSigner(Mock.Of<IFundingOutputBuilder>(), Mock.Of<IKeyDerivationService>(),
                                              NullLogger<LocalLightningSigner>.Instance, nodeOptions.Value,
                                              _keyManager.Object, _utxos);
        _service = new WalletPsbtService(_selector, _anchorReserve.Object, _utxos, signer, _monitor.Object,
                                         scopeFactory, nodeOptions, NullLogger<WalletPsbtService>.Instance,
                                         _chain.Object, _time);
    }

    public void Dispose() => _service.Dispose();

    [Fact]
    public async Task Given_AWallet_When_FundingAPsbt_Then_TheInputsAreLeasedAndTheChangeAdded()
    {
        // Arrange
        var (utxo, _, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);

        // Act
        var result = await _service.FundPsbtAsync(Request(40_000), Ct);

        // Assert
        var psbt = PSBT.Load(result.Psbt, Network.RegTest);
        var tx = psbt.GetGlobalTransaction();
        Assert.Single(tx.Inputs);
        Assert.Equal(2, tx.Outputs.Count);
        Assert.Equal(1, result.ChangeOutputIndex);
        Assert.Equal(40_000, tx.Outputs[0].Value.Satoshi);
        Assert.NotNull(psbt.Inputs[0].WitnessUtxo);
        Assert.Equal(100_000 - 40_000 - tx.Outputs[1].Value.Satoshi, result.Fee.Satoshi);
        var lease = Assert.Single(result.Leases);
        Assert.Equal(s_lockId, lease.LockId);
        Assert.Equal(_time.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds(), lease.Expiration.ToUnixTimeSeconds());
        Assert.Single(await _service.ListLeasesAsync(Ct));
        Assert.DoesNotContain(await _service.ListUnspentAsync(0, uint.MaxValue, Ct), u => u.TxId == utxo.TxId);
    }

    [Fact]
    public async Task Given_AFundedPsbt_When_FinalizedAndPublished_Then_EveryInputVerifiesAndAWalletSendIsStored()
    {
        // Arrange
        var (_, _, p2wpkh) = AddWalletUtxo(AddressType.P2Wpkh, 0, 30_000);
        var (_, _, p2tr) = AddWalletUtxo(AddressType.P2Tr, 1, 30_000);
        var funded = await _service.FundPsbtAsync(Request(50_000), Ct);

        // Act
        var finalized = await _service.FinalizePsbtAsync(funded.Psbt, Ct);
        var published = await _service.PublishAsync(finalized.RawFinalTx, "psbt spend", Ct);

        // Assert
        var tx = Transaction.Load(finalized.RawFinalTx, Network.RegTest);
        var spent = tx.Inputs.Select(i => i.PrevOut).Select(o => _utxos.TryGetUtxo(new TxId(o.Hash.ToBytes()), o.N,
                                                                                   out var u)
                                                                  ? new TxOut(Money.Satoshis(u.Amount.Satoshi),
                                                                              u.AddressType == AddressType.P2Tr
                                                                                  ? p2tr.ScriptPubKey
                                                                                  : p2wpkh.ScriptPubKey)
                                                                  : null!).ToArray();
        var validator = tx.CreateValidator(spent);
        for (var i = 0; i < tx.Inputs.Count; i++)
            Assert.True(validator.ValidateInput(i).Error is null or ScriptError.OK);
        Assert.True(published);
        var row = Assert.Single(_published);
        Assert.Equal(BroadcastPurpose.WalletSend, row.Purpose);
        Assert.Equal("psbt spend", row.Label);
        Assert.Equal(funded.Fee, row.Fee);
        Assert.True(PSBT.Load(finalized.SignedPsbt, Network.RegTest).Inputs.All(i => i.FinalScriptWitness is not null));
    }

    [Fact]
    public async Task Given_AnUnleasedWalletInput_When_Finalizing_Then_ItIsRefused()
    {
        // Arrange
        var (_, outPoint, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var psbt = UnsignedPsbt(outPoint);

        // Act
        var e = await Assert.ThrowsAsync<WalletPsbtException>(() => _service.FinalizePsbtAsync(psbt, Ct));

        // Assert
        Assert.Equal(WalletPsbtError.FailedPrecondition, e.Error);
        Assert.Contains("not leased", e.Message);
    }

    [Fact]
    public async Task Given_AnInputReservedForAnotherSpend_When_Finalizing_Then_ItIsRefused()
    {
        // Arrange: a CPFP's fee input (any other purpose) is never signed for a PSBT
        var (_, outPoint, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        await _selector.ReserveInputsAsync([(new TxId(outPoint.Hash.ToBytes()), outPoint.N)], "cpfp:test", Ct);

        // Act
        var e = await Assert.ThrowsAsync<WalletPsbtException>(() => _service.FinalizePsbtAsync(UnsignedPsbt(outPoint),
                                                                                              Ct));

        // Assert
        Assert.Contains("another spend", e.Message);
    }

    [Fact]
    public async Task Given_ALeasedOutput_When_LeasedUnderAnotherId_Then_RefusedAndTheSameIdExtends()
    {
        // Arrange
        var (utxo, _, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        await _service.LeaseAsync(s_lockId, utxo.TxId, utxo.Index, TimeSpan.FromMinutes(1), Ct);

        // Act
        var other = await Assert.ThrowsAsync<WalletPsbtException>(
                        () => _service.LeaseAsync(new byte[32], utxo.TxId, utxo.Index, TimeSpan.FromMinutes(1), Ct));
        var extended = await _service.LeaseAsync(s_lockId, utxo.TxId, utxo.Index, TimeSpan.FromHours(1), Ct);

        // Assert
        Assert.Equal(WalletPsbtError.FailedPrecondition, other.Error);
        Assert.Equal(_time.GetUtcNow().AddHours(1).ToUnixTimeSeconds(), extended.Expiration.ToUnixTimeSeconds());
        Assert.Single(await _service.ListLeasesAsync(Ct));
    }

    [Fact]
    public async Task Given_ALease_When_ReleasedWithItsId_Then_TheOutputIsFreeAgain()
    {
        // Arrange
        var (utxo, _, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        await _service.LeaseAsync(s_lockId, utxo.TxId, utxo.Index, TimeSpan.Zero, Ct);

        // Act
        var wrongId = await Assert.ThrowsAsync<WalletPsbtException>(
                          () => _service.ReleaseAsync(new byte[32], utxo.TxId, utxo.Index, Ct));
        await _service.ReleaseAsync(s_lockId, utxo.TxId, utxo.Index, Ct);

        // Assert
        Assert.Equal(WalletPsbtError.FailedPrecondition, wrongId.Error);
        Assert.Empty(await _service.ListLeasesAsync(Ct));
        Assert.Contains(await _service.ListUnspentAsync(1, uint.MaxValue, Ct), u => u.TxId == utxo.TxId);
    }

    [Fact]
    public async Task Given_ALeaseOfOneOfAGroup_When_Released_Then_TheOthersStayLeased()
    {
        // Arrange: a PSBT funded from two outputs leases them under one reservation
        AddWalletUtxo(AddressType.P2Wpkh, 0, 30_000);
        AddWalletUtxo(AddressType.P2Wpkh, 1, 30_000);
        var funded = await _service.FundPsbtAsync(Request(50_000), Ct);
        var first = funded.Leases[0];

        // Act
        await _service.ReleaseAsync(s_lockId, first.TxId, first.Index, Ct);

        // Assert
        var left = Assert.Single(await _service.ListLeasesAsync(Ct));
        Assert.Equal(funded.Leases[1].TxId, left.TxId);
    }

    [Fact]
    public async Task Given_AnExpiredLease_When_TheWalletIsUsed_Then_TheOutputIsReleased()
    {
        // Arrange
        var (utxo, _, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        await _service.LeaseAsync(s_lockId, utxo.TxId, utxo.Index, TimeSpan.FromMinutes(1), Ct);

        // Act
        _time.Advance(TimeSpan.FromMinutes(2));
        var leases = await _service.ListLeasesAsync(Ct);

        // Assert
        Assert.Empty(leases);
        Assert.False(_utxos.TryGetFeeReservation(utxo.TxId, utxo.Index, out _));
    }

    [Fact]
    public async Task Given_AnOutputOurPendingBroadcastSpends_When_Leased_Then_Refused()
    {
        // Arrange: our own funding spends it, not mined yet (its channel lock is memory only)
        var (utxo, outPoint, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var spender = Network.RegTest.CreateTransaction();
        spender.Inputs.Add(new TxIn(outPoint));
        spender.Outputs.Add(Money.Satoshis(90_000), s_destination);
        _published.Add(new BroadcastTransactionModel(new SignedTransaction(new TxId(spender.GetHash().ToBytes()),
                                                                           spender.ToBytes()),
                                                     BroadcastPurpose.Funding, null, Height));

        // Act
        var e = await Assert.ThrowsAsync<WalletPsbtException>(
                    () => _service.LeaseAsync(s_lockId, utxo.TxId, utxo.Index, TimeSpan.Zero, Ct));

        // Assert
        Assert.Contains("pending transaction", e.Message);
        Assert.Empty(await _service.ListUnspentAsync(0, uint.MaxValue, Ct));
    }

    [Fact]
    public async Task Given_AChannelLockedOutput_When_Leased_Then_Refused()
    {
        // Arrange
        var (utxo, _, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        utxo.LockedToChannelId = new ChannelId(Enumerable.Repeat((byte)9, 32).ToArray());

        // Act
        var e = await Assert.ThrowsAsync<WalletPsbtException>(
                    () => _service.LeaseAsync(s_lockId, utxo.TxId, utxo.Index, TimeSpan.Zero, Ct));

        // Assert
        Assert.Contains("channel funding", e.Message);
    }

    [Fact]
    public async Task Given_ExplicitInputs_When_FundingAPsbt_Then_ExactlyThoseAreUsed()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var (chosen, _, _) = AddWalletUtxo(AddressType.P2Wpkh, 1, 80_000);

        // Act
        var result = await _service.FundPsbtAsync(Request(50_000) with { Inputs = [(chosen.TxId, chosen.Index)] },
                                                  Ct);

        // Assert
        var tx = PSBT.Load(result.Psbt, Network.RegTest).GetGlobalTransaction();
        Assert.Equal(chosen.TxId, new TxId(Assert.Single(tx.Inputs).PrevOut.Hash.ToBytes()));
        Assert.Equal(1, result.ChangeOutputIndex);
    }

    [Fact]
    public async Task Given_TheAnchorsReserve_When_AFundingWouldEatIt_Then_RefusedAndNothingLeased()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        _reserveSat = 80_000;

        // Act
        var e = await Assert.ThrowsAsync<WalletPsbtException>(() => _service.FundPsbtAsync(Request(50_000), Ct));

        // Assert
        Assert.Contains("anchors reserve", e.Message);
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Given_ATransactionWithoutWalletInputs_When_Published_Then_ItIsSentOnceWithoutARow()
    {
        // Arrange
        var foreign = Network.RegTest.CreateTransaction();
        foreign.Inputs.Add(new TxIn(new OutPoint(RandomUtils.GetUInt256(), 0)));
        foreign.Outputs.Add(Money.Satoshis(10_000), s_destination);

        // Act
        var published = await _service.PublishAsync(foreign.ToBytes(), null, Ct);

        // Assert
        Assert.True(published);
        Assert.Empty(_published);
        _chain.Verify(c => c.SendTransactionAsync(It.IsAny<Transaction>()), Times.Once);
    }

    [Fact]
    public async Task Given_BitcoindRefusesASpend_When_Published_Then_ItIsLndsErrorAndNothingIsStored()
    {
        // Arrange: LND returns an RPC error for a refused publish and keeps nothing (lndclient ignores publish_error)
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var finalized = await _service.FinalizePsbtAsync((await _service.FundPsbtAsync(Request(40_000), Ct)).Psbt, Ct);
        _chain.Setup(c => c.SendTransactionAsync(It.IsAny<Transaction>()))
              .ThrowsAsync(new NBitcoin.RPC.RPCException(NBitcoin.RPC.RPCErrorCode.RPC_VERIFY_REJECTED,
                                                         "txn-mempool-conflict", null!));

        // Act
        var e = await Assert.ThrowsAsync<WalletPsbtException>(() => _service.PublishAsync(finalized.RawFinalTx, null,
                                                                                         Ct));

        // Assert
        Assert.Equal(WalletPsbtError.PublishRefused, e.Error);
        Assert.Equal("transaction rejected: output already spent", e.Message);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Given_BitcoindAlreadyHasTheSpend_When_Published_Then_ItIsStoredAsPublished()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var finalized = await _service.FinalizePsbtAsync((await _service.FundPsbtAsync(Request(40_000), Ct)).Psbt, Ct);
        _chain.Setup(c => c.SendTransactionAsync(It.IsAny<Transaction>()))
              .ThrowsAsync(new NBitcoin.RPC.RPCException(NBitcoin.RPC.RPCErrorCode.RPC_VERIFY_ALREADY_IN_CHAIN,
                                                         "txn-already-in-mempool", null!));

        // Act
        var published = await _service.PublishAsync(finalized.RawFinalTx, null, Ct);

        // Assert
        Assert.True(published);
        Assert.Single(_published);
    }

    [Theory]
    [InlineData("bad-txns-inputs-missingorspent", "transaction rejected: output already spent")]
    [InlineData("txn-mempool-conflict", "transaction rejected: output already spent")]
    [InlineData("insufficient fee, rejecting replacement", "insufficient fee")]
    [InlineData("txn-same-nonwitness-data-in-mempool", "txn same nonwitness data in mempool")]
    [InlineData("mempool min fee not met, 100 < 200",
                "transaction rejected by the mempool because of low fees: mempool min fee not met, 100 < 200")]
    [InlineData("txn-already-known", null)]
    [InlineData("Transaction outputs already in utxo set", null)]
    [InlineData("something else", "something else")]
    public void Given_ARejectReason_When_Mapped_Then_ItIsLndsError(string reason, string? expected)
    {
        // Act / Assert
        Assert.Equal(expected, WalletPsbtService.MapRefusal(reason));
    }

    [Fact]
    public void Given_ALeasePurpose_When_ParsedBack_Then_TheIdAndExpirationRoundTrip()
    {
        // Arrange
        var expiration = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        // Act
        var purpose = WalletPsbtService.LeasePurpose(s_lockId, expiration);
        var parsed = WalletPsbtService.TryParseLease(purpose, out var lockId, out var parsedExpiration);

        // Assert
        Assert.True(parsed);
        Assert.True(purpose.Length <= 128);
        Assert.Equal(s_lockId, lockId);
        Assert.Equal(expiration, parsedExpiration);
        Assert.False(WalletPsbtService.TryParseLease("withdraw", out _, out _));
    }

    private static PsbtFundRequest Request(long amountSat) =>
        new([(new BitcoinScript(s_destination.ScriptPubKey.ToBytes()), LightningMoney.Satoshis(amountSat))], [],
            1_000, 1, s_lockId, TimeSpan.Zero);

    private static byte[] UnsignedPsbt(OutPoint outPoint)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(outPoint));
        tx.Outputs.Add(Money.Satoshis(50_000), s_destination);
        return PSBT.FromTransaction(tx, Network.RegTest).ToBytes();
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

    private sealed class SettableTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}