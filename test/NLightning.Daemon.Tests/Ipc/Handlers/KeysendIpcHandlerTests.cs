using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>keysend</c> (ClientCommand 31, lane lh1-l3) over IPC: the request reaches
/// <see cref="IPaymentService.PayKeysendAsync"/> with its custom records and options, the payment comes back with its
/// records, and refusals carry the error code the CLI shows.
/// </summary>
public class KeysendIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly Hash s_hash = new(Enumerable.Repeat((byte)0x77, 32).ToArray());
    private static readonly CompactPubKey s_node =
        new(Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c"));

    private readonly Mock<IPaymentService> _service = new();
    private PayKeysendRequest? _request;
    private PayInvoiceOptions? _options;

    public KeysendIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _service.Setup(s => s.PayKeysendAsync(It.IsAny<PayKeysendRequest>(), It.IsAny<PayInvoiceOptions>(),
                                              It.IsAny<CancellationToken>()))
                .Callback<PayKeysendRequest, PayInvoiceOptions, CancellationToken>((r, o, _) =>
                                                                                       (_request, _options) = (r, o))
                .ReturnsAsync(new PayInvoiceResult(Payment(), 1, 1));
    }

    [Fact]
    public async Task Given_AKeysendRequest_When_Handled_Then_TheServiceGetsItAndThePaymentComesBackWithRecords()
    {
        // Arrange
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(new KeysendIpcRequest
        {
            Destination = s_node,
            Amount = LightningMoney.Satoshis(21),
            CustomRecords = new Dictionary<ulong, byte[]> { [7629169] = "boost"u8.ToArray(), [65537] = [] },
            TimeoutSeconds = 45,
            MaxFee = LightningMoney.MilliSatoshis(3_000)
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<PayInvoiceIpcResponse>(response.Payload, s_options,
                                                                               TestContext.Current.CancellationToken);
        Assert.Equal(PaymentStatus.Succeeded, payload.Payment.Status);
        Assert.True(payload.Payment.IsKeysend);
        Assert.Equal("boost"u8.ToArray(), payload.Payment.CustomRecords![7629169]);
        Assert.Equal(s_node, _request!.Destination);
        Assert.Equal(21_000UL, _request.Amount.MilliSatoshi);
        Assert.Equal([65537UL, 7629169UL], _request.CustomRecords.Select(r => r.Type));
        Assert.Equal(TimeSpan.FromSeconds(45), _options!.Timeout);
        Assert.Equal(3_000UL, _options.MaxFee!.MilliSatoshi);
        Assert.Equal(1, _options.MaxParts);
    }

    [Theory]
    [InlineData(0UL, null, null)]
    [InlineData(1UL, 301u, null)]
    [InlineData(1UL, null, 65535UL)]
    [InlineData(1UL, null, CustomRecordCodec.KeysendPreimageType)]
    public async Task Given_ArgumentsOutOfBounds_When_Handled_Then_RefusedBeforeTheService(ulong amountSat,
        uint? timeout, ulong? recordType)
    {
        // Arrange
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(new KeysendIpcRequest
        {
            Destination = s_node,
            Amount = LightningMoney.Satoshis(amountSat),
            TimeoutSeconds = timeout,
            CustomRecords = recordType is { } type ? new Dictionary<ulong, byte[]> { [type] = [0x01] } : null
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, AssertError(response).Code);
        _service.Verify(s => s.PayKeysendAsync(It.IsAny<PayKeysendRequest>(), It.IsAny<PayInvoiceOptions>(),
                                               It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_TheServiceRefusesTheDestination_When_Handled_Then_InvalidOperation()
    {
        // Arrange
        _service.Setup(s => s.PayKeysendAsync(It.IsAny<PayKeysendRequest>(), It.IsAny<PayInvoiceOptions>(),
                                              It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ArgumentException("The destination is this node"));
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(new KeysendIpcRequest
        {
            Destination = s_node,
            Amount = LightningMoney.Satoshis(1)
        }), TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("this node", error.Message);
    }

    [Fact]
    public void Given_TheRegistrationCalledTwice_When_Composed_Then_OneHandler()
    {
        // Arrange
        var services = BuildServices();
        services.AddKeysendIpcServices();

        // Act
        var provider = services.BuildServiceProvider();

        // Assert
        Assert.Single(provider.GetServices<IIpcCommandHandler>(), h => h.Command == ClientCommand.Keysend);
    }

    [Fact]
    public void Given_AKeysendInvoiceRecord_When_MappedForListInvoices_Then_KindAndRecordsCrossTheWire()
    {
        // Arrange
        var invoice = new InvoiceModel(s_hash, new Secret(new byte[32]), new Secret(new byte[32]), null, null, null,
                                       DateTimeOffset.UnixEpoch, 60, 18,
                                       keysend: new KeysendDetails([new CustomRecord(7629169, [0x01])]));
        var ipc = InvoiceInfoIpcResponse.FromClientResponse(
            Domain.Client.Responses.InvoiceInfoClientResponse.FromModel(invoice, DateTimeOffset.UnixEpoch));

        // Act
        var read = MessagePackSerializer.Deserialize<InvoiceInfoIpcResponse>(
            MessagePackSerializer.Serialize(ipc, s_options, TestContext.Current.CancellationToken), s_options,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(InvoiceKind.Keysend, read.Kind);
        Assert.Equal(new byte[] { 0x01 }, read.CustomRecords![7629169]);
    }

    [Fact]
    public void Given_AKeysendInvoiceRecord_When_Serialized_Then_CustomRecordsUseKey12AndKey11StaysFree()
    {
        // Arrange: key 11 belongs to lane lh1-l2's BOLT 12 offer id (NL-454); a second type on one key would break
        // listinvoices once both lanes are merged
        var ipc = new InvoiceInfoIpcResponse
        {
            PaymentHash = s_hash,
            Status = InvoiceStatus.Settled,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ExpiresAt = DateTimeOffset.UnixEpoch,
            Kind = InvoiceKind.Keysend,
            CustomRecords = new Dictionary<ulong, byte[]> { [7629169] = [0x01] }
        };

        // Act
        var bytes = MessagePackSerializer.Serialize(ipc, s_options.WithCompression(MessagePackCompression.None),
                                                    TestContext.Current.CancellationToken);
        var reader = new MessagePackReader(bytes);
        var count = reader.ReadArrayHeader();
        for (var i = 0; i < 11; i++)
            reader.Skip();
        var key11IsNil = reader.TryReadNil();
        var key12Entries = reader.ReadMapHeader();

        // Assert
        Assert.Equal(13, count);
        Assert.True(key11IsNil);
        Assert.Equal(1, key12Entries);
    }

    private static PaymentModel Payment()
    {
        var payment = new PaymentModel(s_hash, null, s_node, LightningMoney.Satoshis(21), LightningMoney.Zero,
                                       DateTimeOffset.UnixEpoch,
                                       keysend: new KeysendDetails([new CustomRecord(7629169, "boost"u8)]));
        payment.Succeed(new Secret(new byte[32]), DateTimeOffset.UnixEpoch);
        return payment;
    }

    private static IpcError AssertError(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }

    private IIpcCommandHandler GetHandler()
    {
        var provider = BuildServices().BuildServiceProvider();
        return provider.GetServices<IIpcCommandHandler>().Single(h => h.Command == ClientCommand.Keysend);
    }

    private ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_service.Object);
        services.AddKeysendIpcServices();
        return services;
    }

    private static IpcEnvelope CreateEnvelope(KeysendIpcRequest request) =>
        new()
        {
            Version = 1,
            Command = ClientCommand.Keysend,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };
}