namespace NLightning.Domain.Tests.LiquidityAds;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;

public class LiquidityPurchaseModelTests
{
    private static readonly FundingRate s_rate = new(100_000, 1_000_000, 500, 100, 10, 1_000);

    private static readonly CompactPubKey s_peer =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private static readonly DateTimeOffset s_createdAt = new(2026, 10, 3, 1, 2, 3, TimeSpan.Zero);

    #region Construction

    [Fact]
    public void Given_ValidFields_When_Created_Then_PendingWithoutIdOrLease()
    {
        // Arrange
        var signature = Enumerable.Repeat((byte)0x5A, 64).ToArray();
        var script = new byte[] { 0x00, 0x20, 0x01 };

        // Act
        var purchase = new LiquidityPurchaseModel(Channel(1), Txid(2), LiquidityPurchaseRole.Seller,
                                                  LiquidityPurchaseKind.Splice, 400_000, 450_000, s_rate,
                                                  LiquidityPaymentType.FromChannelBalance, 625, 5_010, signature,
                                                  script, s_peer, 4_032, s_createdAt);
        signature[0] = 0;
        script[0] = 0x51;

        // Assert
        Assert.Equal(0, purchase.Id);
        Assert.Equal(Channel(1), purchase.ChannelId);
        Assert.Equal(Txid(2), purchase.FundingTxId);
        Assert.Equal(LiquidityPurchaseRole.Seller, purchase.Role);
        Assert.Equal(LiquidityPurchaseKind.Splice, purchase.Kind);
        Assert.Equal(400_000UL, purchase.RequestedSat);
        Assert.Equal(450_000UL, purchase.ContributedSat);
        Assert.Equal(s_rate, purchase.Rate);
        Assert.Equal(LiquidityPaymentType.FromChannelBalance, purchase.PaymentType);
        Assert.Equal(new LiquidityFees(625, 5_010), purchase.Fees);
        Assert.Equal(5_635_000UL, purchase.TotalFeeMsat);
        Assert.Equal(0x5A, purchase.Signature.Value[0]);
        Assert.Equal(new byte[] { 0x00, 0x20, 0x01 }, purchase.FundingScript);
        Assert.Equal(s_peer, purchase.PeerNodeId);
        Assert.Equal(4_032U, purchase.LeaseBlocks);
        Assert.Equal(s_createdAt, purchase.CreatedAt);
        Assert.Equal(LiquidityPurchaseStatus.Pending, purchase.Status);
        Assert.Null(purchase.LeaseStartHeight);
        Assert.Null(purchase.LeaseEndHeight);
        Assert.Null(purchase.ClosedAtHeight);
        Assert.False(purchase.ClosedEarly);
    }

    [Fact]
    public void Given_ASignatureOtherThan64Bytes_When_Created_Then_Throws()
    {
        // Act & Assert
        // CompactSignature takes 63 bytes (a DER-trimmed form); will_fund carries exactly 64
        Assert.Throws<ArgumentException>(() => Create(signature: new byte[63]));
    }

