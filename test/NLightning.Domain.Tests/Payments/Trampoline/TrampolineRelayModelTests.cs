namespace NLightning.Domain.Tests.Payments.Trampoline;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Trampoline;

public class TrampolineRelayModelTests
{
    private static readonly DateTimeOffset s_createdAt = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Hash s_hash = new(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly CompactPubKey s_next = new([0x02, .. Enumerable.Repeat((byte)7, 32)]);
    private static readonly Secret s_preimage = new(Enumerable.Repeat((byte)5, 32).ToArray());

    private static TrampolineRelayModel CreateRelay(byte[]? packet = null) =>
        new(s_hash, s_next, LightningMoney.MilliSatoshis(100_000UL), 800_100, LightningMoney.MilliSatoshis(101_500UL),
            s_createdAt, packet ?? [0x00, 0x01, 0x02]);

    [Fact]
    public void Given_NewRelay_When_Created_Then_CollectingWithCopiedRoutingFields()
    {
        // Arrange
        var packet = new byte[] { 9, 8, 7 };

        // Act
        var relay = CreateRelay(packet);
        packet[0] = 0;

        // Assert
        Assert.Equal(TrampolineRelayStatus.Collecting, relay.Status);
        Assert.False(relay.IsCompleted);
        Assert.Equal(new byte[] { 9, 8, 7 }, relay.NextTrampolinePacket);
        Assert.Null(relay.FeeEarned);
        Assert.Null(relay.Preimage);
        Assert.Null(relay.CompletedAt);
    }

    [Fact]
    public void Given_CollectingRelay_When_SentThenFulfilled_Then_FulfilledWithPreimageAndFee()
    {
        // Arrange
        var relay = CreateRelay();
        var at = s_createdAt.AddSeconds(3);

        // Act
        relay.MarkSending([0xAA, 0xBB]);
        relay.MarkFulfilled(s_preimage, LightningMoney.MilliSatoshis(1_200UL), at);

        // Assert
        Assert.Equal(TrampolineRelayStatus.Fulfilled, relay.Status);
        Assert.True(relay.IsCompleted);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, relay.OutgoingPaymentSecret);
        Assert.Equal(s_preimage, relay.Preimage);
        Assert.Equal(1_200UL, relay.FeeEarned!.MilliSatoshi);
        Assert.Equal(at, relay.CompletedAt);
    }

    [Fact]
    public void Given_CollectingRelay_When_Fulfilled_Then_Throws()
    {
        // Arrange
        var relay = CreateRelay();

        // Act & Assert: nothing was sent, so nothing can have been fulfilled downstream
        Assert.Throws<InvalidOperationException>(() =>
            relay.MarkFulfilled(s_preimage, LightningMoney.Zero, s_createdAt));
    }

    [Fact]
    public void Given_CollectingOrSendingRelay_When_Failed_Then_Failed()
    {
        // Arrange
        var collecting = CreateRelay();
        var sending = CreateRelay();
        sending.MarkSending();

        // Act
        collecting.MarkFailed(0x4017, "mpp_timeout", s_createdAt);
        sending.MarkFailed(null, "no route", s_createdAt);

        // Assert
        Assert.Equal(TrampolineRelayStatus.Failed, collecting.Status);
        Assert.Equal((ushort)0x4017, collecting.FailureCode);
        Assert.Equal(TrampolineRelayStatus.Failed, sending.Status);
        Assert.Equal("no route", sending.FailureReason);
    }

    [Fact]
    public void Given_CompletedRelay_When_MovedAgain_Then_Throws()
    {
        // Arrange
        var relay = CreateRelay();
        relay.MarkSending();
        relay.MarkFulfilled(s_preimage, LightningMoney.Zero, s_createdAt);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => relay.MarkFailed(null, null, s_createdAt));
        Assert.Throws<InvalidOperationException>(() => relay.MarkSending());
    }

    [Fact]
    public void Given_InvalidRoutingFields_When_Created_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new TrampolineRelayModel(s_hash, null, LightningMoney.MilliSatoshis(1UL), 1, LightningMoney.MilliSatoshis(1UL),
                                     s_createdAt));
        Assert.Throws<ArgumentException>(() =>
            new TrampolineRelayModel(s_hash, s_next, LightningMoney.MilliSatoshis(1UL), 1,
                                     LightningMoney.MilliSatoshis(1UL), s_createdAt, nextEncryptedRecipientData: [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TrampolineRelayModel(s_hash, s_next, LightningMoney.Zero, 1, LightningMoney.MilliSatoshis(1UL),
                                     s_createdAt));
    }

    [Fact]
    public void Given_BlindedPathsWithoutNextNode_When_Created_Then_Accepted()
    {
        // Act
        var relay = new TrampolineRelayModel(s_hash, null, LightningMoney.MilliSatoshis(1UL), 1,
                                             LightningMoney.MilliSatoshis(2UL), s_createdAt,
                                             recipientBlindedPaths: [1, 2, 3]);

        // Assert
        Assert.Null(relay.NextNodeId);
        Assert.Equal(new byte[] { 1, 2, 3 }, relay.RecipientBlindedPaths);
    }

    [Fact]
    public void Given_StoredRelay_When_Restored_Then_FieldsMustMatchTheStatus()
    {
        // Act
        var fulfilled = TrampolineRelayModel.Restore(s_hash, TrampolineRelayStatus.Fulfilled, s_next, null, null, null,
                                                     null, null, LightningMoney.MilliSatoshis(10UL), 5,
                                                     LightningMoney.MilliSatoshis(12UL),
                                                     LightningMoney.MilliSatoshis(2UL), null, s_preimage, null, null,
                                                     s_createdAt, s_createdAt);

        // Assert
        Assert.Equal(TrampolineRelayStatus.Fulfilled, fulfilled.Status);
        Assert.Equal(2UL, fulfilled.FeeEarned!.MilliSatoshi);
        Assert.Throws<ArgumentException>(() =>
            TrampolineRelayModel.Restore(s_hash, TrampolineRelayStatus.Fulfilled, s_next, null, null, null, null, null,
                                         LightningMoney.MilliSatoshis(10UL), 5, LightningMoney.MilliSatoshis(12UL),
                                         null, null, null, null, null, s_createdAt, s_createdAt));
        Assert.Throws<ArgumentException>(() =>
            TrampolineRelayModel.Restore(s_hash, TrampolineRelayStatus.Sending, s_next, null, null, null, null, null,
                                         LightningMoney.MilliSatoshis(10UL), 5, LightningMoney.MilliSatoshis(12UL),
                                         null, null, null, 0x2002, null, s_createdAt, null));
        Assert.Throws<ArgumentException>(() =>
            TrampolineRelayModel.Restore(s_hash, TrampolineRelayStatus.Failed, s_next, null, null, null, null, null,
                                         LightningMoney.MilliSatoshis(10UL), 5, LightningMoney.MilliSatoshis(12UL),
                                         null, null, null, 0x2002, null, s_createdAt, null));
    }

    [Fact]
    public void Given_Statuses_When_Read_Then_PersistedValuesAreStable()
    {
        // Assert
        Assert.Equal(0, (byte)TrampolineRelayStatus.Collecting);
        Assert.Equal(1, (byte)TrampolineRelayStatus.Sending);
        Assert.Equal(2, (byte)TrampolineRelayStatus.Fulfilled);
        Assert.Equal(3, (byte)TrampolineRelayStatus.Failed);
    }
}