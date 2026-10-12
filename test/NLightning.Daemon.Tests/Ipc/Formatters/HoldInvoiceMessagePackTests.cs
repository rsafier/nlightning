using MessagePack;

namespace NLightning.Daemon.Tests.Ipc.Formatters;

using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// MessagePack round trips of the hold invoice IPC contract (ClientCommand 49-51, NL-995): the three requests, the
/// shared response and the nulls an older client or daemon leaves out.
/// </summary>
public class HoldInvoiceMessagePackTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;

    private static readonly Hash s_paymentHash = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly Secret s_preimage = new(Enumerable.Range(33, 32).Select(i => (byte)i).ToArray());
    private static readonly Secret s_paymentSecret = new(Enumerable.Repeat((byte)9, 32).ToArray());
    private static readonly DateTimeOffset s_createdAt = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    [Fact]
    public void Given_CreateHoldInvoiceRequest_When_RoundTripped_Then_EveryFieldIsPreserved()
    {
        // Arrange
        var request = new CreateHoldInvoiceIpcRequest
        {
            PaymentHash = s_paymentHash,
            Amount = LightningMoney.MilliSatoshis(50_000_123),
            Description = "held coffee ☕",
            ExpirySeconds = 600,
            Label = "shop",
            Tags = ["a=b", "c=d"]
        };

        // Act
        var clientRequest = RoundTrip(request).ToClientRequest();

        // Assert
        Assert.Equal(s_paymentHash, clientRequest.PaymentHash);
        Assert.Equal(50_000_123UL, clientRequest.Amount!.MilliSatoshi);
        Assert.Equal("held coffee ☕", clientRequest.Description);
        Assert.Equal(600U, clientRequest.ExpirySeconds);
        Assert.Equal("shop", clientRequest.Label);
        Assert.Equal(["a=b", "c=d"], clientRequest.Tags);
    }

    [Fact]
    public void Given_AnyAmountCreateHoldInvoiceRequest_When_RoundTripped_Then_NullsArePreserved()
    {
        // Act
        var result = RoundTrip(new CreateHoldInvoiceIpcRequest { PaymentHash = s_paymentHash });
        var clientRequest = result.ToClientRequest();

        // Assert
        Assert.Equal(s_paymentHash, result.PaymentHash);
        Assert.Null(result.Amount);
        Assert.Equal(string.Empty, result.Description);
        Assert.Null(result.ExpirySeconds);
        Assert.Null(result.Label);
        Assert.Null(result.Tags);
        Assert.Null(clientRequest.Amount);
        Assert.Null(clientRequest.ExpirySeconds);
        Assert.Null(clientRequest.Label);
        Assert.Empty(clientRequest.Tags);
    }

    [Fact]
    public void Given_SettleAndCancelRequests_When_RoundTripped_Then_HashAndPreimageArePreserved()
    {
        // Act
        var settle = RoundTrip(new SettleHoldInvoiceIpcRequest { PaymentHash = s_paymentHash, Preimage = s_preimage })
           .ToClientRequest();
        var cancel = RoundTrip(new CancelHoldInvoiceIpcRequest { PaymentHash = s_paymentHash }).ToClientRequest();

        // Assert
        Assert.Equal((s_paymentHash, s_preimage), (settle.PaymentHash, settle.Preimage));
        Assert.Equal(s_paymentHash, cancel.PaymentHash);
    }

    [Fact]
    public void Given_HeldInvoiceResponse_When_RoundTripped_Then_EveryFieldIsPreserved()
    {
        // Arrange: a held hold invoice (the set complete, nothing fulfilled or failed yet)
        var response = new HoldInvoiceIpcResponse
        {
            Invoice = new InvoiceInfoIpcResponse
            {
                Bolt11 = "lnbcrt1hold",
                PaymentHash = s_paymentHash,
                Amount = LightningMoney.MilliSatoshis(50_000_123),
                Description = "desc",
                Status = InvoiceStatus.Held,
                CreatedAt = s_createdAt,
                ExpiresAt = s_createdAt.AddHours(1),
                AmountReceived = LightningMoney.MilliSatoshis(50_000_123),
                Label = "shop",
                Tags = ["a=b"]
            }
        };

        // Act
        var result = RoundTrip(response).Invoice;

        // Assert
        Assert.Equal("lnbcrt1hold", result.Bolt11);
        Assert.Equal(s_paymentHash, result.PaymentHash);
        Assert.Equal(50_000_123UL, result.Amount!.MilliSatoshi);
        Assert.Equal("desc", result.Description);
        Assert.Equal(InvoiceStatus.Held, result.Status);
        Assert.Equal(s_createdAt, result.CreatedAt);
        Assert.Equal(s_createdAt.AddHours(1), result.ExpiresAt);
        Assert.False(result.IsExpired);
        Assert.Equal(50_000_123UL, result.AmountReceived!.MilliSatoshi);
        Assert.Null(result.SettledAt);
        Assert.Equal("shop", result.Label);
        Assert.Equal(["a=b"], result.Tags);
    }

    [Fact]
    public void Given_MinimalResponse_When_RoundTripped_Then_NullsArePreserved()
    {
        // Arrange
        var response = new HoldInvoiceIpcResponse
        {
            Invoice = new InvoiceInfoIpcResponse
            {
                PaymentHash = s_paymentHash,
                Status = InvoiceStatus.Canceled,
                CreatedAt = s_createdAt,
                ExpiresAt = s_createdAt.AddHours(1)
            }
        };

        // Act
        var result = RoundTrip(response).Invoice;

        // Assert
        Assert.Null(result.Bolt11);
        Assert.Null(result.Amount);
        Assert.Null(result.Description);
        Assert.Null(result.AmountReceived);
        Assert.Null(result.SettledAt);
        Assert.Null(result.Label);
        Assert.Null(result.Tags);
        Assert.Equal(InvoiceStatus.Canceled, result.Status);
    }

    [Fact]
    public void Given_HoldInvoiceModel_When_MappedThroughClientAndIpcResponses_Then_ItCarriesWithoutAPreimage()
    {
        // Arrange: an open hold invoice has no preimage (NL-995); the mapping must leave that be, not dereference it
        var open = new InvoiceModel(s_paymentHash, null, s_paymentSecret, LightningMoney.MilliSatoshis(5_000), "tea",
                                    "lnbcrt1hold", s_createdAt, 900, 40);
        var held = new InvoiceModel(s_paymentHash, null, s_paymentSecret, LightningMoney.MilliSatoshis(5_000), "tea",
                                    "lnbcrt1hold", s_createdAt, 900, 40, InvoiceStatus.Held,
                                    LightningMoney.MilliSatoshis(5_000));

        // Act
        InvoiceInfoIpcResponse ToIpc(InvoiceModel invoice) =>
            InvoiceInfoIpcResponse.FromClientResponse(
                InvoiceInfoClientResponse.FromModel(invoice, s_createdAt));
        var openResult = ToIpc(open);
        var heldResult = ToIpc(held);

        // Assert: no preimage anywhere on the wire, and the Held status (new, = 4) crosses as is
        Assert.Equal(InvoiceStatus.Open, openResult.Status);
        Assert.Equal(InvoiceStatus.Held, heldResult.Status);
        Assert.Equal(5_000UL, heldResult.AmountReceived!.MilliSatoshi);
        Assert.Equal(s_paymentHash, heldResult.PaymentHash);
        Assert.Equal("lnbcrt1hold", heldResult.Bolt11);
    }

    private static T RoundTrip<T>(T value)
    {
        var bytes = MessagePackSerializer.Serialize(value, s_options, TestContext.Current.CancellationToken);
        return MessagePackSerializer.Deserialize<T>(bytes, s_options, TestContext.Current.CancellationToken);
    }
}