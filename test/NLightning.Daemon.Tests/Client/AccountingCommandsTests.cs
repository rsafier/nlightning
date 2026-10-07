namespace NLightning.Daemon.Tests.Client;

using Domain.Accounting.Enums;
using NLightning.Client;
using NLightning.Client.Handlers;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of <c>listaccountingevents</c> (ClientCommand 41) and <c>accountingsnapshot</c> (42), NL-602:
/// argument parsing and the printed results.
/// </summary>
public class AccountingCommandsTests
{
    [Fact]
    public void Given_EveryOption_When_Parsed_Then_TheRequestCarriesThem()
    {
        // Arrange
        string[] args =
        [
            "--after", "12", "--limit=50", "--kind", "invoicesettled,ForwardSettled", "--kind", "31",
            "--channel", "800000x1x0", "--since", "2026-10-01", "--until=1790000000"
        ];

        // Act
        var request = AccountingCommands.ParseListAccountingEventsOptions(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(12, request.AfterLedgerSeq);
        Assert.Equal(50, request.Limit);
        Assert.Equal([
                         (int)AccountingEventKind.InvoiceSettled, (int)AccountingEventKind.ForwardSettled,
                         (int)AccountingEventKind.WalletSent
                     ], request.Kinds!);
        Assert.Equal("800000x1x0", request.Channel);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                     request.SinceUnixSeconds);
        Assert.Equal(1_790_000_000, request.UntilUnixSeconds);
        Assert.Null(ClientApp.ValidateArguments("listaccountingevents", args));
        Assert.Null(ClientApp.ValidateArguments("list-accounting-events", args));
    }

