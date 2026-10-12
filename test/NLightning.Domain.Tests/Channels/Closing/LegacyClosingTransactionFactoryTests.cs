namespace NLightning.Domain.Tests.Channels.Closing;

using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// BOLT 3 legacy closing transaction content (B3-LCTX-01): rounding, fee from the funder, dust removal, variants.
/// </summary>
public class LegacyClosingTransactionFactoryTests
{
    private static readonly BitcoinScript s_localScript = Convert.FromHexString("0014" + new string('1', 40));
    private static readonly BitcoinScript s_remoteScript = Convert.FromHexString("0020" + new string('2', 64));

    private static readonly FundingOutputInfo s_funding =
        new(LightningMoney.Satoshis(1_000_000),
            new CompactPubKey(Convert.FromHexString(
                                  "023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb")),
            new CompactPubKey(Convert.FromHexString(
                                  "030e9f7b623d2ccc7c9bd44d66d5ce21ce504c0acf6385a132cec6d3c39fa711c1")),
            new TxId(new byte[32]), 0);

    [Fact]
    public void Given_LocalFunder_When_Create_Then_FeeTakenFromLocalAndMsatRoundedDown()
    {
        // Arrange / Act
        var model = LegacyClosingTransactionFactory.Create(s_funding, 700_000_999, 299_999_001, true, 1_000,
                                                           s_localScript, s_remoteScript, 546);

        // Assert
        Assert.Equal(LightningMoney.Satoshis(699_000), model.LocalOutput!.Amount);
        Assert.Equal(LightningMoney.Satoshis(299_999), model.RemoteOutput!.Amount);
        Assert.Equal(s_localScript, model.LocalOutput.ScriptPubKey);
        Assert.Equal(s_remoteScript, model.RemoteOutput.ScriptPubKey);
        Assert.Equal(LightningMoney.Satoshis(1_000), model.Fee);
    }

    [Fact]
    public void Given_RemoteFunder_When_Create_Then_FeeTakenFromRemote()
    {
        // Arrange / Act
        var model = LegacyClosingTransactionFactory.Create(s_funding, 400_000_000, 600_000_000, false, 2_500,
                                                           s_localScript, s_remoteScript, 546);

        // Assert
        Assert.Equal(LightningMoney.Satoshis(400_000), model.LocalOutput!.Amount);
        Assert.Equal(LightningMoney.Satoshis(597_500), model.RemoteOutput!.Amount);
    }

    [Theory]
    [InlineData(545UL, false)]
    [InlineData(546UL, true)]
    public void Given_OutputAroundDustLimit_When_Create_Then_RemovedBelowLimit(ulong remoteSat, bool kept)
    {
        // Arrange
        var remoteMsat = remoteSat * 1000;

        // Act
        var model = LegacyClosingTransactionFactory.Create(s_funding, 1_000_000_000 - remoteMsat, remoteMsat, true,
                                                           500, s_localScript, s_remoteScript, 546);

        // Assert
        Assert.Equal(kept, model.RemoteOutput is not null);
        Assert.NotNull(model.LocalOutput);
    }

    [Fact]
    public void Given_ZeroBalanceNonFunder_When_Create_Then_SingleOutput()
    {
        // Act
        var model = LegacyClosingTransactionFactory.Create(s_funding, 1_000_000_000, 0, true, 700, s_localScript,
                                                           s_remoteScript, 0);

        // Assert
        var output = Assert.Single(model.Outputs);
        Assert.True(output.IsLocal);
        Assert.Equal(LightningMoney.Satoshis(999_300), output.Amount);
    }

    [Fact]
    public void Given_Variants_When_Create_Then_OwnOutputRemoved()
    {
        // Act
        var withoutLocal = LegacyClosingTransactionFactory.Create(s_funding, 500_000_000, 500_000_000, true, 500,
                                                                  s_localScript, s_remoteScript, 546,
                                                                  ClosingVariant.WithoutLocalOutput);
        var withoutRemote = LegacyClosingTransactionFactory.Create(s_funding, 500_000_000, 500_000_000, true, 500,
                                                                   s_localScript, s_remoteScript, 546,
                                                                   ClosingVariant.WithoutRemoteOutput);

        // Assert
        Assert.False(Assert.Single(withoutLocal.Outputs).IsLocal);
        Assert.True(Assert.Single(withoutRemote.Outputs).IsLocal);
    }

    [Fact]
    public void Given_FeeAboveFunderBalance_When_Create_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyClosingTransactionFactory.Create(
                                                       s_funding, 1_000_999, 998_999_001, true, 1_001,
                                                       s_localScript, s_remoteScript, 546));
        Assert.Equal(1_000UL, LegacyClosingTransactionFactory.MaxFeeSat(1_000_999, 998_999_001, true));
    }

    [Fact]
    public void Given_EveryOutputBelowDust_When_Create_Then_Throws()
    {
        // Arrange: a 1000 sat channel with a 546 sat dust limit and a 100 sat fee: 450 / 450
        var funding = new FundingOutputInfo(LightningMoney.Satoshis(1_000), s_funding.LocalFundingPubKey,
                                            s_funding.RemoteFundingPubKey, new TxId(new byte[32]), 0);

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => LegacyClosingTransactionFactory.Create(
                                                     funding, 550_000, 450_000, true, 100, s_localScript,
                                                     s_remoteScript, 546));
    }
}