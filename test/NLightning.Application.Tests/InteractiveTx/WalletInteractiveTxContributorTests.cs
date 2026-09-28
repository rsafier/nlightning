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
using Domain.Protocol.InteractiveTx.Interfaces;
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
/// <see cref="PrevTxInspector"/>, <see cref="InteractiveTxTransactionParser"/> and <see cref="InteractiveTxBuilder"/>,
/// with a mocked unit of work (reservations and stored negotiations) and an in-memory source of the wallet's previous
/// transactions. Every signature is checked with NBitcoin's script interpreter.
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
    private readonly Mock<IInteractiveTxSessionDbRepository> _sessions = new();
    private readonly List<InteractiveTxSessionModel> _storedSessions = [];
    private readonly FakePrevTxSource _prevTxSource = new();
    private readonly List<FeeInputReservation> _stored = [];
    private readonly BitcoinAddress _changeAddress;
    private readonly IFeeInputSelector _selector;
    private readonly LocalLightningSigner _signer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WalletInteractiveTxContributor _contributor;
    private long _reserveSat;

    public WalletInteractiveTxContributorTests()
    {
        _changeAddress = GetP2WpkhExtKey(50, true).Neuter().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);

        _unitOfWork.Setup(u => u.FeeInputReservationDbRepository).Returns(_reservations.Object);
        _unitOfWork.Setup(u => u.BroadcastTransactionDbRepository).Returns(_broadcasts.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);
        _unitOfWork.Setup(u => u.InteractiveTxSessionDbRepository).Returns(_sessions.Object);
        _sessions.Setup(r => r.GetUnresolvedAsync()).ReturnsAsync(() => _storedSessions.ToList());
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
        _scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var nodeOptions = new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest };

        _selector = new Infrastructure.Bitcoin.Wallet.FeeInputSelector(
            _utxos, provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(nodeOptions),
            NullLogger<Infrastructure.Bitcoin.Wallet.FeeInputSelector>.Instance);
        _signer = new LocalLightningSigner(Mock.Of<IFundingOutputBuilder>(), Mock.Of<IKeyDerivationService>(),
                                           NullLogger<LocalLightningSigner>.Instance, nodeOptions, _keyManager.Object,
                                           _utxos);

        // Without a session store: the in-process guard only (a durable one is made by CreateDurableContributor)
        _contributor = new WalletInteractiveTxContributor(_selector, _signer, _utxos, _prevTxSource,
                                                          new PrevTxInspector(), new InteractiveTxTransactionParser(),
                                                          anchorReserveService: _anchorReserve.Object);
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

    [Fact]
    public void Given_TheServiceCollection_When_AddingTheContributor_Then_ItIsOneSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(_selector);
        services.AddSingleton<ILightningSigner>(_signer);
        services.AddSingleton<IUtxoMemoryRepository>(_utxos);
        services.AddSingleton<IWalletPrevTxSource>(_prevTxSource);
        services.AddSingleton<IPrevTxInspector>(new PrevTxInspector());
        services.AddSingleton<IInteractiveTxTransactionParser>(new InteractiveTxTransactionParser());

        // Act
        services.AddInteractiveTxContributorServices();
        services.AddInteractiveTxContributorServices();
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.Same(provider.GetRequiredService<WalletInteractiveTxContributor>(),
                    provider.GetRequiredService<IInteractiveTxContributor>());
        Assert.Same(provider.GetRequiredService<IInteractiveTxContributor>(),
                    provider.GetRequiredService<IInteractiveTxContributor>());
    }

    [Fact]
    public async Task Given_AWalletOutputWhosePrevTxIsTooLarge_When_Contributing_Then_AnotherIsUsedAndItIsReleased()
    {
        // Arrange: the largest output (picked first) was paid by a transaction larger than a tx_add_input can carry
        var oversized = AddWalletUtxo(AddressType.P2Wpkh, 0, 900_000,
                                      prevTxSize: WalletInteractiveTxContributor.MaxPrevTxLength + 1);
        var usable = AddWalletUtxo(AddressType.P2Wpkh, 1, 400_000);

        // Act
        var contribution = await _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);

        // Assert
        var input = Assert.Single(contribution.Inputs);
        Assert.Equal(usable.Model.TxId, input.PrevTxId);
        Assert.True(input.PrevTx.Length <= WalletInteractiveTxContributor.MaxPrevTxLength);
        Assert.Equal(contribution.ReservationId, Assert.Single(_stored).Id);
        Assert.False(_utxos.TryGetFeeReservation(oversized.Model.TxId, oversized.Model.Index, out _));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Given_APrevTxAtTheTxAddInputLimit_When_Contributing_Then_OnlyUpToTheLimitIsContributed(
        int bytesOverTheLimit, bool contributed)
    {
        // Arrange: 65,535 - 52 = 65,483 bytes fit in a tx_add_input; the u16 prevtx_len must never wrap
        Assert.Equal(65_483, WalletInteractiveTxContributor.MaxPrevTxLength);
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000,
                                 prevTxSize: WalletInteractiveTxContributor.MaxPrevTxLength + bytesOverTheLimit);

        // Act
        var contribute = () => _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);

        // Assert
        if (contributed)
        {
            var input = Assert.Single((await contribute()).Inputs);
            Assert.Equal(WalletInteractiveTxContributor.MaxPrevTxLength, input.PrevTx.Length);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(contribute);
            Assert.Empty(_stored);
            Assert.False(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
        }
    }

    [Fact]
    public async Task Given_ASessionStoreWithoutTheNegotiation_When_Signing_Then_NothingIsSignedAndReleaseWorks()
    {
        // Arrange: the driver did not store the negotiation (with our commitment_signed) before asking to sign
        var contributor = CreateDurableContributor();
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var contribution = await contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);
        var (constructed, peerSpent) = Construct(contribution);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => contributor.SignAsync(constructed, contribution, [peerSpent], TestContext.Current.CancellationToken));
        await contributor.ReleaseAsync(contribution, TestContext.Current.CancellationToken);
        Assert.Empty(_stored);
        Assert.False(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
    }

    [Fact]
    public async Task Given_OurSignaturesAndAStoredNegotiation_When_ReleasingAfterARestart_Then_TheReservationIsKept()
    {
        // Arrange: the negotiation is stored, we sign, then the process restarts (a new contributor, no memory)
        var contributor = CreateDurableContributor();
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var contribution = await contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);
        var (constructed, peerSpent) = Construct(contribution);
        _storedSessions.Add(StoredSession(contribution, InteractiveTxSessionState.AwaitingTxSignatures));
        await contributor.SignAsync(constructed, contribution, [peerSpent], TestContext.Current.CancellationToken);
        _storedSessions[0] = _storedSessions[0] with { State = InteractiveTxSessionState.TxSignaturesSent };
        var restarted = CreateDurableContributor();

        // Act
        await restarted.ReleaseAsync(contribution, TestContext.Current.CancellationToken);
        var released = await restarted.ReleaseOrphanedReservationsAsync(TestContext.Current.CancellationToken);

        // Assert (IT-ABT-01)
        Assert.Equal(0, released);
        Assert.Single(_stored);
        Assert.True(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
    }

    [Fact]
    public async Task Given_AStoredNegotiationMarkedAborted_When_Releasing_Then_TheOutputsReturnToTheWallet()
    {
        // Arrange: stored at our commitment_signed, then tx_abort before our tx_signatures
        var contributor = CreateDurableContributor();
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var contribution = await contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);
        _storedSessions.Add(StoredSession(contribution, InteractiveTxSessionState.AwaitingTxSignatures));

        // Act: refused while stored and not aborted, done once aborted
        await contributor.ReleaseAsync(contribution, TestContext.Current.CancellationToken);
        var keptWhileStored = _stored.Count;
        _storedSessions[0] = _storedSessions[0] with { State = InteractiveTxSessionState.Aborted };
        await contributor.ReleaseAsync(contribution, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, keptWhileStored);
        Assert.Empty(_stored);
        Assert.False(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
    }

    [Fact]
    public async Task Given_ReservationsLeftByACrash_When_SweepingAtStartup_Then_OnlyOrphanedNegotiationsAreReleased()
    {
        // Arrange: two negotiations reserved, one of them stored; a withdraw reservation of another purpose
        var contributor = CreateDurableContributor();
        var orphanUtxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var storedUtxo = AddWalletUtxo(AddressType.P2Wpkh, 1, 400_000);
        AddWalletUtxo(AddressType.P2Wpkh, 2, 300_000);
        var orphan = await contributor.ContributeAsync(Request(300_000), TestContext.Current.CancellationToken);
        var kept = await contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);
        _storedSessions.Add(StoredSession(kept, InteractiveTxSessionState.AwaitingCommitmentSigned));
        var withdraw = await _selector.ReserveAsync(LightningMoney.Satoshis(100_000), LightningMoney.Satoshis(253), 0,
                                                    "withdraw", TestContext.Current.CancellationToken);
        var restarted = CreateDurableContributor();

        // Act
        var released = await restarted.ReleaseOrphanedReservationsAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, released);
        Assert.Equal(orphanUtxo.Model.TxId, Assert.Single(orphan.Inputs).PrevTxId);
        Assert.False(_utxos.TryGetFeeReservation(orphanUtxo.Model.TxId, orphanUtxo.Model.Index, out _));
        Assert.True(_utxos.TryGetFeeReservation(storedUtxo.Model.TxId, storedUtxo.Model.Index, out _));
        Assert.Equal(2, _stored.Count);
        Assert.Contains(_stored, r => r.Id == kept.ReservationId);
        Assert.Contains(_stored, r => r.Id == withdraw.Id);
    }

    [Fact]
    public async Task Given_ReleaseAndSignAtOnce_When_BothRun_Then_TheReservationOutlivesEverySignature()
    {
        for (uint i = 0; i < 8; i++)
        {
            // Arrange
            AddWalletUtxo(AddressType.P2Wpkh, i, 500_000 + i);
            var contribution = await _contributor.ContributeAsync(Request(200_000),
                                                                  TestContext.Current.CancellationToken);
            var (constructed, peerSpent) = Construct(contribution);

            // Act
            var sign = Task.Run(() => _contributor.SignAsync(constructed, contribution, [peerSpent],
                                                             TestContext.Current.CancellationToken),
                                TestContext.Current.CancellationToken);
            var release = Task.Run(() => _contributor.ReleaseAsync(contribution,
                                                                   TestContext.Current.CancellationToken),
                                   TestContext.Current.CancellationToken);
            await release;
            var signed = true;
            try
            {
                await sign;
            }
            catch (Exception e) when (e is InvalidOperationException or SignerException)
            {
                // Released first: the signer no longer holds the reservation
                signed = false;
            }

            // Assert: signed exactly when the reservation is still held
            Assert.Equal(signed, _stored.Any(r => r.Id == contribution.ReservationId));
        }
    }

    [Theory]
    [InlineData("txid")]
    [InlineData("locktime")]
    [InlineData("sequence")]
    public async Task Given_BytesThatAreNotTheDescribedTransaction_When_Signing_Then_NothingIsSigned(string tampered)
    {
        // Arrange: the metadata is right, the bytes (and maybe their txid) are another transaction's
        AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var contribution = await _contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);
        var (constructed, peerSpent) = Construct(contribution);
        var builder = new InteractiveTxBuilder();
        var other = tampered == "sequence"
                        ? builder.Build(constructed.Locktime,
                                        constructed.Inputs.Select(i => i with { Sequence = 0xFFFFFFFE }).ToList(),
                                        constructed.Outputs)
                        : builder.Build(constructed.Locktime + 1, constructed.Inputs, constructed.Outputs);
        var described = tampered == "txid"
                            ? constructed with { UnsignedTx = other.UnsignedTx }
                            : constructed with { UnsignedTx = other.UnsignedTx, TxId = other.TxId };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _contributor.SignAsync(described, contribution, [peerSpent], TestContext.Current.CancellationToken));
        await _contributor.ReleaseAsync(contribution, TestContext.Current.CancellationToken);
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Given_ASignedAttemptThatCanNoLongerConfirm_When_ReleasingItAsDiscarded_Then_ItsOutputsReturnOnce()
    {
        // Arrange (NL-492): signed and stored, so an ordinary release refuses (IT-ABT-01); the process then restarts
        var contributor = CreateDurableContributor();
        var utxo = AddWalletUtxo(AddressType.P2Wpkh, 0, 500_000);
        var contribution = await contributor.ContributeAsync(Request(200_000), TestContext.Current.CancellationToken);
        var (constructed, peerSpent) = Construct(contribution);
        _storedSessions.Add(StoredSession(contribution, InteractiveTxSessionState.AwaitingTxSignatures));
        await contributor.SignAsync(constructed, contribution, [peerSpent], TestContext.Current.CancellationToken);
        await contributor.ReleaseAsync(contribution, TestContext.Current.CancellationToken);
        var keptBySignature = _stored.Count;
        var restarted = CreateDurableContributor();

        // Act: the commitment that conflicts with it is irrevocable
        var released = await restarted.ReleaseDiscardedAsync(constructed, [], TestContext.Current.CancellationToken);
        var again = await restarted.ReleaseDiscardedAsync(constructed, [], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, keptBySignature);
        Assert.Equal(1, released);
        Assert.Equal(0, again);
        Assert.Empty(_stored);
        Assert.False(_utxos.TryGetFeeReservation(utxo.Model.TxId, utxo.Model.Index, out _));
        Assert.Contains(_utxos.GetUnreservedUtxos(), u => u.TxId == utxo.Model.TxId);
    }

    [Fact]
    public async Task Given_AnInputTheWinnerSpent_When_ReleasingTheDiscardedAttempt_Then_KeptUntilTheWalletDropsIt()
    {
        // Arrange: one reservation of two outputs; the locked sibling re-added the first one
        var first = AddWalletUtxo(AddressType.P2Wpkh, 0, 150_000);
        var second = AddWalletUtxo(AddressType.P2Wpkh, 1, 150_000);
        var contribution = await _contributor.ContributeAsync(Request(250_000), TestContext.Current.CancellationToken);
        Assert.Equal(2, contribution.Inputs.Count);
        var (constructed, _) = Construct(contribution);
        (TxId, uint)[] kept = [(first.Model.TxId, first.Model.Index)];

        // Act: while the wallet still holds the kept output, nothing; once the winner's block spent it, the rest
        var whileHeld = await _contributor.ReleaseDiscardedAsync(constructed, kept,
                                                                 TestContext.Current.CancellationToken);
        var reservedWhileHeld = _stored.Count;
        _utxos.Spend(first.Model);
        var afterSpend = await _contributor.ReleaseDiscardedAsync(constructed, kept,
                                                                  TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, whileHeld);
        Assert.Equal(1, reservedWhileHeld);
        Assert.Equal(1, afterSpend);
        Assert.Empty(_stored);
        Assert.False(_utxos.TryGetFeeReservation(second.Model.TxId, second.Model.Index, out _));
    }

    [Fact]
    public async Task Given_AReservationWithOutputsOfAnotherSpend_When_ReleasingADiscardedAttempt_Then_ItIsKept()
    {
        // Arrange: the discarded attempt carries only one of the reservation's two outputs
        AddWalletUtxo(AddressType.P2Wpkh, 0, 150_000);
        AddWalletUtxo(AddressType.P2Wpkh, 1, 150_000);
        var contribution = await _contributor.ContributeAsync(Request(250_000), TestContext.Current.CancellationToken);
        var partial = new InteractiveTxContribution([contribution.Inputs[0]], contribution.Outputs,
                                                    contribution.ReservationId);
        var (constructed, _) = Construct(partial);

        // Act
        var released = await _contributor.ReleaseDiscardedAsync(constructed, [], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, released);
        Assert.Single(_stored);
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

    private static InteractiveTxSessionModel StoredSession(InteractiveTxContribution contribution,
                                                           InteractiveTxSessionState state) =>
        new()
        {
            ChannelId = s_channelId,
            SessionId = Guid.NewGuid(),
            Purpose = InteractiveTxPurpose.Splice,
            IsInitiator = true,
            FeeratePerKw = FeeratePerKw,
            Locktime = 0,
            Inputs = [],
            Outputs = [],
            LocalContribution = contribution,
            State = state,
            CreatedAt = DateTimeOffset.UnixEpoch
        };

    /// <summary>A contributor with the session store, as the production registration builds it.</summary>
    private WalletInteractiveTxContributor CreateDurableContributor() =>
        new(_selector, _signer, _utxos, _prevTxSource, new PrevTxInspector(), new InteractiveTxTransactionParser(),
            _scopeFactory, _anchorReserve.Object);

    private static InteractiveTxContributionRequest Request(long walletSat, IReadOnlyList<ContributedOutput>? outputs = null,
                                                            int extraWeight = 0, bool requireConfirmed = false) =>
        new(s_channelId, InteractiveTxPurpose.Splice, LightningMoney.Satoshis(walletSat), outputs ?? [], FeeratePerKw,
            extraWeight, requireConfirmed);

    private static ExtKey GetP2WpkhExtKey(uint index, bool isChange) =>
        s_masterKey.Derive(isChange ? 1u : 0u).Derive(index);

    private static ExtKey GetP2TrExtKey(uint index, bool isChange) =>
        s_masterKey.Derive(isChange ? 3u : 2u).Derive(index);

    private (UtxoModel Model, TxOut TxOut) AddWalletUtxo(AddressType type, uint index, long amountSat,
                                                         uint blockHeight = 100, int? prevTxSize = null)
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
        if (prevTxSize is { } size)
        {
            // A third output whose script pads the transaction to exactly that size (a 3-byte script length)
            prevTx.Outputs.Add(new TxOut(Money.Zero, Script.Empty));
            var scriptLength = size - prevTx.ToBytes().Length - 2;
            Assert.InRange(scriptLength, 0xFD, 0xFFFF);
            prevTx.Outputs[2].ScriptPubKey = new Script(new byte[scriptLength]);
            Assert.Equal(size, prevTx.ToBytes().Length);
        }

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