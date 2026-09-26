using System.Security.Cryptography;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Application.Payments.Send;
using Bolt11.Models;
using Daemon.Extensions;
using Daemon.Ipc.Handlers;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// ABCD W2-C: with <c>AddPaymentSendServices()</c> in the node's composition, <c>payinvoice</c> and
/// <c>listpayments</c> reach the real <see cref="PaymentService"/> over IPC instead of answering "not available".
/// Channels and persistence are the node's own, except the database-backed payment store (mocked; no schema here).
/// </summary>
public class PaymentSendIpcTests : IDisposable
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;

    private readonly List<PaymentModel> _stored = [];
    private readonly ServiceProvider _provider;

    public PaymentSendIpcTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:Network"] = "regtest",
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = "Data Source=:memory:"
        }).Build();
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetNodePubKey()).Returns(new CompactPubKey(new Key().PubKey.ToBytes()));
        var blockchainMonitor = new Mock<IBlockchainMonitor>();
        blockchainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(500u);

        var payments = new Mock<IPaymentDbRepository>();
        payments.Setup(p => p.AddAsync(It.IsAny<PaymentModel>())).Callback<PaymentModel>(_stored.Add)
                .Returns(Task.CompletedTask);
        payments.Setup(p => p.GetByPaymentHashAsync(It.IsAny<Hash>()))
                .ReturnsAsync((Hash hash) => _stored.LastOrDefault(p => p.PaymentHash == hash));
        payments.Setup(p => p.ListAsync(It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync((int skip, int take) => _stored.Skip(skip).Take(take).ToList());
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddNltgNodeServices(configuration, keyManager.Object);
        services.AddPaymentSendServices();
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(blockchainMonitor.Object);
        services.AddScoped(_ => unitOfWork.Object);
        services.AddScoped(_ => payments.Object);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public void Given_SendServicesInTheNode_When_Resolved_Then_PaymentServiceIsTheImplementation()
    {
        // Act
        var paymentService = _provider.GetRequiredService<IPaymentService>();

        // Assert
        Assert.IsType<PaymentService>(paymentService);
    }

    [Fact]
    public async Task Given_InvoiceToAnUnknownNode_When_PayInvoiceOverIpc_Then_AFailedNoRoutePaymentIsReturnedAndListed()
    {
        // Arrange
        var bolt11 = CreateInvoice(LightningMoney.MilliSatoshis(21_000));
        var pay = new PayInvoiceIpcHandler(NullLogger<PayInvoiceIpcHandler>.Instance, _provider);
        var list = new ListPaymentsIpcHandler(NullLogger<ListPaymentsIpcHandler>.Instance, _provider);
        var payEnvelope = CreateEnvelope(ClientCommand.PayInvoice,
                                         new PayInvoiceIpcRequest { Bolt11 = bolt11, TimeoutSeconds = 5 });

        // Act
        var payResponse = await pay.HandleAsync(payEnvelope, TestContext.Current.CancellationToken);
        var listResponse = await list.HandleAsync(CreateEnvelope(ClientCommand.ListPayments,
                                                                 new ListPaymentsIpcRequest()),
                                                  TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, payResponse.Kind);
        var payment = Deserialize<PayInvoiceIpcResponse>(payResponse).Payment;
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Contains("No route", payment.FailureReason);
        Assert.Equal(bolt11, payment.Bolt11);
        Assert.Equal(IpcEnvelopeKind.Response, listResponse.Kind);
        var listed = Assert.Single(Deserialize<ListPaymentsIpcResponse>(listResponse).Payments);
        Assert.Equal(payment.PaymentHash, listed.PaymentHash);
    }

    [Fact]
    public async Task Given_MainnetInvoice_When_PayInvoiceOverIpc_Then_InvalidOperationErrorFromThePaymentService()
    {
        // Arrange
        var pay = new PayInvoiceIpcHandler(NullLogger<PayInvoiceIpcHandler>.Instance, _provider);
        var envelope = CreateEnvelope(ClientCommand.PayInvoice,
                                      new PayInvoiceIpcRequest
                                      {
                                          Bolt11 = CreateInvoice(LightningMoney.MilliSatoshis(21_000),
                                                                 BitcoinNetwork.Mainnet)
                                      });

        // Act
        var response = await pay.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = Deserialize<IpcError>(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("Invalid invoice", error.Message);
        Assert.DoesNotContain("not available", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_stored);
    }

    private static string CreateInvoice(LightningMoney amount, BitcoinNetwork? network = null)
    {
        var invoice = new Invoice(amount, "ipc", new uint256(RandomNumberGenerator.GetBytes(32)),
                                  new uint256(RandomNumberGenerator.GetBytes(32)), network ?? BitcoinNetwork.Regtest);
        return invoice.Encode(new Key());
    }

    private static IpcEnvelope CreateEnvelope<T>(ClientCommand command, T request) => new()
    {
        Version = 1,
        Command = command,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
    };

    private static T Deserialize<T>(IpcEnvelope response) =>
        MessagePackSerializer.Deserialize<T>(response.Payload, s_options, TestContext.Current.CancellationToken);
}