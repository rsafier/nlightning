namespace NLightning.Domain.Tests.Payments.Models;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;

public class ForwardCircuitModelTests
{
    private static readonly DateTimeOffset s_createdAt = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly ChannelId s_incoming = new(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly ChannelId s_outgoing = new(Enumerable.Repeat((byte)2, 32).ToArray());
    private static readonly ShortChannelId s_outgoingScid = new(500, 1, 0);

    private static ForwardCircuitModel CreateCircuit(ulong incomingMsat = 50_033_125, ulong outgoingMsat = 50_027_123,
                                                     uint incomingCltv = 1_140, uint outgoingCltv = 1_100) =>
        new(s_incoming, 7, LightningMoney.MilliSatoshis(incomingMsat), incomingCltv,
            new Hash(Enumerable.Repeat((byte)3, 32).ToArray()), new Secret(Enumerable.Repeat((byte)4, 32).ToArray()),
            s_outgoingScid, LightningMoney.MilliSatoshis(outgoingMsat), outgoingCltv, s_createdAt);

    [Fact]
    public void Given_NewCircuit_When_Created_Then_PendingWithFee()
    {
        // Act
        var circuit = CreateCircuit();

        // Assert
        Assert.Equal(ForwardCircuitStatus.Pending, circuit.Status);
        Assert.Equal(6_002UL, circuit.Fee.MilliSatoshi);
        Assert.Null(circuit.OutgoingHtlcId);
    }

    [Fact]
    public void Given_PendingCircuit_When_OfferedThenFulfilled_Then_Fulfilled()
    {
        // Arrange
        var circuit = CreateCircuit();

        // Act
        circuit.AddOutgoingHtlc(s_outgoing, 3);
        circuit.MarkFulfilled(s_createdAt.AddSeconds(1));

        // Assert
        Assert.Equal(ForwardCircuitStatus.Fulfilled, circuit.Status);
        Assert.Equal(s_outgoing, circuit.OutgoingChannelId);
        Assert.Equal(3UL, circuit.OutgoingHtlcId);
        Assert.NotNull(circuit.ResolvedAt);
    }

    [Fact]
    public void Given_PendingCircuit_When_Failed_Then_Failed()
    {
        // Arrange
        var circuit = CreateCircuit();

        // Act
        circuit.MarkFailed(s_createdAt);

        // Assert
        Assert.Equal(ForwardCircuitStatus.Failed, circuit.Status);
    }

    [Fact]
    public void Given_PendingCircuit_When_FulfilledWithoutOutgoingHtlc_Then_Throws()
    {
        // Arrange
        var circuit = CreateCircuit();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => circuit.MarkFulfilled(s_createdAt));
    }

    [Fact]
    public void Given_PendingCircuitWhoseOfferedSaveWasLost_When_FulfilledWithOutgoingHtlc_Then_FulfilledAndHtlcRecorded()
    {
        // Arrange: the outgoing add was saved but the crash came before the circuit's Offered save
        var circuit = CreateCircuit();

        // Act
        circuit.MarkFulfilled(s_outgoing, 3, s_createdAt.AddSeconds(1));

        // Assert
        Assert.Equal(ForwardCircuitStatus.Fulfilled, circuit.Status);
        Assert.Equal(s_outgoing, circuit.OutgoingChannelId);
        Assert.Equal(3UL, circuit.OutgoingHtlcId);
        Assert.Equal(s_createdAt.AddSeconds(1), circuit.ResolvedAt);
    }

    [Fact]
    public void Given_PendingCircuit_When_FailedWithOutgoingHtlc_Then_FailedAndHtlcRecorded()
    {
        // Arrange
        var circuit = CreateCircuit();

        // Act
        circuit.MarkFailed(s_outgoing, 3, s_createdAt);

        // Assert
        Assert.Equal(ForwardCircuitStatus.Failed, circuit.Status);
        Assert.Equal(3UL, circuit.OutgoingHtlcId);
    }

    [Fact]
    public void Given_OfferedCircuit_When_FulfilledWithSameOutgoingHtlc_Then_Fulfilled()
    {
        // Arrange
        var circuit = CreateCircuit();
        circuit.AddOutgoingHtlc(s_outgoing, 3);

        // Act
        circuit.MarkFulfilled(s_outgoing, 3, s_createdAt);

        // Assert
        Assert.Equal(ForwardCircuitStatus.Fulfilled, circuit.Status);
    }

    [Fact]
    public void Given_OfferedCircuit_When_ResolvedWithAnotherOutgoingHtlc_Then_Throws()
    {
        // Arrange
        var circuit = CreateCircuit();
        circuit.AddOutgoingHtlc(s_outgoing, 3);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => circuit.MarkFulfilled(s_outgoing, 4, s_createdAt));
        Assert.Throws<InvalidOperationException>(() => circuit.MarkFailed(s_incoming, 3, s_createdAt));
        Assert.Equal(ForwardCircuitStatus.Offered, circuit.Status);
    }

    [Fact]
    public void Given_FulfilledCircuit_When_FulfilledWithOutgoingHtlcAgain_Then_Throws()
    {
        // Arrange
        var circuit = CreateCircuit();
        circuit.MarkFulfilled(s_outgoing, 3, s_createdAt);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => circuit.MarkFulfilled(s_outgoing, 3, s_createdAt));
        Assert.Throws<InvalidOperationException>(() => circuit.MarkFailed(s_outgoing, 3, s_createdAt));
    }

    [Fact]
    public void Given_FulfilledCircuit_When_Failed_Then_Throws()
    {
        // Arrange
        var circuit = CreateCircuit();
        circuit.AddOutgoingHtlc(s_outgoing, 3);
        circuit.MarkFulfilled(s_createdAt);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => circuit.MarkFailed(s_createdAt));
    }

    [Fact]
    public void Given_OfferedCircuit_When_OfferedAgain_Then_Throws()
    {
        // Arrange
        var circuit = CreateCircuit();
        circuit.AddOutgoingHtlc(s_outgoing, 3);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => circuit.AddOutgoingHtlc(s_outgoing, 4));
    }

    [Fact]
    public void Given_OutgoingAboveIncoming_When_Created_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateCircuit(incomingMsat: 1_000, outgoingMsat: 1_001));
    }

    [Fact]
    public void Given_OutgoingExpiryAfterIncoming_When_Created_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateCircuit(incomingCltv: 100, outgoingCltv: 101));
    }

    [Fact]
    public void Given_StoredOfferedCircuit_When_Restored_Then_OutgoingSideKept()
    {
        // Act
        var circuit = ForwardCircuitModel.Restore(s_incoming, 7, LightningMoney.MilliSatoshis(2_000UL), 140,
                                                  new Hash(new byte[32]), new Secret(new byte[32]), s_outgoingScid,
                                                  LightningMoney.MilliSatoshis(1_000UL), 100, s_createdAt,
                                                  ForwardCircuitStatus.Offered, s_outgoing, 9, null);

        // Assert
        Assert.Equal(ForwardCircuitStatus.Offered, circuit.Status);
        Assert.Equal(9UL, circuit.OutgoingHtlcId);
    }

    [Fact]
    public void Given_OfferedWithoutOutgoingHtlc_When_Restored_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => ForwardCircuitModel.Restore(s_incoming, 7,
                                                                           LightningMoney.MilliSatoshis(2_000UL), 140,
                                                                           new Hash(new byte[32]),
                                                                           new Secret(new byte[32]), s_outgoingScid,
                                                                           LightningMoney.MilliSatoshis(1_000UL), 100,
                                                                           s_createdAt, ForwardCircuitStatus.Offered,
                                                                           null, null, null));
    }
}