    [Fact]
    public void Given_NoOptions_When_Parsed_Then_TheDefaultsApply()
    {
        // Act
        var request = AccountingCommands.ParseListAccountingEventsOptions([], out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(0, request!.AfterLedgerSeq);
        Assert.Equal(100, request.Limit);
        Assert.Null(request.Kinds);
        Assert.Null(request.Channel);
    }

    [Theory]
    [InlineData(new[] { "--after", "-1" }, "Invalid after")]
    [InlineData(new[] { "--limit", "0" }, "Invalid limit")]
    [InlineData(new[] { "--limit", "1001" }, "Invalid limit")]
    [InlineData(new[] { "--kind", "Coffee" }, "Unknown kind 'Coffee'")]
    [InlineData(new[] { "--kind", "9" }, "Unknown kind '9'")]
    [InlineData(new[] { "--kind", "," }, "Missing value for --kind")]
    [InlineData(new[] { "--since", "yesterday" }, "Invalid since")]
    [InlineData(new[] { "--until" }, "Missing value for --until")]
    [InlineData(new[] { "--color", "red" }, "Unknown option")]
    [InlineData(new[] { "12" }, "Unexpected argument")]
    public void Given_ABadArgument_When_Validated_Then_UsageError(string[] args, string expected)
    {
        // Act
        var error = ClientApp.ValidateArguments("listaccountingevents", args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expected, error);
        Assert.Contains("Usage: listaccountingevents", error);
    }

    [Fact]
    public void Given_AccountingSnapshot_When_Validated_Then_ItTakesNoArguments()
    {
        // Act & Assert
        Assert.Null(ClientApp.ValidateArguments("accountingsnapshot", []));
        Assert.Null(ClientApp.ValidateArguments("accounting-snapshot", []));
        Assert.NotNull(ClientApp.ValidateArguments("accountingsnapshot", ["now"]));
    }

    [Fact]
    public void Given_APage_When_Printed_Then_EachEventAndTheNextCursorAreShown()
    {
        // Arrange
        var page = new ListAccountingEventsIpcResponse
        {
            Events =
            [
                new AccountingEventIpcResponse
                {
                    LedgerSeq = 7,
                    EventKey = "fwd:ab:1:settled",
                    Kind = (int)AccountingEventKind.ForwardSettled,
                    KindName = "ForwardSettled",
                    OccurredAtUnixMilliseconds = new DateTimeOffset(2026, 10, 2, 12, 0, 0, 250, TimeSpan.Zero)
                       .ToUnixTimeMilliseconds(),
                    AmountMsat = 1_500,
                    FeeMsat = 0,
                    ChannelId = new string('c', 64),
                    ShortChannelId = "800000x1x0",
                    PaymentHash = new string('d', 64),
                    Counterparty = "02" + new string('e', 64),
                    Finality = 0,
                    FinalityName = "Final",
                    Flags = 2,
                    Details = new Dictionary<string, string> { ["out"] = "800001x2x0" }
                },
                new AccountingEventIpcResponse
                {
                    LedgerSeq = 8,
                    EventKey = "wsend:ff",
                    Kind = (int)AccountingEventKind.WalletSent,
                    KindName = "WalletSent",
                    OccurredAtUnixMilliseconds = 0,
                    BlockHeight = 800_100,
                    AmountMsat = -2_000_000,
                    FeeMsat = 141_000,
                    TxId = new string('f', 64),
                    OutputIndex = 1,
                    Finality = 1,
                    FinalityName = "Confirmed",
                    Flags = 0,
                    Details = []
                }
            ],
            NextAfter = 8,
            HasMore = false,
            ChainTipLedgerSeq = 8
        };
        using var output = new StringWriter();

        // Act
        new ListAccountingEventsPrinter(output).Print(page);

        // Assert
        var printed = output.ToString();
        Assert.Contains("#7  2026-10-02 12:00:00.250 UTC  ForwardSettled  Final", printed);
        Assert.Contains("+1500 msat", printed);
        Assert.Contains($"800000x1x0 ({new string('c', 64)})", printed);
        Assert.Contains("Flags:       duplicate", printed);
        Assert.Contains("Details:     out=800001x2x0", printed);
        Assert.Contains("WalletSent  Confirmed at block 800100", printed);
        Assert.Contains("-2000000 msat   Fee: 141000 msat", printed);
        Assert.Contains($"{new string('f', 64)}:1", printed);
        Assert.Contains("Next page: --after 8 (nothing more yet)", printed);
    }

    [Fact]
    public void Given_ASnapshot_When_Printed_Then_TotalsWalletAndChannelsAreShown()
    {
        // Arrange
        var snapshot = new AccountingSnapshotIpcResponse
        {
            TakenAtUnixMilliseconds = 0,
            BlockHeight = 800_000,
            Channels =
            [
                new AccountingChannelBucketIpc
                {
                    ChannelId = new string('a', 64),
                    ShortChannelId = "800000x1x0",
                    State = 22,
                    StateName = "Open",
                    Counterparty = "02" + new string('b', 64),
                    CapacityMsat = 1_000_000_000,
                    LocalBalanceMsat = 600_000_000,
                    RemoteBalanceMsat = 400_000_000,
                    LocalInFlightMsat = 10_000_000,
                    RemoteInFlightMsat = 0,
                    PendingOnchainMsat = 0,
                    PendingHtlcOnchainMsat = 0,
                    PendingSweepCount = 0,
                    IsLoaded = true
                },
                new AccountingChannelBucketIpc
                {
                    ChannelId = new string('c', 64),
                    State = 37,
                    StateName = "OnchainResolving",
                    CapacityMsat = 0,
                    LocalBalanceMsat = 0,
                    RemoteBalanceMsat = 0,
                    LocalInFlightMsat = 0,
                    RemoteInFlightMsat = 0,
                    PendingOnchainMsat = 20_000_000,
                    PendingHtlcOnchainMsat = 7_000_000,
                    PendingSweepCount = 2,
                    IsLoaded = false
                }
            ],
            WalletConfirmedMsat = 2_000_000,
            WalletUnconfirmedMsat = 300_000,
            WalletLockedMsat = 100_000,
            ChannelLocalMsat = 600_000_000,
            PendingOnchainMsat = 20_000_000,
            PendingHtlcOnchainMsat = 7_000_000,
            PendingSweepCount = 2,
            TotalMsat = 629_300_000
        };
        using var output = new StringWriter();

        // Act
        new AccountingSnapshotPrinter(output).Print(snapshot);

        // Assert
        var printed = output.ToString();
        Assert.Contains("block 800000", printed);
        Assert.Contains("Total:               629300000 msat", printed);
        Assert.Contains("Pending on chain:    27000000 msat (7000000 msat in HTLC outputs, 2 output(s))", printed);
        Assert.Contains("2000000 msat confirmed, 300000 msat unconfirmed, 100000 msat locked", printed);
        Assert.Contains("Channels (2):", printed);
        Assert.Contains("In flight:   ours 10000000 msat, theirs 0 msat", printed);
        Assert.Contains($"{new string('c', 64)}  OnchainResolving (not loaded)", printed);
    }
}