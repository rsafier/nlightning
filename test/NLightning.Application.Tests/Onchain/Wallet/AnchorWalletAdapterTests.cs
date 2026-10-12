using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Onchain.Wallet;

using Application.Onchain.Anchors;
using Application.Onchain.Resolvers.Local;
using Application.Onchain.Wallet;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Onchain.Models;

/// <summary>
/// The wave O7 adapters from the wallet's fee-input selector and signer (O7-T1) to the CPFP port (O7-T2) and the
/// anchors HTLC port (O7-T3).
/// </summary>
public class AnchorWalletAdapterTests
{
    private const int WalletInputWeight = 272;

    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static readonly TxId s_commitmentTxId = new(Enumerable.Repeat((byte)0x22, 32).ToArray());
    private static readonly byte[] s_changeScript = [0x00, 0x14, .. new byte[20]];

    private readonly Mock<IFeeInputSelector> _selector = new();
    private readonly List<FeeInputReservation> _stored = [];

    public AnchorWalletAdapterTests()
    {
        _selector.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _stored.ToList());
        _selector.Setup(s => s.ReleaseAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .Callback<Guid, CancellationToken>((id, _) => _stored.RemoveAll(r => r.Id == id))
                 .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task Given_CpfpSource_When_Reserving_Then_ChannelPurposeUsedAndOnlyNewInputsReturned()
    {
        // Arrange
        var source = new WalletAnchorFeeInputSource(_selector.Object);
        var earlier = Reservation(WalletAnchorFeeInputSource.GetPurpose(s_channelId), 1);
        _stored.Add(earlier);
        SetupReserve(WalletAnchorFeeInputSource.GetPurpose(s_channelId), Reservation("x", 2, 3));

        // Act
        var reserved = await source.ReserveAsync(s_channelId, 5_000, 2_500, TestContext.Current.CancellationToken);
        var held = await source.GetReservedAsync(s_channelId, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(reserved);
        Assert.Equal(2, reserved.Count);
        Assert.Equal(20_000UL, reserved[0].AmountSat);
        Assert.Equal(WalletInputWeight, reserved[0].InputWeight);
        _selector.Verify(s => s.ReserveAsync(LightningMoney.Satoshis(5_000), LightningMoney.Satoshis(2_500), 0,
                                             "anchor-cpfp:" + s_channelId, It.IsAny<CancellationToken>()));
        Assert.Equal(3, held.Count);
    }

    [Fact]
    public async Task Given_CpfpSource_When_WalletShortOrReleased_Then_NullAndOnlyTheChannelsReservationsReleased()
    {
        // Arrange
        var source = new WalletAnchorFeeInputSource(_selector.Object);
        _selector.Setup(s => s.ReserveAsync(It.IsAny<LightningMoney>(), It.IsAny<LightningMoney>(), It.IsAny<int>(),
                                            It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InsufficientFundsException(LightningMoney.Satoshis(10), LightningMoney.Zero));
        var ours = Reservation(WalletAnchorFeeInputSource.GetPurpose(s_channelId), 1);
        var other = Reservation("anchor-cpfp:other", 2);
        _stored.AddRange([ours, other]);

        // Act
        var reserved = await source.ReserveAsync(s_channelId, 5_000, 2_500, TestContext.Current.CancellationToken);
        await source.ReleaseAsync(s_channelId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(reserved);
        Assert.Equal([other], _stored);
    }

    [Fact]
    public async Task Given_HtlcProvider_When_Selecting_Then_EarlierReservationReplacedAndChangeScriptProvided()
    {
        // Arrange
        var owner = new AnchorFeeInputOwner(s_channelId, s_commitmentTxId, 3);
        var purpose = WalletAnchorFeeInputProvider.GetPurpose(owner);
        var earlier = Reservation(purpose, 9);
        _stored.Add(earlier);
        SetupReserve(purpose, Reservation("x", 4));
        var destination = new Mock<ISweepDestinationProvider>();
        destination.Setup(d => d.GetDestinationScriptAsync(It.IsAny<CancellationToken>())).ReturnsAsync(s_changeScript);
        var provider = new WalletAnchorFeeInputProvider(_selector.Object, Mock.Of<ILightningSigner>(),
                                                        destination.Object);

        // Act
        var selection = await provider.SelectAsync(owner, 700, 253, TestContext.Current.CancellationToken);

        // Assert: the earlier reservation went back first, then the base weight was charged at the feerate
        Assert.NotNull(selection);
        Assert.DoesNotContain(earlier, _stored);
        var input = Assert.Single(selection.Inputs);
        Assert.Equal(4U, input.Vout);
        Assert.Equal(s_changeScript, selection.ChangeScript);
        Assert.True(purpose.Length <= 128);
        _selector.Verify(s => s.ReserveAsync(LightningMoney.Zero, LightningMoney.Satoshis(253), 700, purpose,
                                             It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Given_HtlcProvider_When_Signing_Then_TheReservationThatHoldsTheInputsIsSignedWithTheHtlcPrevout()
    {
        // Arrange
        var owner = new AnchorFeeInputOwner(s_channelId, s_commitmentTxId, 3);
        var reservation = Reservation(WalletAnchorFeeInputProvider.GetPurpose(owner), 5, 6);
        _stored.AddRange([Reservation(WalletAnchorFeeInputSource.GetPurpose(s_channelId), 7), reservation]);
        var signer = new Mock<ILightningSigner>();
        IReadOnlyList<SpentOutput>? passed = null;
        signer.Setup(s => s.SignWalletTransaction(It.IsAny<SignedTransaction>(), reservation.Id,
                                                  It.IsAny<IReadOnlyList<SpentOutput>>()))
              .Callback<SignedTransaction, Guid, IReadOnlyList<SpentOutput>>((tx, _, others) =>
               {
                   passed = others;
                   tx.RawTxBytes = [9, 9];
               })
              .Returns(true);
        var provider = new WalletAnchorFeeInputProvider(_selector.Object, signer.Object,
                                                        Mock.Of<ISweepDestinationProvider>());
        var htlcInput = new SpentOutput(s_commitmentTxId, 3, LightningMoney.Satoshis(30_000), s_changeScript);
        var feeInputs = reservation.Inputs.Select(i => new AnchorFeeInput(i.TxId, i.Index, 1, [], 273)).ToList();
        var unsigned = new SignedTransaction(s_commitmentTxId, [1, 2]);

        // Act
        var signed = await provider.SignAsync(unsigned, feeInputs, htlcInput, TestContext.Current.CancellationToken);

        // Assert: signed on a copy, with the HTLC prevout for P2TR inputs
        Assert.Equal(new byte[] { 9, 9 }, signed.RawTxBytes);
        Assert.Equal(new byte[] { 1, 2 }, unsigned.RawTxBytes);
        Assert.Equal([htlcInput], passed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SignAsync(
            unsigned, [new AnchorFeeInput(s_commitmentTxId, 99, 1, [], 273)], htlcInput,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Given_Registrations_When_SelectorMissingOrPresent_Then_FallbackOrWalletAdapters()
    {
        // Arrange
        var without = new ServiceCollection();
        without.AddAnchorWalletServices();
        var with = new ServiceCollection();
        with.AddSingleton(_selector.Object);
        with.AddSingleton(Mock.Of<ILightningSigner>());
        with.AddSingleton(Mock.Of<ISweepDestinationProvider>());
        with.AddAnchorWalletServices();

        // Act
        using var withoutProvider = without.BuildServiceProvider();
        using var withProvider = with.BuildServiceProvider();

        // Assert
        Assert.IsType<UnavailableAnchorFeeInputProvider>(withoutProvider.GetRequiredService<IAnchorFeeInputProvider>());
        Assert.Null(withoutProvider.GetService<IAnchorFeeInputSource>());
        Assert.IsType<WalletAnchorFeeInputProvider>(withProvider.GetRequiredService<IAnchorFeeInputProvider>());
        Assert.IsType<WalletAnchorFeeInputSource>(withProvider.GetRequiredService<IAnchorFeeInputSource>());
    }

    private void SetupReserve(string purpose, FeeInputReservation template) =>
        _selector.Setup(s => s.ReserveAsync(It.IsAny<LightningMoney>(), It.IsAny<LightningMoney>(), It.IsAny<int>(),
                                            purpose, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(() =>
                  {
                      var reservation = new FeeInputReservation(Guid.NewGuid(), purpose, template.Inputs, template.Fee,
                                                                template.ChangeAmount, template.ChangeScript);
                      _stored.Add(reservation);
                      return reservation;
                  });

    private static FeeInputReservation Reservation(string purpose, params uint[] indexes) =>
        new(Guid.NewGuid(), purpose,
            indexes.Select(i => new WalletInput(new TxId(Enumerable.Repeat((byte)0x33, 32).ToArray()), i,
                                                LightningMoney.Satoshis(20_000), AddressType.P2Wpkh, s_changeScript,
                                                WalletInputWeight)).ToList(),
            LightningMoney.Satoshis(500), LightningMoney.Zero, null);
}