    [Fact]
    public void Given_AnEmptyFundingScript_When_Created_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => Create(script: []));
    }

    [Fact]
    public void Given_UndefinedEnums_When_Created_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(role: (LiquidityPurchaseRole)0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(kind: (LiquidityPurchaseKind)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(paymentType: (LiquidityPaymentType)1));
    }

    [Fact]
    public void Given_AnAmountAboveTheSignedRange_When_Created_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(requested: (ulong)long.MaxValue + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(miningFee: (ulong)long.MaxValue + 1));
    }

    [Fact]
    public void Given_AMissingPeer_When_Created_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new LiquidityPurchaseModel(
                                             Channel(1), Txid(2), LiquidityPurchaseRole.Buyer,
                                             LiquidityPurchaseKind.ChannelOpen, 1, 1, s_rate,
                                             LiquidityPaymentType.FromChannelBalance, 0, 0, new byte[64], [0x51],
                                             default, 4_032, s_createdAt));
    }

    #endregion

    #region Mutators

    [Fact]
    public void Given_APendingPurchase_When_MarkedActive_Then_TheLeaseStartsThere()
    {
        // Arrange
        var purchase = Create();

        // Act
        purchase.MarkActive(800_000);

        // Assert
        Assert.Equal(LiquidityPurchaseStatus.Active, purchase.Status);
        Assert.Equal(800_000U, purchase.LeaseStartHeight);
        Assert.Equal(804_032U, purchase.LeaseEndHeight);
    }

    [Fact]
    public void Given_AnActivePurchase_When_MarkedActiveAgain_Then_TheStartMoves()
    {
        // Arrange (a reorg confirmed the attempt again at another height)
        var purchase = Create();
        purchase.MarkActive(800_000);

        // Act
        purchase.MarkActive(800_002);

        // Assert
        Assert.Equal(800_002U, purchase.LeaseStartHeight);
    }

    [Fact]
    public void Given_APendingPurchase_When_Replaced_Then_ReplacedAndIdempotent()
    {
        // Arrange
        var purchase = Create();

        // Act
        purchase.MarkReplaced();
        purchase.MarkReplaced();

        // Assert
        Assert.Equal(LiquidityPurchaseStatus.Replaced, purchase.Status);
        Assert.Throws<InvalidOperationException>(() => purchase.MarkActive(1));
        Assert.Throws<InvalidOperationException>(() => purchase.MarkClosed(1));
    }

    [Fact]
    public void Given_AnActivePurchase_When_Replaced_Then_Throws()
    {
        // Arrange
        var purchase = Create();
        purchase.MarkActive(100);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => purchase.MarkReplaced());
    }

    [Theory]
    [InlineData(800_000U, true)]
    [InlineData(804_031U, true)]
    [InlineData(804_032U, false)]
    [InlineData(900_000U, false)]
    public void Given_AnActivePurchase_When_Closed_Then_EarlyOnlyInsideTheLease(uint height, bool expectedEarly)
    {
        // Arrange
        var purchase = Create();
        purchase.MarkActive(800_000);

        // Act
        purchase.MarkClosed(height);

        // Assert
        Assert.Equal(LiquidityPurchaseStatus.Closed, purchase.Status);
        Assert.Equal(height, purchase.ClosedAtHeight);
        Assert.Equal(expectedEarly, purchase.ClosedEarly);
    }

    [Fact]
    public void Given_APendingPurchase_When_Closed_Then_ClosedEarly()
    {
        // Arrange (the channel closed before the attempt confirmed)
        var purchase = Create();

        // Act
        purchase.MarkClosed(900_000);

        // Assert
        Assert.True(purchase.ClosedEarly);
        Assert.Null(purchase.LeaseStartHeight);
    }

    [Fact]
    public void Given_AClosedPurchase_When_ClosedAgain_Then_TheFirstCloseKept()
    {
        // Arrange
        var purchase = Create();
        purchase.MarkActive(800_000);
        purchase.MarkClosed(800_010);

        // Act
        purchase.MarkClosed(900_000);

        // Assert
        Assert.Equal(800_010U, purchase.ClosedAtHeight);
        Assert.True(purchase.ClosedEarly);
        Assert.Throws<InvalidOperationException>(() => purchase.MarkActive(1));
    }

    [Fact]
    public void Given_AnId_When_Assigned_Then_KeptAndNeverReplaced()
    {
        // Arrange
        var purchase = Create();

        // Act
        purchase.AssignId(7);
        purchase.AssignId(7);

        // Assert
        Assert.Equal(7, purchase.Id);
        Assert.Throws<InvalidOperationException>(() => purchase.AssignId(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create().AssignId(0));
    }

    [Fact]
    public void Given_EachStatus_When_LeaseChecked_Then_OnlyPendingAndActiveInsideBind()
    {
        // Arrange
        var pending = Create();
        var active = Create();
        active.MarkActive(100);
        var replaced = Create();
        replaced.MarkReplaced();
        var closed = Create();
        closed.MarkActive(100);
        closed.MarkClosed(110);

        // Act & Assert
        Assert.True(pending.IsLeaseInForce(1_000_000));
        Assert.True(active.IsLeaseInForce(4_131));
        Assert.False(active.IsLeaseInForce(4_132));
        Assert.False(replaced.IsLeaseInForce(100));
        Assert.False(closed.IsLeaseInForce(110));
    }

    #endregion

    #region Restore

    [Fact]
    public void Given_StoredFields_When_Restored_Then_EveryFieldKept()
    {
        // Act
        var purchase = LiquidityPurchaseModel.Restore(42, Channel(1), Txid(2), LiquidityPurchaseRole.Buyer,
                                                      LiquidityPurchaseKind.OpenRbf, 1_000, 1_200, s_rate,
                                                      LiquidityPaymentType.FromChannelBalance, 3, 4, new byte[64],
                                                      [0x51], s_peer, 10, s_createdAt,
                                                      LiquidityPurchaseStatus.Closed, 500, 505, true);

        // Assert
        Assert.Equal(42, purchase.Id);
        Assert.Equal(LiquidityPurchaseStatus.Closed, purchase.Status);
        Assert.Equal(500U, purchase.LeaseStartHeight);
        Assert.Equal(505U, purchase.ClosedAtHeight);
        Assert.True(purchase.ClosedEarly);
        Assert.Equal(510U, purchase.LeaseEndHeight);
    }

    [Theory]
    [InlineData(LiquidityPurchaseStatus.Active, null, null, false)]
    [InlineData(LiquidityPurchaseStatus.Pending, 100U, null, false)]
    [InlineData(LiquidityPurchaseStatus.Replaced, 100U, null, false)]
    [InlineData(LiquidityPurchaseStatus.Closed, 100U, null, false)]
    [InlineData(LiquidityPurchaseStatus.Active, 100U, 105U, false)]
    [InlineData(LiquidityPurchaseStatus.Pending, null, null, true)]
    public void Given_FieldsThatContradictTheStatus_When_Restored_Then_Throws(LiquidityPurchaseStatus status,
                                                                              uint? leaseStart, uint? closedAt,
                                                                              bool closedEarly)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => LiquidityPurchaseModel.Restore(
                                             1, Channel(1), Txid(2), LiquidityPurchaseRole.Buyer,
                                             LiquidityPurchaseKind.ChannelOpen, 1, 1, s_rate,
                                             LiquidityPaymentType.FromChannelBalance, 0, 0, new byte[64], [0x51],
                                             s_peer, 10, s_createdAt, status, leaseStart, closedAt, closedEarly));
    }

    [Fact]
    public void Given_NoId_When_Restored_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => LiquidityPurchaseModel.Restore(
                                                       0, Channel(1), Txid(2), LiquidityPurchaseRole.Buyer,
                                                       LiquidityPurchaseKind.ChannelOpen, 1, 1, s_rate,
                                                       LiquidityPaymentType.FromChannelBalance, 0, 0, new byte[64],
                                                       [0x51], s_peer, 10, s_createdAt,
                                                       LiquidityPurchaseStatus.Pending, null, null, false));
    }

    #endregion

    private static LiquidityPurchaseModel Create(LiquidityPurchaseRole role = LiquidityPurchaseRole.Seller,
                                                 LiquidityPurchaseKind kind = LiquidityPurchaseKind.ChannelOpen,
                                                 LiquidityPaymentType paymentType =
                                                     LiquidityPaymentType.FromChannelBalance,
                                                 ulong requested = 500_000, ulong miningFee = 625,
                                                 byte[]? signature = null, byte[]? script = null)
    {
        return new LiquidityPurchaseModel(Channel(1), Txid(2), role, kind, requested, 500_000, s_rate, paymentType,
                                          miningFee, 5_010, signature ?? new byte[64], script ?? [0x00, 0x20],
                                          s_peer, 4_032, s_createdAt);
    }

    private static ChannelId Channel(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static TxId Txid(byte fill) => Enumerable.Repeat(fill, 32).ToArray();
}