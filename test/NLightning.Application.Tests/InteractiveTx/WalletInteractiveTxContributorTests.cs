using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.InteractiveTx;

using Application.InteractiveTx;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.InteractiveTx;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Repositories.Memory;

/// <summary>
/// <see cref="WalletInteractiveTxContributor"/> (splicing plan IT2-T3, IT-ABT-01) over the real
/// <c>FeeInputSelector</c>, <see cref="LocalLightningSigner"/>, <see cref="UtxoMemoryRepository"/>,
/// <see cref="PrevTxInspector"/> and <see cref="InteractiveTxBuilder"/>, with a mocked unit of work and an in-memory
/// source of the wallet's previous transactions. Every signature is checked with NBitcoin's script interpreter.
/// </summary>
public class WalletInteractiveTxContributorTests
{
    private const uint FeeratePerKw = 253;

    private static readonly ExtKey s_masterKey =
        ExtKey.CreateFromSeed(Convert.FromHexString("202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f"));

    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x42, 32).ToArray());

    private static readonly Script s_peerScript =
        new Key(Enumerable.Repeat((byte)9, 32).ToArray()).PubKey.WitHash.ScriptPubKey;

    private static readonly Script s_fundingScript =
        new Key(Enumerable.Repeat((byte)8, 32).ToArray()).PubKey.ScriptPubKey.WitHash.ScriptPubKey;

    private readonly UtxoMemoryRepository _utxos = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IFeeInputReservationDbRepository> _reservations = new();
    private readonly Mock<IBroadcastTransactionDbRepository> _broadcasts = new();
    private readonly Mock<IBitcoinWalletService> _walletService = new();
    private readonly Mock<IAnchorReserveService> _anchorReserve = new();
    private readonly Mock<ISecureKeyManager> _keyManager = new();
    private readonly FakePrevTxSource _prevTxSource = new();
    private readonly List<FeeInputReservation> _stored = [];
    private readonly BitcoinAddress _changeAddress;
    private readonly IFeeInputSelector _selector;
    private readonly WalletInteractiveTxContributor _contributor;
    private long _reserveSat;

    public WalletInteractiveTxContributorTests()
    {
        _changeAddress = GetP2WpkhExtKey(50, true).Neuter().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);

        _unitOfWork.Setup(u => u.FeeInputReservationDbRepository).Returns(_reservations.Object);
        _unitOfWork.Setup(u => u.BroadcastTransactionDbRepository).Returns(_broadcasts.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);
        _broadcasts.Setup(b => b.GetPendingAsync()).ReturnsAsync([]);
        _reservations.Setup(r => r.Add(It.IsAny<FeeInputReservation>(), It.IsAny<DateTimeOffset>()))
                     .Callback<FeeInputReservation, DateTimeOffset>((r, _) => _stored.Add(r));
        _reservations.Setup(r => r.GetAllAsync()).ReturnsAsync(() => _stored.ToList());
        _reservations.Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
                     .ReturnsAsync((Guid id) => _stored.FirstOrDefault(r => r.Id == id));
        _reservations.Setup(r => r.DeleteAsync(It.IsAny<Guid>()))
                     .ReturnsAsync((Guid id) => _stored.RemoveAll(r => r.Id == id) > 0);
        _walletService.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, true))
                      .ReturnsAsync(new WalletAddressModel(AddressType.P2Wpkh, 50, true, _changeAddress.ToString()));

        _anchorReserve.Setup(a => a.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            var available = _utxos.GetUnreservedUtxos().Where(u => u.BlockHeight != 0).Sum(u => u.Amount.Satoshi);
            return new AnchorReserveStatus(_reserveSat > 0 ? 1 : 0, LightningMoney.Satoshis(_reserveSat),
                                           LightningMoney.Satoshis(available),
                                           LightningMoney.Satoshis(Math.Max(0, available - _reserveSat)));
        });

        _keyManager.Setup(k => k.GetDepositP2WpkhKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()))
                   .Returns((uint index, bool isChange) => GetP2WpkhExtKey(index, isChange).ToBytes());
        _keyManager.Setup(k => k.GetDepositP2TrKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()))
                   .Returns((uint index, bool isChange) => GetP2TrExtKey(index, isChange).ToBytes());

        var services = new ServiceCollection();
        services.AddScoped(_ => _unitOfWork.Object);
        services.AddScoped(_ => _walletService.Object);
        var provider = services.BuildServiceProvider();
        var nodeOptions = new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest };

        _selector = new Infrastructure.Bitcoin.Wallet.FeeInputSelector(
            _utxos, provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(nodeOptions),
            NullLogger<Infrastructure.Bitcoin.Wallet.FeeInputSelector>.Instance);
        var signer = new LocalLightningSigner(Mock.Of<IFundingOutputBuilder>(), Mock.Of<IKeyDerivationService>(),
                                              NullLogger<LocalLightningSigner>.Instance, nodeOptions,
                                              _keyManager.Object, _utxos);
        _contributor = new WalletInteractiveTxContributor(_selector, signer, _utxos, _prevTxSource,
                                                          new PrevTxInspector(), _anchorReserve.Object);
    }

    [Fact]
    public async Task Given_AConfirmedP2WpkhOutput_When_Contributing_Then_ItIsReservedWithItsPrevTxAndChange()
    {
        // Arrange
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);

        // Act
        var contribution = await _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);

        // Assert: fee = 253 sat/kw over the input (272) and the change (124) = 101 sat
        var input = Assert.Single(contribution.Inputs);
        Assert.Equal(utxo.Model.TxId, input.PrevTxId);
        Assert.Equal(utxo.Model.Index, input.PrevTxVout);
        Assert.Equal(_prevTxSource.Transactions[utxo.Model.TxId], input.PrevTx);
        Assert.Equal(0xFFFFFFFDu, input.Sequence);
        Assert.Equal(272, input.InputWeight);
        var change = Assert.Single(contribution.Outputs);
        Assert.True(change.IsChange);
        Assert.Equal(_changeAddress.ScriptPubKey.ToBytes(), (byte[])change.ScriptPubKey);
        Assert.Equal(500_000 - 200_000 - 101, change.Amount.Satoshi);
        var reservation = Assert.Single(_stored);
        Assert.Equal(reservation.Id, contribution.ReservationId);
        Assert.Equal($"itx:splice:{s_channelId}", reservation.Purpose);
        Assert.True(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
    }

    [Fact]
    public async Task Given_RequestedOutputsAndExtraWeight_When_Contributing_Then_OurFeeCoversThemAndTheyAreReturned()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var spliceOut = new ContributedOutput(LightningMoney.Satoshis(50_000), s_peerScript.ToBytes(), false);

        // Act
        var contribution = await _contributor.ContributeAsync(
                               Request(100_000, [spliceOut], extraWeight: 1_000), TestContext.Current.CancellationToken);

        // Assert: fee = 253 x (1,000 + 124 (splice-out) + 272 + 124 (change)) = 385 sat
        Assert.Equal(2, contribution.Outputs.Count);
        Assert.Equal(spliceOut, contribution.Outputs[0]);
        Assert.Equal(500_000 - 100_000 - 50_000 - 385, contribution.Outputs[1].Amount.Satoshi);
    }

    [Fact]
    public async Task Given_AP2TrOutput_When_Contributing_Then_TheFeeCountsBolt3sMinimumWitnessWeight()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Tr, 0, 500_000);

        // Act
        var contribution = await _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);

        // Assert: the P2TR input weighs 231, the peer charges 164 + 107 = 271; with the change 395 WU = 100 sat
        Assert.Equal(231, Assert.Single(contribution.Inputs).InputWeight);
        Assert.Equal(500_000 - 200_000 - 100, Assert.Single(contribution.Outputs).Amount.Satoshi);
    }

    [Fact]
    public async Task Given_ChangeBelowDust_When_Contributing_Then_NoChangeOutputIsAdded()
    {
        // Arrange: 200,000 + 69 sat fee without change leaves 231 sat, below the 294 sat dust limit
        AddWalletUtxo(AddressType.P2Wpkh, 0, 200_300);

        // Act
        var contribution = await _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(contribution.Inputs);
        Assert.Empty(contribution.Outputs);
    }

    [Fact]
    public async Task Given_UnconfirmedOutputs_When_ContributingWithConfirmedInputsRequired_Then_OnlyConfirmedAreUsed()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 900_000, blockHeight: 0);
        var confirmed = AddWalletUtxo(AddressType.P2Wpkh, 1, 400_000);

        // Act
        var contribution = await _contributor.ContributeAsync(Request(200_000, requireConfirmed: true),
                                                              TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(confirmed.Model.TxId, Assert.Single(contribution.Inputs).PrevTxId);
    }

    [Fact]
    public async Task Given_TooLittleInTheWallet_When_Contributing_Then_InsufficientFundsAndNothingReserved()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);

        // Act & Assert
        await Assert.ThrowsAsync<InsufficientFundsException>(
            () => _contributor.ContributeAsync(Request(100_000), TestContext.Current.CancellationToken));
        Assert.Empty(_stored);
        Assert.Single(_utxos.GetUnreservedUtxos());
    }

    [Fact]
    public async Task Given_TheAnchorsReserve_When_TheContributionWouldSpendIt_Then_ItIsRefusedAndReleased()
    {
        // Arrange: 300,000 sat in the wallet, a 150,000 sat reserve
        AddWalletUtxo(AddressType.P2Wpkh, 0, 300_000);
        _reserveSat = 150_000;

        // Act & Assert
        await Assert.ThrowsAsync<AnchorReserveException>(
            () => _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken));
        Assert.Empty(_stored);
        Assert.Single(_utxos.GetUnreservedUtxos());

        // A contribution that leaves the reserve as change passes
        var contribution = await _contributor.ContributeAsync(Request(100_000), TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(contribution.Outputs).Amount.Satoshi >= 150_000);
    }

    [Fact]
    public async Task Given_APrevTxThatDoesNotMatchTheOutput_When_Contributing_Then_ItThrowsAndReleases()
    {
        // Arrange
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var other = AddWalletUtxo(AddressType.P2Wpkh, 1, 1_000);
        _prevTxSource.Transactions[utxo.Model.TxId] = _prevTxSource.Transactions[other.Model.TxId];

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken));
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Given_NoWalletAmount_When_Contributing_Then_NoInputIsReserved()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var spliceOut = new ContributedOutput(LightningMoney.Satoshis(50_000), s_peerScript.ToBytes(), false);

        // Act
        var empty = await _contributor.ContributeAsync(Request(0), TestContext.Current.CancellationToken);
        var outputsOnly = await _contributor.ContributeAsync(Request(0, [spliceOut]),
                                                             TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(InteractiveTxContribution.Empty, empty);
        Assert.Empty(outputsOnly.Inputs);
        Assert.Equal([spliceOut], outputsOnly.Outputs);
        Assert.Null(outputsOnly.ReservationId);
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Given_AnAbortBeforeOurSignatures_When_Releasing_Then_TheOutputsReturnToTheWallet()
    {
        // Arrange
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var contribution = await _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);

        // Act
        await _contributor.ReleaseAsync(contribution, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_stored);
        Assert.False(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
    }

    [Theory]
    [InlineData(AddressType.P2Wpkh)]
    [InlineData(AddressType.P2Tr)]
    public async Task Given_TheConstructedTx_When_Signing_Then_OurInputsAreValidAndOnlyTheyAreSigned(
        AddressType addressType)
    {
        // Arrange
        var utxo = AddWalletUtxo(addressType, 0, 500_000);
        var contribution = await _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);
        var (constructed, peerSpent) = Construct(contribution);

        // Act
        var witnesses = await _contributor.SignAsync(constructed, contribution, [peerSpent],
                                                     TestContext.Current.CancellationToken);

        // Assert: our input is 0 (serial_id 0), the peer's (serial_id 1) is left for the peer
        var witness = Assert.Single(witnesses);
        var signed = new InteractiveTxBuilder().Finalize(constructed, new Dictionary<ulong, Witness>
        {
            [0] = witness,
            [1] = new Witness([0x01, 0x01, 0x00])
        });
        var tx = Transaction.Load(signed.RawTxBytes, Network.RegTest);
        var validator = tx.CreateValidator([utxo.TxOut, new TxOut(Money.Satoshis(peerSpent.Amount.Satoshi),
                                                                  new Script((byte[])peerSpent.ScriptPubKey))]);
        var ours = validator.ValidateInput(0);
        Assert.True(ours.Error is null or ScriptError.OK, $"our input: {ours.Error}");
        Assert.Equal((byte[])constructed.TxId, tx.GetHash().ToBytes());
    }

    [Fact]
    public async Task Given_OurSignaturesWereProduced_When_Releasing_Then_TheReservationIsKeptUntilAnInputIsSpent()
    {
        // Arrange
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var contribution = await _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);
        var (constructed, peerSpent) = Construct(contribution);
        await _contributor.SignAsync(constructed, contribution, [peerSpent], TestContext.Current.CancellationToken);

        // Act: an abort after our tx_signatures (IT-ABT-01)
        await _contributor.ReleaseAsync(contribution, TestContext.Current.CancellationToken);
        var confirmedWhileUnspent = await _contributor.ConfirmAsync(contribution,
                                                                    TestContext.Current.CancellationToken);
        _utxos.Spend(utxo.Model); // the chain monitor processed the block spending it
        var confirmedAfterSpend = await _contributor.ConfirmAsync(contribution, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(confirmedWhileUnspent);
        Assert.True(confirmedAfterSpend);
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Given_OurSignaturesWereProduced_When_Releasing_Then_TheOutputsStayReserved()
    {
        // Arrange
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var contribution = await _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);
        var (constructed, peerSpent) = Construct(contribution);
        await _contributor.SignAsync(constructed, contribution, [peerSpent], TestContext.Current.CancellationToken);

        // Act
        await _contributor.ReleaseAsync(contribution, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(_stored);
        Assert.True(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
    }

    [Fact]
    public async Task Given_ATransactionWithoutOurChange_When_Signing_Then_NothingIsSignedAndReleaseStillWorks()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var contribution = await _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);
        var (constructed, peerSpent) = Construct(contribution);
        var tampered = new InteractiveTxBuilder().Build(
            constructed.Locktime, constructed.Inputs,
            constructed.Outputs.Select(o => o.IsShared ? o : o with { Amount = o.Amount - LightningMoney.Satoshis(1_000) }).ToList());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _contributor.SignAsync(tampered, contribution, [peerSpent], TestContext.Current.CancellationToken));
        await _contributor.ReleaseAsync(contribution, TestContext.Current.CancellationToken);
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Given_ATransactionWithoutOurInput_When_Signing_Then_ItThrows()
    {
        // Arrange
        AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var contribution = await _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);
        var (constructed, peerSpent) = Construct(contribution);
        var withoutOurs = new InteractiveTxBuilder().Build(constructed.Locktime,
                                                           constructed.Inputs.Where(i => i.SerialId != 0).ToList(),
                                                           constructed.Outputs);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _contributor.SignAsync(withoutOurs, contribution, [peerSpent],
                                         TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The negotiated transaction as the session would build it: we are the initiator (even serial ids), the peer adds
    /// one P2WPKH input and the shared funding output takes our wallet amount and the peer's input.
    /// </summary>
    private static (ConstructedInteractiveTx Tx, SpentOutput PeerSpent) Construct(
        InteractiveTxContribution contribution)
    {
        var peerTxId = new TxId(Enumerable.Repeat((byte)0x77, 32).ToArray());
        var peerSpent = new SpentOutput(peerTxId, 3, LightningMoney.Satoshis(300_000), s_peerScript.ToBytes());
        var inputs = new List<InteractiveTxInput>();
        ulong serial = 0;
        foreach (var input in contribution.Inputs)
        {
            inputs.Add(new InteractiveTxInput(serial, InteractiveTxParty.Local, input.PrevTxId, input.PrevTxVout,
                                              input.Sequence, input.Amount, input.ScriptPubKey, input.PrevTx, false));
            serial += 2;
        }

        inputs.Add(new InteractiveTxInput(1, InteractiveTxParty.Remote, peerTxId, 3, 0xFFFFFFFD, peerSpent.Amount,
                                          peerSpent.ScriptPubKey, [0x00], false));
        var outputs = new List<InteractiveTxOutput>
        {
            new(100, InteractiveTxParty.Local, LightningMoney.Satoshis(200_000 + 299_000), s_fundingScript.ToBytes(),
                true)
        };
        foreach (var output in contribution.Outputs)
        {
            outputs.Add(new InteractiveTxOutput(serial, InteractiveTxParty.Local, output.Amount, output.ScriptPubKey,
                                                false));
            serial += 2;
        }

        return (new InteractiveTxBuilder().Build(0, inputs, outputs), peerSpent);
    }

    private static InteractiveTxContributionRequest Request(long walletSat, IReadOnlyList<ContributedOutput>? outputs = null,
                                                            int extraWeight = 0, bool requireConfirmed = false) =>
        new(s_channelId, InteractiveTxPurpose.Splice, LightningMoney.Satoshis(walletSat), outputs ?? [], FeeratePerKw,
            extraWeight, requireConfirmed);

    private static ExtKey GetP2WpkhExtKey(uint index, bool isChange) =>
        s_masterKey.Derive(isChange ? 1u : 0u).Derive(index);

    private static ExtKey GetP2TrExtKey(uint index, bool isChange) =>
        s_masterKey.Derive(isChange ? 3u : 2u).Derive(index);

    private (UtxoModel Model, TxOut TxOut) AddWalletUtxo(AddressType type, uint index, long amountSat,
                                                         uint blockHeight = 100)
    {
        var pubKey = type == AddressType.P2Wpkh
                         ? GetP2WpkhExtKey(index, false).Neuter().PubKey
                         : GetP2TrExtKey(index, false).Neuter().PubKey;
        var address = pubKey.GetAddress(type == AddressType.P2Wpkh
                                            ? ScriptPubKeyType.Segwit
                                            : ScriptPubKeyType.TaprootBIP86, Network.RegTest);

        // The transaction that paid the wallet: a foreign input, a decoy output, then ours at vout 1
        var prevTx = Network.RegTest.CreateTransaction();
        prevTx.Inputs.Add(new TxIn(new OutPoint(RandomUtils.GetUInt256(), 0)));
        prevTx.Outputs.Add(new TxOut(Money.Satoshis(12_345), s_peerScript));
        prevTx.Outputs.Add(new TxOut(Money.Satoshis(amountSat), address.ScriptPubKey));
        var txId = new TxId(prevTx.GetHash().ToBytes());
        _prevTxSource.Transactions[txId] = prevTx.ToBytes();

        var model = new UtxoModel(txId, 1, LightningMoney.Satoshis(amountSat), blockHeight,
                                  new WalletAddressModel(type, index, false, address.ToString()));
        _utxos.Add(model);
        return (model, new TxOut(Money.Satoshis(amountSat), address.ScriptPubKey));
    }

    private sealed class FakePrevTxSource : IWalletPrevTxSource
    {
        public Dictionary<TxId, byte[]> Transactions { get; } = [];

        public Task<byte[]?> GetTransactionAsync(TxId txId, uint blockHeight,
                                                 CancellationToken cancellationToken = default) =>
            Task.FromResult(Transactions.GetValueOrDefault(txId));
    }
}