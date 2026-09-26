using System.Runtime.Serialization;
using MessagePack;

namespace NLightning.Daemon.Tests.Ipc.Formatters;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.Onion.Enums;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// MessagePack round trips of the invoice/payment IPC contract (ClientCommand 9-12) and the ListChannels additions.
/// </summary>
public class PaymentsMessagePackTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;

    private static readonly MessagePackSerializerOptions s_uncompressedOptions =
        NLightningMessagePackOptions.Options.WithCompression(MessagePackCompression.None);

    private static readonly Hash s_paymentHash = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly Secret s_preimage = new(Enumerable.Range(33, 32).Select(i => (byte)i).ToArray());
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)7, 32).ToArray());
    private static readonly CompactPubKey s_payee = new([0x03, .. Enumerable.Repeat((byte)5, 32)]);
    private static readonly DateTimeOffset s_createdAt = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    [Fact]
    public void Given_CreateInvoiceRequest_When_RoundTripped_Then_EveryFieldIsPreserved()
    {
        // Arrange
        var request = new CreateInvoiceIpcRequest
        {
            Amount = LightningMoney.MilliSatoshis(50_000_123),
            Description = "coffee ☕",
            ExpirySeconds = 600
        };

        // Act
        var result = RoundTrip(request);

        // Assert
        Assert.Equal(50_000_123UL, result.Amount!.MilliSatoshi);
        Assert.Equal("coffee ☕", result.Description);
        Assert.Equal(600U, result.ExpirySeconds);
        var clientRequest = result.ToClientRequest();
        Assert.Equal(request.Amount, clientRequest.Amount);
        Assert.Equal("coffee ☕", clientRequest.Description);
        Assert.Equal(600U, clientRequest.ExpirySeconds);
    }

    [Fact]
    public void Given_AnyAmountCreateInvoiceRequest_When_RoundTripped_Then_NullsArePreserved()
    {
        // Act
        var result = RoundTrip(new CreateInvoiceIpcRequest());

        // Assert
        Assert.Null(result.Amount);
        Assert.Equal(string.Empty, result.Description);
        Assert.Null(result.ExpirySeconds);
    }

    [Fact]
    public void Given_PayInvoiceRequest_When_RoundTripped_Then_EveryFieldIsPreserved()
    {
        // Arrange
        var request = new PayInvoiceIpcRequest
        {
            Bolt11 = "lnbcrt500u1pexample",
            Amount = LightningMoney.MilliSatoshis(1_001),
            TimeoutSeconds = 30,
            MaxFee = LightningMoney.MilliSatoshis(7_500),
            MaxParts = 5
        };

        // Act
        var result = RoundTrip(request);
        var clientRequest = result.ToClientRequest();
        var defaults = RoundTrip(new PayInvoiceIpcRequest { Bolt11 = "lnbcrt1" }).ToClientRequest();

        // Assert
        Assert.Equal("lnbcrt500u1pexample", clientRequest.Bolt11);
        Assert.Equal(1_001UL, clientRequest.Amount!.MilliSatoshi);
        Assert.Equal(30U, clientRequest.TimeoutSeconds);
        Assert.Equal(7_500UL, clientRequest.MaxFee!.MilliSatoshi);
        Assert.Equal(5U, clientRequest.MaxParts);
        Assert.Null(defaults.MaxFee);
        Assert.Null(defaults.MaxParts);
    }

    [Fact]
    public void Given_ListRequests_When_RoundTripped_Then_PagesArePreserved()
    {
        // Act
        var invoices = RoundTrip(new ListInvoicesIpcRequest { Skip = 3, Take = 7 }).ToClientRequest();
        var payments = RoundTrip(new ListPaymentsIpcRequest { Skip = 4, Take = 9 }).ToClientRequest();
        var defaults = RoundTrip(new ListPaymentsIpcRequest());

        // Assert
        Assert.Equal((3, 7), (invoices.Skip, invoices.Take));
        Assert.Equal((4, 9), (payments.Skip, payments.Take));
        Assert.Equal((0, 100), (defaults.Skip, defaults.Take));
    }

    [Fact]
    public void Given_SettledInvoice_When_RoundTripped_Then_EveryFieldIsPreserved()
    {
        // Arrange
        var response = new CreateInvoiceIpcResponse { Invoice = CreateInvoice(InvoiceStatus.Settled) };

        // Act
        var result = RoundTrip(response).Invoice;

        // Assert
        Assert.Equal("lnbcrt1invoice", result.Bolt11);
        Assert.Equal(s_paymentHash, result.PaymentHash);
        Assert.Equal(50_000_123UL, result.Amount!.MilliSatoshi);
        Assert.Equal("desc", result.Description);
        Assert.Equal(InvoiceStatus.Settled, result.Status);
        Assert.Equal(s_createdAt, result.CreatedAt);
        Assert.Equal(s_createdAt.AddHours(1), result.ExpiresAt);
        Assert.False(result.IsExpired);
        Assert.Equal(50_000_200UL, result.AmountReceived!.MilliSatoshi);
        Assert.Equal(s_createdAt.AddMinutes(1), result.SettledAt);
    }

    [Fact]
    public void Given_InvoiceListWithOpenAnyAmountInvoice_When_RoundTripped_Then_NullsArePreserved()
    {
        // Arrange
        var open = new InvoiceInfoIpcResponse
        {
            Bolt11 = "lnbcrt1open",
            PaymentHash = s_paymentHash,
            Status = InvoiceStatus.Open,
            CreatedAt = s_createdAt,
            ExpiresAt = s_createdAt.AddHours(1),
            IsExpired = true
        };

        // Act
        var result = RoundTrip(new ListInvoicesIpcResponse { Invoices = [open, CreateInvoice(InvoiceStatus.Canceled)] });

        // Assert
        Assert.Equal(2, result.Invoices.Count);
        var first = result.Invoices[0];
        Assert.Null(first.Amount);
        Assert.Null(first.Description);
        Assert.Null(first.AmountReceived);
        Assert.Null(first.SettledAt);
        Assert.True(first.IsExpired);
        Assert.Equal(InvoiceStatus.Canceled, result.Invoices[1].Status);
    }

    [Fact]
    public void Given_SucceededPayment_When_RoundTripped_Then_EveryFieldIsPreserved()
    {
        // Arrange
        var response = new PayInvoiceIpcResponse { Payment = CreateSucceededPayment(), Attempts = 3, Parts = 2 };

        // Act
        var roundTripped = RoundTrip(response);
        var result = roundTripped.Payment;

        // Assert
        Assert.Equal(s_paymentHash, result.PaymentHash);
        Assert.Equal("lnbcrt1pay", result.Bolt11);
        Assert.Equal(s_payee, result.PayeeNodeId);
        Assert.Equal(50_000_123UL, result.Amount.MilliSatoshi);
        Assert.Equal(3_025UL, result.Fee.MilliSatoshi);
        Assert.Equal(PaymentStatus.Succeeded, result.Status);
        Assert.Equal(s_preimage, result.Preimage);
        Assert.Null(result.FailureCode);
        Assert.Null(result.FailureSourceIndex);
        Assert.Null(result.FailureReason);
        Assert.Equal(s_channelId, result.OutgoingChannelId);
        Assert.Equal(4UL, result.OutgoingHtlcId);
        Assert.Equal((3, 2), (roundTripped.Attempts, roundTripped.Parts));
        Assert.Equal(s_createdAt, result.CreatedAt);
        Assert.Equal(s_createdAt.AddSeconds(3), result.CompletedAt);
    }

    [Fact]
    public void Given_FailedPaymentWithUnnamedFailureCode_When_RoundTripped_Then_TheCodeSurvives()
    {
        // Arrange: a failure code this build has no name for still crosses as its u16
        var failed = new PaymentInfoIpcResponse
        {
            PaymentHash = s_paymentHash,
            PayeeNodeId = s_payee,
            Amount = LightningMoney.MilliSatoshis(1_000),
            Fee = LightningMoney.Zero,
            Status = PaymentStatus.Failed,
            FailureCode = (FailureCode)0x4099,
            FailureSourceIndex = 2,
            FailureReason = "unknown code",
            CreatedAt = s_createdAt,
            CompletedAt = s_createdAt
        };
        var known = new PaymentInfoIpcResponse
        {
            PaymentHash = s_paymentHash,
            PayeeNodeId = s_payee,
            Amount = LightningMoney.MilliSatoshis(1_000),
            Fee = LightningMoney.Zero,
            Status = PaymentStatus.Failed,
            FailureCode = FailureCode.IncorrectOrUnknownPaymentDetails,
            CreatedAt = s_createdAt,
            CompletedAt = s_createdAt
        };

        // Act
        var result = RoundTrip(new ListPaymentsIpcResponse { Payments = [failed, known] });

        // Assert
        Assert.Equal((FailureCode)0x4099, result.Payments[0].FailureCode);
        Assert.Equal(2, result.Payments[0].FailureSourceIndex);
        Assert.Equal("unknown code", result.Payments[0].FailureReason);
        Assert.Null(result.Payments[0].Preimage);
        Assert.Null(result.Payments[0].OutgoingChannelId);
        Assert.Null(result.Payments[0].OutgoingHtlcId);
        Assert.Null(result.Payments[0].Bolt11);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Payments[1].FailureCode);
    }

    [Fact]
    public void Given_Preimage_When_Serialized_Then_IsWrittenAsBin32()
    {
        // Act
        var bytes = MessagePackSerializer.Serialize<Secret?>(s_preimage, s_uncompressedOptions,
                                                             TestContext.Current.CancellationToken);
        var nil = MessagePackSerializer.Serialize<Secret?>(null, s_uncompressedOptions,
                                                           TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([0xc4, 0x20, .. (byte[])s_preimage], bytes);
        Assert.Equal([0xc0], nil);
    }

    [Fact]
    public void Given_PreimageOfWrongLength_When_Deserialized_Then_Throws()
    {
        // Arrange: bin 8 of 31 bytes
        byte[] bytes = [0xc4, 0x1f, .. new byte[31]];

        // Act / Assert
        var exception = Assert.Throws<MessagePackSerializationException>(() =>
            MessagePackSerializer.Deserialize<Secret?>(bytes, s_uncompressedOptions,
                                                       TestContext.Current.CancellationToken));
        Assert.IsType<SerializationException>(exception.InnerException);
    }

    [Fact]
    public void Given_ChannelInfoWithFeePolicyAndReestablished_When_RoundTripped_Then_NewKeysArePreserved()
    {
        // Arrange
        var response = new ListChannelsIpcResponse
        {
            Channels =
            [
                new ChannelInfoIpcResponse
                {
                    ChannelId = s_channelId,
                    PeerId = s_payee,
                    State = ChannelState.Open,
                    Capacity = LightningMoney.Satoshis(2_000_000),
                    LocalBalance = LightningMoney.Satoshis(1_000_000),
                    RemoteBalance = LightningMoney.Satoshis(1_000_000),
                    IsReestablished = true,
                    FeeBaseMsat = 1_000,
                    FeePpm = 100
                }
            ]
        };

        // Act
        var channel = Assert.Single(RoundTrip(response).Channels);

        // Assert
        Assert.True(channel.IsReestablished);
        Assert.Equal(1_000U, channel.FeeBaseMsat);
        Assert.Equal(100U, channel.FeePpm);
    }

    private static InvoiceInfoIpcResponse CreateInvoice(InvoiceStatus status) => new()
    {
        Bolt11 = "lnbcrt1invoice",
        PaymentHash = s_paymentHash,
        Amount = LightningMoney.MilliSatoshis(50_000_123),
        Description = "desc",
        Status = status,
        CreatedAt = s_createdAt,
        ExpiresAt = s_createdAt.AddHours(1),
        AmountReceived = status == InvoiceStatus.Settled ? LightningMoney.MilliSatoshis(50_000_200) : null,
        SettledAt = status == InvoiceStatus.Settled ? s_createdAt.AddMinutes(1) : null
    };

    private static PaymentInfoIpcResponse CreateSucceededPayment() => new()
    {
        PaymentHash = s_paymentHash,
        Bolt11 = "lnbcrt1pay",
        PayeeNodeId = s_payee,
        Amount = LightningMoney.MilliSatoshis(50_000_123),
        Fee = LightningMoney.MilliSatoshis(3_025),
        Status = PaymentStatus.Succeeded,
        Preimage = s_preimage,
        OutgoingChannelId = s_channelId,
        OutgoingHtlcId = 4,
        CreatedAt = s_createdAt,
        CompletedAt = s_createdAt.AddSeconds(3)
    };

    private static T RoundTrip<T>(T value)
    {
        var bytes = MessagePackSerializer.Serialize(value, s_options, TestContext.Current.CancellationToken);
        return MessagePackSerializer.Deserialize<T>(bytes, s_options, TestContext.Current.CancellationToken);
    }
}