namespace NLightning.Daemon.Tests.Client;

using Domain.Client.Enums;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI's <c>accounting lots import &lt;file&gt;</c> (NL-602 A3-T4, D-A9): the arguments, the lot file read on the
/// client into one request with its bad lines reported by number (nothing sent), and the printer.
/// </summary>
public sealed class AccountingLotsCommandsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nltg-lots-{Guid.NewGuid():N}.csv");

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
        var arguments = AccountingBooksCommands.Parse(["lots", "import", "lots.csv", "--currency", "usd"],
                                                      out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal("lots.csv", arguments!.LotsFile);
        Assert.Equal((int)AccountingAdminAction.LotsImport, arguments.Admin!.Action);
        Assert.Equal("USD", arguments.Admin.Lots!.Currency);
        Assert.Null(ClientApp.ValidateArguments("accounting", ["lots", "import", "lots.csv"]));
    }

    [Theory]
    [InlineData(new[] { "lots" }, "Missing lots subcommand")]
    [InlineData(new[] { "lots", "list" }, "Unknown lots subcommand")]
    [InlineData(new[] { "lots", "import" }, "Missing the lot file")]
    [InlineData(new[] { "lots", "import", "f.csv", "--currency", "dollars" }, "Invalid currency")]
    [InlineData(new[] { "lots", "import", "f.csv", "--since", "1" }, "--since")]
    public void Given_BadArguments_When_Validated_Then_TheErrorSaysWhy(string[] args, string expected)
    {
        // Act
        var error = AccountingBooksCommands.Validate(args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public async Task Given_ALotFile_When_Read_Then_OneRequestCarriesEveryLot()
    {
        // Arrange
        await File.WriteAllTextAsync(_path, "time,sats,cost\n1748736000,1000000,300\n2025-09-01,499999.6,250.5\n",
                                     TestContext.Current.CancellationToken);
        var arguments = AccountingBooksCommands.Parse(["lots", "import", _path], out _)!;

        // Act
        var request = await AccountingLotsCommands.BuildRequestAsync(arguments, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((int)AccountingAdminAction.LotsImport, request.Action);
        Assert.Equal([(1_748_736_000L, 1_000_000_000L, "300"), (1_756_684_800L, 499_999_600L, "250.5")],
                     request.Lots!.Rows!.Select(r => (r.TimeUnixSeconds, r.Msat, r.Cost)).ToArray());
    }

    [Fact]
    public async Task Given_BadLines_When_Read_Then_NothingIsSentAndTheLinesAreListed()
    {
        // Arrange
        await File.WriteAllTextAsync(_path, "1748736000,1000000,300\nnope,1,1\n1748736000,1,-2\n",
                                     TestContext.Current.CancellationToken);
        var arguments = AccountingBooksCommands.Parse(["lots", "import", _path], out _)!;

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                            () => AccountingLotsCommands.BuildRequestAsync(arguments,
                                                                           TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("2 bad line(s)", exception.Message);
        Assert.Contains("line 2:", exception.Message);
        Assert.Contains("line 3:", exception.Message);
    }

    [Fact]
    public void Given_AnImportResult_When_Printed_Then_TheCountsTheAdjustmentAndTheRebuildAreShown()
    {
        // Arrange
        var output = new StringWriter();

        // Act
        new AccountingAdminPrinter(output).Print(new AccountingAdminIpcResponse
        {
            Action = (int)AccountingAdminAction.LotsImport,
            LotImport = new AccountingLotImportIpcResponse
            {
                Currency = "USD",
                Imported = 2,
                ImportedMsat = 1_500_000_000,
                ImportedCost = "550.5",
                OpeningMsat = 1_500_000_000,
                ReplacedLots = 1,
                ProjectedEntries = 9,
                AdjustedMsat = 400
            }
        });

        // Assert
        var text = output.ToString();
        Assert.Contains("Imported 2 lot(s): 1500000000 msat for 550.5 USD", text);
        Assert.Contains("1 lot(s) of an earlier import replaced", text);
        Assert.Contains("The last lot took 400 msat", text);
        Assert.Contains("Rebuilt the financial book: 9 entries projected", text);
    }
}