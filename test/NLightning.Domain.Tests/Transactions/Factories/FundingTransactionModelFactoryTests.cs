using NLightning.Tests.Utils.Channels;

namespace NLightning.Domain.Tests.Transactions.Factories;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;

public class FundingTransactionModelFactoryTests
{
    // One P2WPKH input: weight = 40 (base) + 41 * 4 + 107 (input) + 172 (P2WSH funding output) = 483
    // At 2500 sat/kw: 1208 sat without change; +124 weight (P2WPKH change) = 607 -> 1518 sat with change
    private const long FeeWithoutChangeSat = 1_208;
    private const long FeeWithChangeSat = 1_518;
    private const long DustLimitSat = 294;

    private static readonly LightningMoney s_fundingAmount = LightningMoney.Satoshis(100_000);

    private static readonly CompactPubKey s_pubKey = new([
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01
    ]);

    private readonly WalletAddressModel _changeAddress = new(AddressType.P2Wpkh, 0, true, "change");
    private readonly FundingTransactionModelFactory _factory = new();

    private static ChannelModel CreateChannel()
    {
        var channelConfig = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Satoshis(2_500), LightningMoney.Zero,
                                              LightningMoney.Zero, 0, LightningMoney.Zero, 0, false,
                                              LightningMoney.Zero, 144, FeatureSupport.No);
        var fundingOutputInfo = new FundingOutputInfo(s_fundingAmount, s_pubKey, s_pubKey);
        var keySet = new ChannelKeySetModel(0, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey, s_pubKey);

        return new ChannelModel(channelConfig, ChannelId.Zero, null, fundingOutputInfo, true, null, null,
                                s_fundingAmount, keySet, 0, 0, LightningMoney.Zero, keySet, 0, s_pubKey, 0,
                                ChannelState.V1Opening, ChannelVersion.V1);
    }

    private static List<UtxoModel> CreateUtxos(long satoshis) =>
    [
        new(new byte[32], 0, LightningMoney.Satoshis(satoshis), 100, 0, false, AddressType.P2Wpkh)
    ];

    [Fact]
    public void Given_InputsBelowFundingPlusFee_When_Creating_Then_ThrowsInsufficientFundsException()
    {
        // Arrange
        var utxos = CreateUtxos(100_000 + FeeWithoutChangeSat - 1);

        // Act / Assert
        var exception =
            Assert.Throws<InsufficientFundsException>(() => _factory.Create(CreateChannel(), utxos, _changeAddress));
        Assert.Equal(LightningMoney.Satoshis(100_000 + FeeWithoutChangeSat), exception.Required);
        Assert.Equal(LightningMoney.Satoshis(100_000 + FeeWithoutChangeSat - 1), exception.Available);
    }

    [Fact]
    public void Given_LeftoverBelowChangeFeePlusDust_When_Creating_Then_NoChangeAndLeftoverGoesToFee()
    {
        // Arrange: the change would be 1 sat below the dust limit
        var leftover = FeeWithChangeSat + DustLimitSat - 1;
        var utxos = CreateUtxos(100_000 + leftover);

        // Act
        var model = _factory.Create(CreateChannel(), utxos, _changeAddress);

        // Assert
        Assert.Null(model.ChangeAddress);
        Assert.Null(model.ChangeAmount);
        Assert.Equal(LightningMoney.Satoshis(leftover), model.Fee);
    }

    [Fact]
    public void Given_LeftoverAtChangeFeePlusDust_When_Creating_Then_ChangeIsDustLimit()
    {
        // Arrange
        var utxos = CreateUtxos(100_000 + FeeWithChangeSat + DustLimitSat);

        // Act
        var model = _factory.Create(CreateChannel(), utxos, _changeAddress);

        // Assert
        Assert.Same(_changeAddress, model.ChangeAddress);
        Assert.Equal(LightningMoney.Satoshis(DustLimitSat), model.ChangeAmount);
    }

    [Fact]
    public void Given_LargeLeftover_When_Creating_Then_FeeIncludesChangeOutputWeight()
    {
        // Arrange
        var utxos = CreateUtxos(150_000);

        // Act
        var model = _factory.Create(CreateChannel(), utxos, _changeAddress);

        // Assert
        Assert.Equal(LightningMoney.Satoshis(FeeWithChangeSat), model.Fee);
        Assert.Equal(LightningMoney.Satoshis(50_000 - FeeWithChangeSat), model.ChangeAmount);
        Assert.Equal(LightningMoney.Satoshis(150_000), s_fundingAmount + model.Fee + model.ChangeAmount!);
    }
}