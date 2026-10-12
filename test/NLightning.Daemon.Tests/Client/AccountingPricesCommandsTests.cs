namespace NLightning.Daemon.Tests.Client;

using Domain.Client.Enums;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI's <c>accounting prices import|list|fetch|replace</c> (NL-602 A3-T2, NL-693): the arguments, an import file read on the
/// client with its bad lines reported by number (nothing sent), a large file sent in chunks, and the printer.
/// </summary>
public sealed class AccountingPricesCommandsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nltg-import-{Guid.NewGuid():N}.csv");

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A leftover temp file is harmless
        }
    }

    [Fact]
    public void Given_AnImport_When_Parsed_Then_TheFileAndCurrencyAreKept()
    {
        // Act
        var arguments = AccountingBooksCommands.Parse(["prices", "import", "prices.csv", "--currency", "eur"],
                                                      out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal("prices.csv", arguments!.PricesFile);
        Assert.Equal((int)AccountingAdminAction.PricesImport, arguments.Admin!.Action);
        Assert.Equal("EUR", arguments.Admin.Prices!.Currency);
        Assert.Null(ClientApp.ValidateArguments("accounting", ["prices", "import", "prices.csv"]));
    }

    [Fact]
    public void Given_AListWithEveryOption_When_Parsed_Then_TheRequestCarriesThem()
    {
        // Act
        var request = AccountingBooksCommands.Parse(
                          ["prices", "list", "--since", "2026-09-01", "--until=1790000000", "--limit", "50",
                           "--currency", "USD"], out var error)!.Admin!;

        // Assert
        Assert.Null(error);
        Assert.Equal((int)AccountingAdminAction.PricesList, request.Action);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                     request.Prices!.SinceUnixSeconds);
        Assert.Equal(1_790_000_000, request.Prices.UntilUnixSeconds);
        Assert.Equal(50, request.Prices.Limit);
        Assert.Equal("USD", request.Prices.Currency);
    }

    [Fact]
    public void Given_AFetch_When_Parsed_Then_SinceIsRequired()
    {
        // Act
        var fetch = AccountingBooksCommands.Parse(["prices", "fetch", "--since", "1759406400"], out var error);
        var missing = AccountingBooksCommands.Validate(["prices", "fetch"]);

        // Assert
        Assert.Null(error);
        Assert.Equal((int)AccountingAdminAction.PricesFetch, fetch!.Admin!.Action);
        Assert.Equal(1_759_406_400, fetch.Admin.Prices!.SinceUnixSeconds);
        Assert.NotNull(missing);
        Assert.Contains("Missing --since", missing);
    }

    [Fact]
    public void Given_AReplace_When_Parsed_Then_TheTimePriceAndAuditTextAreSent()
    {
        // Act (NL-693)
        var request = AccountingBooksCommands.Parse(
                          ["prices", "replace", "2025-10-02T12:00:00Z", "86048.5", "--currency", "eur", "--source",
                           "exchange statement", "--note", "the source was off by 10x"], out var error)!.Admin!;

        // Assert
        Assert.Null(error);
        Assert.Equal((int)AccountingAdminAction.PricesReplace, request.Action);
        Assert.Equal(1_759_406_400, request.Prices!.ReplaceTimeUnixSeconds);
        Assert.Equal("86048.5", request.Prices.ReplacePrice);
        Assert.Equal("EUR", request.Prices.Currency);
        Assert.Equal("exchange statement", request.Prices.Source);
        Assert.Equal("the source was off by 10x", request.Prices.Note);
        Assert.Null(ClientApp.ValidateArguments("accounting", ["prices", "replace", "1759406400", "86000"]));
    }

    [Fact]
    public void Given_AReplace_When_Printed_Then_TheOldAndNewPriceAndTheRevaluationAreShown()
    {
        // Arrange
        var output = new StringWriter();
        var price = new AccountingPriceIpc
        {
            Id = 7,
            TimeUnixSeconds = 1_759_406_400,
            Price = "45000",
            Source = 4,
            SourceName = "Manual",
            FetchedAtUnixSeconds = 1_759_500_000
        };

        // Act
        new AccountingPricesPrinter(output).Print(new AccountingPricesIpcResponse
        {
            Currency = "USD",
            Replace = new AccountingPriceReplaceIpc
            {
                Price = price,
                OldPrice = "50000",
                OldSource = 1,
                OldSourceName = "Csv",
                OldFetchedAtUnixSeconds = 1_759_406_400,
                Changed = true,
                ReplayFromLedgerSeq = 12,
                OpenEntries = 3,
                ClosedEntries = 2,
                Adjustments = 2,
                LinesRepriced = 3
            }
        });

        // Assert
        var text = output.ToString();
        Assert.Contains("Replaced the USD price of 2025-10-02 12:00:00Z (id 7): 50000 (Csv", text);
        Assert.Contains("-> 45000 (Manual", text);
        Assert.Contains("3 open-period entr(ies) valued with it are projected again from ledger sequence 12", text);
        Assert.Contains("2 entr(ies) of closed periods valued with it: 2 price adjustment(s) in the open period, 3 "
                      + "line(s) revalued", text);
    }

    [Theory]
    [InlineData(new[] { "prices" }, "Missing prices subcommand")]
    [InlineData(new[] { "prices", "drop" }, "Unknown prices subcommand")]
    [InlineData(new[] { "prices", "import" }, "Missing the price file")]
    [InlineData(new[] { "prices", "import", "f.csv", "--currency", "dollars" }, "Invalid currency")]
    [InlineData(new[] { "prices", "list", "--limit", "0" }, "Invalid limit")]
    [InlineData(new[] { "prices", "list", "--since", "2", "--until", "1" }, "--until must be after --since")]
    [InlineData(new[] { "prices", "fetch", "--since", "x" }, "Invalid since")]
    [InlineData(new[] { "prices", "list", "--format", "csv" }, "Unknown option")]
    [InlineData(new[] { "prices", "replace", "1759406400" }, "Missing the time or the price")]
    [InlineData(new[] { "prices", "replace", "x", "86000" }, "Invalid time")]
    [InlineData(new[] { "prices", "replace", "1759406400", "-5" }, "Invalid price")]
    [InlineData(new[] { "prices", "replace", "1759406400", "86000", "--why", "x" }, "Unknown option")]
    public void Given_BadArguments_When_Validated_Then_TheErrorSaysWhy(string[] args, string expected)
    {
        // Act
        var error = AccountingBooksCommands.Validate(args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public async Task Given_AFileWithBadLines_When_Imported_Then_EveryBadLineIsListedAndNothingIsSent()
    {
        // Arrange
        await File.WriteAllTextAsync(_path, "unixSeconds,price\n1759406400,86000\nbad\n1759410000,-1\n",
                                     TestContext.Current.CancellationToken);
        var arguments = AccountingBooksCommands.Parse(["prices", "import", _path], out _)!;
        var sent = 0;

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AccountingPricesCommands.RunAsync(arguments, (_, _) =>
            {
                sent++;
                return Task.FromResult(new AccountingAdminIpcResponse { Action = 10 });
            }, TextWriter.Null, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(0, sent);
        Assert.Contains("2 bad line(s); nothing was imported", error.Message);
        Assert.Contains($"{_path}: line 3: expected 2 fields", error.Message);
        Assert.Contains($"{_path}: line 4: price -1 must be positive", error.Message);
    }

    [Fact]
    public async Task Given_ALargeFile_When_Imported_Then_ItIsSentInChunksAndTheTotalsArePrinted()
    {
        // Arrange
        var rows = AccountingPricesIpcRequest.MaxRowsPerRequest + 5;
        var lines = Enumerable.Range(0, rows).Select(i => $"{1_700_000_000L + i * 3600L},{1000 + i}.25");
        await File.WriteAllTextAsync(_path, string.Join('\n', lines), TestContext.Current.CancellationToken);
        var arguments = AccountingBooksCommands.Parse(["prices", "import", _path, "--currency", "USD"], out _)!;
        var requests = new List<AccountingAdminIpcRequest>();
        var output = new StringWriter();

        // Act
        await AccountingPricesCommands.RunAsync(arguments, (request, _) =>
        {
            requests.Add(request);
            var count = request.Prices!.Rows!.Count;
            return Task.FromResult(new AccountingAdminIpcResponse
            {
                Action = request.Action,
                Prices = new AccountingPricesIpcResponse
                {
                    Currency = "USD",
                    Import = new AccountingPriceImportIpc
                    {
                        Added = count - 1,
                        AlreadyStored = 1,
                        Valuation = new AccountingValuationRoundIpc
                        {
                            Listed = 4,
                            Valued = 3,
                            Fetched = 0,
                            Stored = 0,
                            LateValuations = 0,
                            ClosedLeftUnvalued = 0,
                            Unpriced = 1
                        }
                    }
                }
            });
        }, output, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([AccountingPricesIpcRequest.MaxRowsPerRequest, 5], requests.Select(r => r.Prices!.Rows!.Count));
        Assert.All(requests, r => Assert.Equal("USD", r.Prices!.Currency));
        Assert.Equal("1000.25", requests[0].Prices!.Rows![0].Price);
        Assert.Equal(1_700_000_000, requests[0].Prices!.Rows![0].TimeUnixSeconds);
        var text = output.ToString();
        Assert.Contains($"Imported {rows - 2} USD price(s); 2 time(s) already had a price", text);
        Assert.Contains("Valued 3 of 4 unvalued posting(s); 1 still without a price", text);
    }

    [Fact]
    public void Given_AListAndAFetch_When_Printed_Then_EveryFieldIsShown()
    {
        // Arrange
        var output = new StringWriter();
        var printer = new AccountingPricesPrinter(output);

        // Act
        printer.Print(new AccountingPricesIpcResponse
        {
            Currency = "USD",
            Prices =
            [
                new AccountingPriceIpc
                {
                    Id = 3,
                    TimeUnixSeconds = 1_759_406_400,
                    Price = "86048.12",
                    Source = 2,
                    SourceName = "Http",
                    FetchedAtUnixSeconds = 1_759_406_500
                }
            ]
        });
        printer.Print(new AccountingPricesIpcResponse
        {
            Currency = "USD",
            Fetch = new AccountingPriceFetchIpc
            {
                SinceUnixSeconds = 1_759_406_400,
                UntilUnixSeconds = 1_759_417_200,
                Hours = 3,
                AlreadyCovered = 1,
                Requested = 2,
                Stored = 2,
                Unavailable = 0
            }
        });

        // Assert
        var text = output.ToString();
        Assert.Contains("1 USD price(s) per BTC", text);
        Assert.Contains("2025-10-02 12:00:00Z", text);
        Assert.Contains("86048.12", text);
        Assert.Contains("Http", text);
        Assert.Contains("Next page: --since 1759406401", text);
        Assert.Contains("3 hour(s), 1 already stored, 2 asked, 2 stored, 0 unavailable", text);
        Assert.Contains("No valuation: the books are off", text);
    }
}