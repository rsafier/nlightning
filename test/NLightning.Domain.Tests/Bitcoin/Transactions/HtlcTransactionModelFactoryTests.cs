using NLightning.Tests.Utils.Mocks;

namespace NLightning.Domain.Tests.Bitcoin.Transactions;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Models;

public class HtlcTransactionModelFactoryTests
{
    private static readonly CompactPubKey s_keyA =
        Convert.FromHexString("030d417a46946384f88d5f3337267c5e579765875dc4daca813e21734b140639e7");

    private static readonly CompactPubKey s_keyB =
        Convert.FromHexString("0394854aa6eab5b2a8122cc726e9dded053a2184d88256816826d6231c068d4a5b");

    private static readonly CompactPubKey s_delayedKey =
        Convert.FromHexString("03fd5960528dc152014952efdb702a88f71e3c1653b2314431701ec77e57fde83c");

    private static readonly CompactPubKey s_revocationKey =
        Convert.FromHexString("0212a140cd0c6539d07cd08dfe09984dec3251ea808b892efeac3ede9402bf2b19");

    private static readonly TxId s_commitmentTxId = new byte[32];

    [Fact]
    public void Given_BoltExampleFeerate_When_CreatingTimeoutTx_Then_Fee3315AndLockTimeIsCltv()
    {
        // Arrange - BOLT 3 fee example: feerate 5000, offered HTLC of 5000 sat
        var commitment = CreateCommitment(5_000, false);
        var output = new OfferedHtlcOutputInfo(CreateHtlc(5_000_000, 600), s_keyA, s_keyB, s_revocationKey);

        // Act
        var model = HtlcTransactionModelFactory.CreateHtlcTransactionModel(commitment, s_commitmentTxId, output, 3);

        // Assert
        Assert.Equal(HtlcTransactionType.Timeout, model.Type);
        Assert.Equal(LightningMoney.Satoshis(3_315), model.Fee);
        Assert.Equal(LightningMoney.Satoshis(5_000 - 3_315), model.OutputAmount);
        Assert.Equal(600U, (uint)model.LockTime);
        Assert.Equal(0U, (uint)model.Sequence);
        Assert.Equal(3U, model.CommitmentOutputIndex);
        Assert.Equal(s_delayedKey, model.LocalDelayedPubKey);
        Assert.Equal(s_revocationKey, model.RevocationPubKey);
        Assert.Equal((ushort)144, model.ToSelfDelay);
    }

    [Fact]
    public void Given_BoltExampleFeerate_When_CreatingSuccessTx_Then_Fee3515AndLockTimeZero()
    {
        // Arrange - BOLT 3 fee example: received HTLC of 7000 sat
        var commitment = CreateCommitment(5_000, false);
        var output = new ReceivedHtlcOutputInfo(CreateHtlc(7_000_000, 600), s_keyA, s_keyB, s_revocationKey);

        // Act
        var model = HtlcTransactionModelFactory.CreateHtlcTransactionModel(commitment, s_commitmentTxId, output, 0);

        // Assert
        Assert.Equal(HtlcTransactionType.Success, model.Type);
        Assert.Equal(LightningMoney.Satoshis(3_515), model.Fee);
        Assert.Equal(LightningMoney.Satoshis(7_000 - 3_515), model.OutputAmount);
        Assert.Equal(0U, (uint)model.LockTime);
    }

    [Fact]
    public void Given_Anchors_When_CreatingHtlcTx_Then_ZeroFeeAndSequenceOne()
    {
        // Arrange
        var commitment = CreateCommitment(5_000, true);
        var output = new ReceivedHtlcOutputInfo(CreateHtlc(7_000_999, 600), s_keyA, s_keyB, s_revocationKey);

        // Act
        var model = HtlcTransactionModelFactory.CreateHtlcTransactionModel(commitment, s_commitmentTxId, output, 0);

        // Assert - amount rounded down, no fee
        Assert.Equal(LightningMoney.Zero, model.Fee);
        Assert.Equal(LightningMoney.Satoshis(7_000), model.OutputAmount);
        Assert.Equal(1U, (uint)model.Sequence);
        Assert.True(model.HasAnchors);
    }

    [Fact]
    public void Given_HtlcBelowItsFee_When_CreatingHtlcTx_Then_Throws()
    {
        // Arrange - would have been trimmed by the commitment factory
        var commitment = CreateCommitment(5_000, false);
        var output = new OfferedHtlcOutputInfo(CreateHtlc(1_000_000, 600), s_keyA, s_keyB, s_revocationKey);

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => HtlcTransactionModelFactory.CreateHtlcTransactionModel(
                                                     commitment, s_commitmentTxId, output, 0));
    }

    [Fact]
    public void Given_CommitmentWithoutKeys_When_CreatingHtlcTx_Then_Throws()
    {
        // Arrange
        var commitment = CreateCommitment(5_000, false, withKeys: false);
        var output = new OfferedHtlcOutputInfo(CreateHtlc(9_000_000, 600), s_keyA, s_keyB, s_revocationKey);

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => HtlcTransactionModelFactory.CreateHtlcTransactionModel(
                                                     commitment, s_commitmentTxId, output, 0));
    }

    [Fact]
    public void Given_BuildResultMap_When_CreatingAll_Then_OneModelPerHtlcInTxOrder()
    {
        // Arrange
        var commitment = CreateCommitment(0, false);
        var received = new ReceivedHtlcOutputInfo(CreateHtlc(2_000_000, 501), s_keyA, s_keyB, s_revocationKey);
        var offered = new OfferedHtlcOutputInfo(CreateHtlc(3_000_000, 503), s_keyA, s_keyB, s_revocationKey);
        var txId = new TxId(Enumerable.Repeat((byte)7, 32).ToArray());
        var buildResult = new CommitmentTransactionBuildResult(new SignedTransaction(txId, [1]),
                                                               [(received, 1U), (offered, 4U)]);

        // Act
        var models = HtlcTransactionModelFactory.CreateHtlcTransactionModels(commitment, buildResult);

        // Assert
        Assert.Collection(models,
                          m =>
                          {
                              Assert.Equal(HtlcTransactionType.Success, m.Type);
                              Assert.Equal(1U, m.CommitmentOutputIndex);
                              Assert.Equal(txId, m.CommitmentTxId);
                          },
                          m =>
                          {
                              Assert.Equal(HtlcTransactionType.Timeout, m.Type);
                              Assert.Equal(4U, m.CommitmentOutputIndex);
                          });
    }

    private static Htlc CreateHtlc(ulong amountMsat, uint cltvExpiry) =>
        new(LightningMoney.MilliSatoshis(amountMsat), null!, HtlcDirection.Outgoing, cltvExpiry, 0, 0, new byte[32],
            HtlcState.Offered);

    private static CommitmentTransactionModel CreateCommitment(ulong feeRatePerKw, bool hasAnchors,
                                                               bool withKeys = true)
    {
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), s_keyA, s_keyB)
        {
            TransactionId = Enumerable.Repeat((byte)1, 32).ToArray(),
            Index = 0
        };
        var commitmentNumber = new CommitmentNumber(s_keyA, s_keyB, new FakeSha256());
        return new CommitmentTransactionModel(commitmentNumber, 0, LightningMoney.Zero, fundingOutput)
        {
            FeeRatePerKw = feeRatePerKw,
            HasAnchors = hasAnchors,
            ToSelfDelay = 144,
            LocalDelayedPubKey = withKeys ? s_delayedKey : null,
            RevocationPubKey = withKeys ? s_revocationKey : null
        };
    }
}