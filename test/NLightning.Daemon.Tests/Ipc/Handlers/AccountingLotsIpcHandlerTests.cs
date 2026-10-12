using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Financial.Lots;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Persistence.Interfaces;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>accounting lots import</c> over IPC 45 (action 13, keys 13; NL-602 A3-T4, D-A9): the lots cross the envelope, the
/// result comes back, the service's refusals are <c>invalid_operation</c>, and a node without the import says so.
/// </summary>
public class AccountingLotsIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_june = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Mock<IAccountingLots> _lots = new();

    public AccountingLotsIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    [Fact]
    public async Task Given_AnImport_When_Sent_Then_TheLotsReachTheServiceAndTheResultComesBack()
    {
        // Arrange
        IReadOnlyList<AccountingLotPoint>? lots = null;
        _lots.Setup(l => l.ImportAsync("USD", It.IsAny<IReadOnlyList<AccountingLotPoint>>(),
                                       It.IsAny<CancellationToken>()))
             .Callback((string? _, IReadOnlyList<AccountingLotPoint> sent, CancellationToken _) => lots = sent)
             .ReturnsAsync(new AccountingLotImportResult("USD", 2, 1_500_000_000, 550.12345678m, 1_500_000_000, 1, 9)
             {
                 AdjustedMsat = -400
             });

        // Act
        var response = await AdminAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.LotsImport,
            Lots = new AccountingLotsIpcRequest
            {
                Currency = "USD",
                Rows =
                [
                    new AccountingLotRowIpc { TimeUnixSeconds = s_june.ToUnixTimeSeconds(), Msat = 1_000_000_000, Cost = "300" },
                    new AccountingLotRowIpc { TimeUnixSeconds = s_june.ToUnixTimeSeconds() + 60, Msat = 500_000_400, Cost = "250.12345678" }
                ]
            }
        });

        // Assert
        Assert.Equal((int)AccountingAdminAction.LotsImport, response.Action);
        Assert.NotNull(lots);
        Assert.Equal([new AccountingLotPoint(s_june, 1_000_000_000, 300m),
                      new AccountingLotPoint(s_june.AddMinutes(1), 500_000_400, 250.12345678m)], lots);
        var import = response.LotImport!;
        Assert.Equal(("USD", 2, 1_500_000_000L, "550.12345678", 1_500_000_000L, 1, 9, -400L),
                     (import.Currency, import.Imported, import.ImportedMsat, import.ImportedCost, import.OpeningMsat,
                      import.ReplacedLots, import.ProjectedEntries, import.AdjustedMsat));
    }

    [Fact]
    public async Task Given_ARefusedImport_When_Sent_Then_ItIsInvalidOperationWithTheReason()
    {
        // Arrange
        _lots.Setup(l => l.ImportAsync(null, It.IsAny<IReadOnlyList<AccountingLotPoint>>(),
                                       It.IsAny<CancellationToken>()))
             .ThrowsAsync(new InvalidOperationException("The period 2026-01 is closed"));

        // Act
        var response = await SendAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.LotsImport,
            Lots = new AccountingLotsIpcRequest
            {
                Rows = [new AccountingLotRowIpc { TimeUnixSeconds = s_june.ToUnixTimeSeconds(), Msat = 1, Cost = "1" }]
            }
        });

        // Assert
        var error = ReadError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("is closed", error.Message);
    }

    [Theory]
    [InlineData("not a cost")]
    [InlineData("")]
    public async Task Given_ARowWithoutACost_When_Sent_Then_ItIsInvalidOperation(string cost)
    {
        // Act
        var response = await SendAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.LotsImport,
            Lots = new AccountingLotsIpcRequest
            {
                Rows = [new AccountingLotRowIpc { TimeUnixSeconds = 1, Msat = 1, Cost = cost }]
            }
        });

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, ReadError(response).Code);
        _lots.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_NoLots_When_Sent_Then_ItIsInvalidOperation()
    {
        // Act
        var response = await SendAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.LotsImport,
            Lots = new AccountingLotsIpcRequest()
        });

        // Assert
        Assert.Contains("No lots", ReadError(response).Message);
    }

    [Fact]
    public async Task Given_NoLotImport_When_Asked_Then_ItIsNotAvailable()
    {
        // Act
        var response = await SendAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.LotsImport,
            Lots = new AccountingLotsIpcRequest
            {
                Rows = [new AccountingLotRowIpc { TimeUnixSeconds = 1, Msat = 1, Cost = "1" }]
            }
        }, withLots: false);

        // Assert
        Assert.Contains("not available", ReadError(response).Message);
    }

    private async Task<AccountingAdminIpcResponse> AdminAsync(AccountingAdminIpcRequest request)
    {
        var response = await SendAsync(request);
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        return MessagePackSerializer.Deserialize<AccountingAdminIpcResponse>(response.Payload, s_options,
                                                                            TestContext.Current.CancellationToken);
    }

    private async Task<IpcEnvelope> SendAsync(AccountingAdminIpcRequest request, bool withLots = true)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => unitOfWork.Object);
        if (withLots)
            services.AddSingleton(_lots.Object);
        services.AddAccountingIpcServices();
        var handler = services.BuildServiceProvider().GetServices<IIpcCommandHandler>()
                              .Single(h => h.Command == ClientCommand.AccountingAdmin);
        return await handler.HandleAsync(new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.AccountingAdmin,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        }, TestContext.Current.CancellationToken);
    }

    private static IpcError ReadError(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }
}