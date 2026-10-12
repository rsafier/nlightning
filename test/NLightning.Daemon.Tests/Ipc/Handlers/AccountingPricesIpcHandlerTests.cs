using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Financial;
using Domain.Accounting.Prices;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Persistence.Interfaces;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>accounting prices import|list|fetch|replace</c> over IPC 45 (NL-602 A3-T2, NL-693): the rows, the range and the answers cross the
/// envelope, the service's refusals are <c>invalid_operation</c>, and a node without the price service says so.
/// </summary>
public class AccountingPricesIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_hour = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly Mock<IAccountingPrices> _prices = new();

    public AccountingPricesIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _prices.SetupGet(p => p.Currency).Returns("USD");
    }

    [Fact]
    public async Task Given_AnImport_When_Sent_Then_TheRowsReachTheServiceAndTheCountsComeBack()
    {
        // Arrange
        IReadOnlyList<AccountingPricePoint>? points = null;
        _prices.Setup(p => p.ImportAsync("EUR", It.IsAny<IReadOnlyList<AccountingPricePoint>>(),
                                         It.IsAny<CancellationToken>()))
               .Callback((string? _, IReadOnlyList<AccountingPricePoint> sent, CancellationToken _) => points = sent)
               .ReturnsAsync(new AccountingPriceImportResult("EUR", 1, 1,
                                                             new AccountingValuationRoundResult(4, 2, 0, 0, 0, 0, 2)
                                                             {
                                                                 Deferred = 1
                                                             }));

        // Act
        var response = await AdminAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.PricesImport,
            Prices = new AccountingPricesIpcRequest
            {
                Currency = "EUR",
                Rows =
                [
                    new AccountingPriceRowIpc { TimeUnixSeconds = s_hour.ToUnixTimeSeconds(), Price = "79000.5" },
                    new AccountingPriceRowIpc { TimeUnixSeconds = s_hour.ToUnixTimeSeconds() + 3600, Price = "79100" }
                ]
            }
        });

        // Assert
        Assert.Equal((int)AccountingAdminAction.PricesImport, response.Action);
        Assert.NotNull(points);
        Assert.Equal([79_000.5m, 79_100m], points.Select(p => p.Price));
        Assert.Equal([s_hour, s_hour.AddHours(1)], points.Select(p => p.Time));
        var import = response.Prices!.Import!;
        Assert.Equal("EUR", response.Prices.Currency);
        Assert.Equal(1, import.Added);
        Assert.Equal(1, import.AlreadyStored);
        Assert.Equal(2, import.Valuation!.Valued);
        Assert.Equal(2, import.Valuation.Unpriced);
        Assert.Equal(1, import.Valuation.Deferred);
    }

    [Fact]
    public async Task Given_AList_When_Sent_Then_ThePricesComeBackAsInvariantText()
    {
        // Arrange
        _prices.Setup(p => p.ListAsync(null, s_hour, null, 5, It.IsAny<CancellationToken>()))
               .ReturnsAsync([
                   new AccountingPrice(7, "USD", s_hour, 86_048.12345678m, AccountingPriceSource.Http,
                                       s_hour.AddMinutes(5))
               ]);
        _prices.Setup(p => p.ListReplacementAuditsAsync(It.Is<IReadOnlyCollection<long>>(ids => ids.Count == 1 && ids.Contains(7)),
            It.IsAny<CancellationToken>())).ReturnsAsync([
                new AccountingPriceReplacementAudit(9, 7, 80_000.5m, 86_048.12345678m, AccountingPriceSource.Csv,
                    AccountingPriceSource.Manual, s_hour, s_hour.AddMinutes(5), "exchange statement", "correct quote")
            ]);

        // Act
        var response = await AdminAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.PricesList,
            Prices = new AccountingPricesIpcRequest { SinceUnixSeconds = s_hour.ToUnixTimeSeconds(), Limit = 5 }
        });

        // Assert
        var price = Assert.Single(response.Prices!.Prices!);
        Assert.Equal("USD", response.Prices.Currency);
        Assert.Equal(7, price.Id);
        Assert.Equal(s_hour.ToUnixTimeSeconds(), price.TimeUnixSeconds);
        Assert.Equal("86048.12345678", price.Price);
        Assert.Equal((int)AccountingPriceSource.Http, price.Source);
        Assert.Equal("Http", price.SourceName);
        Assert.Equal(s_hour.AddMinutes(5).ToUnixTimeSeconds(), price.FetchedAtUnixSeconds);
        var correction = Assert.Single(response.Prices.Replacements!);
        Assert.Equal(7, correction.PriceId);
        Assert.Equal("80000.5", correction.OldPrice);
        Assert.Equal("86048.12345678", correction.NewPrice);
        Assert.Equal("Csv", correction.OldSource);
        Assert.Equal("Manual", correction.NewSource);
        Assert.Equal("exchange statement", correction.OperatorSource);
        Assert.Equal("correct quote", correction.Note);
        Assert.Equal(s_hour.ToUnixTimeSeconds(), correction.OldFetchedAtUnixSeconds);
        Assert.Equal(s_hour.AddMinutes(5).ToUnixTimeSeconds(), correction.ReplacedAtUnixSeconds);
    }

    [Fact]
    public async Task Given_AFetch_When_Sent_Then_TheRangeReachesTheServiceAndTheCountsComeBack()
    {
        // Arrange
        _prices.Setup(p => p.FetchAsync(s_hour, s_hour.AddHours(3), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AccountingPriceFetchResult("USD", s_hour, s_hour.AddHours(3), 3, 1, 2, 1, 1, null));

        // Act
        var response = await AdminAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.PricesFetch,
            Prices = new AccountingPricesIpcRequest
            {
                SinceUnixSeconds = s_hour.ToUnixTimeSeconds(),
                UntilUnixSeconds = s_hour.AddHours(3).ToUnixTimeSeconds()
            }
        });

        // Assert
        var fetch = response.Prices!.Fetch!;
        Assert.Equal(3, fetch.Hours);
        Assert.Equal(1, fetch.AlreadyCovered);
        Assert.Equal(2, fetch.Requested);
        Assert.Equal(1, fetch.Stored);
        Assert.Equal(1, fetch.Unavailable);
        Assert.Null(fetch.Valuation);
    }

    [Fact]
    public async Task Given_AReplace_When_Sent_Then_TheCorrectionReachesTheServiceAndTheRevaluationComesBack()
    {
        // Arrange (NL-693)
        AccountingPriceReplacement? sent = null;
        var replaced = new AccountingPrice(7, "USD", s_hour, 45_000m, AccountingPriceSource.Manual,
                                           s_hour.AddDays(1));
        _prices.Setup(p => p.ReplaceAsync(It.IsAny<AccountingPriceReplacement>(), It.IsAny<CancellationToken>()))
               .Callback((AccountingPriceReplacement r, CancellationToken _) => sent = r)
               .ReturnsAsync(new AccountingPriceReplaceResult(replaced, 50_000m, AccountingPriceSource.Http, s_hour,
                                                              true, 12, 3, 2, 2, 3));

        // Act
        var response = await AdminAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.PricesReplace,
            Prices = new AccountingPricesIpcRequest
            {
                ReplaceTimeUnixSeconds = s_hour.ToUnixTimeSeconds(),
                ReplacePrice = "45000",
                Source = " statement ",
                Note = "10x"
            }
        });

        // Assert
        Assert.Equal(new AccountingPriceReplacement(null, s_hour, 45_000m, "statement", "10x"), sent);
        var replace = response.Prices!.Replace!;
        Assert.Equal("USD", response.Prices.Currency);
        Assert.Equal(7, replace.Price.Id);
        Assert.Equal("45000", replace.Price.Price);
        Assert.Equal("Manual", replace.Price.SourceName);
        Assert.Equal("50000", replace.OldPrice);
        Assert.Equal("Http", replace.OldSourceName);
        Assert.True(replace.Changed);
        Assert.Equal(12, replace.ReplayFromLedgerSeq);
        Assert.Equal((3, 2, 2, 3),
                     (replace.OpenEntries, replace.ClosedEntries, replace.Adjustments, replace.LinesRepriced));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a price")]
    public async Task Given_AReplaceWithoutAPrice_When_Sent_Then_ItIsInvalidOperation(string? price)
    {
        // Act
        var response = await SendAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.PricesReplace,
            Prices = price is null
                         ? new AccountingPricesIpcRequest()
                         : new AccountingPricesIpcRequest
                         {
                             ReplaceTimeUnixSeconds = s_hour.ToUnixTimeSeconds(),
                             ReplacePrice = price
                         }
        });

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, ReadError(response).Code);
        _prices.Verify(p => p.ReplaceAsync(It.IsAny<AccountingPriceReplacement>(), It.IsAny<CancellationToken>()),
                       Times.Never);
    }

    [Fact]
    public async Task Given_TheServiceRefuses_When_Sent_Then_ItIsInvalidOperationWithItsMessage()
    {
        // Arrange
        _prices.Setup(p => p.FetchAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset?>(),
                                        It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("Accounting:Prices:Source is None"));

        // Act
        var response = await SendAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.PricesFetch,
            Prices = new AccountingPricesIpcRequest { SinceUnixSeconds = s_hour.ToUnixTimeSeconds() }
        });

        // Assert
        var error = ReadError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("Source is None", error.Message);
    }

    [Theory]
    [InlineData("not a price")]
    [InlineData("")]
    public async Task Given_ARowThatIsNotAPrice_When_Sent_Then_ItIsInvalidOperation(string price)
    {
        // Act
        var response = await SendAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.PricesImport,
            Prices = new AccountingPricesIpcRequest
            {
                Rows = [new AccountingPriceRowIpc { TimeUnixSeconds = 1, Price = price }]
            }
        });

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, ReadError(response).Code);
        _prices.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_AFetchWithoutSince_When_Sent_Then_ItIsInvalidOperation()
    {
        // Act
        var response = await SendAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.PricesFetch,
            Prices = new AccountingPricesIpcRequest()
        });

        // Assert
        Assert.Contains("--since", ReadError(response).Message);
    }

    [Fact]
    public async Task Given_NoPriceService_When_AskedForPrices_Then_ItIsNotAvailable()
    {
        // Act
        var response = await SendAsync(new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.PricesList,
            Prices = new AccountingPricesIpcRequest()
        }, withPrices: false);

        // Assert
        var error = ReadError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("not available", error.Message);
    }

    private async Task<AccountingAdminIpcResponse> AdminAsync(AccountingAdminIpcRequest request)
    {
        var response = await SendAsync(request);
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        return MessagePackSerializer.Deserialize<AccountingAdminIpcResponse>(response.Payload, s_options,
                                                                            TestContext.Current.CancellationToken);
    }

    private async Task<IpcEnvelope> SendAsync(AccountingAdminIpcRequest request, bool withPrices = true)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => unitOfWork.Object);
        if (withPrices)
            services.AddSingleton(_prices.Object);
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