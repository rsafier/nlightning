namespace NLightning.Daemon.Tests.Client;

using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// The CLI side of <c>listforwards</c> (ClientCommand 40, NL-597): one line per forward, then the totals over the
/// whole filtered set and the HTLCs refused before forwarding since start (NL-598).
/// </summary>
public class ListForwardsPrinterTests
{
    private static readonly string s_separator = new('-', 96);

    [Fact]
    public void Given_AnEmptyPage_When_Printed_Then_ItListsNoneWithZeroTotals()
    {
        // Arrange
        using var output = new StringWriter();
        var response = new ListForwardsIpcResponse
        {
            Forwards = [],
            Summary = new ForwardSummaryIpcResponse
            {
                Pending = 0,
                Offered = 0,
                Fulfilled = 0,
                Failed = 0,
                FulfilledFeesMsat = 0,
                RefusedTotal = 0,
                RefusedByReason = []
            }
        };

        // Act
        new ListForwardsPrinter(output).Print(response);

        // Assert
        Assert.Equal("Forwards:\n"
                   + "  None\n"
                   + s_separator + "\n"
                   + "  Totals over the filtered set: 0 forward(s) - pending 0, offered 0, fulfilled 0, "
                   + "failed 0; fees earned 0 msat\n"
                   + "  Refused before forwarding since start: 0\n",
                     output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Given_AFulfilledForward_When_Printed_Then_TheChannelsTotalsAndRefusalsAreShown()
    {
        // Arrange
        using var output = new StringWriter();
        var response = new ListForwardsIpcResponse
        {
            Forwards =
            [
                new ForwardInfoIpcResponse
                {
                    IncomingChannelId = new string('c', 64),
                    IncomingHtlcId = 1,
                    IncomingAmountMsat = 120_000,
                    IncomingCltvExpiry = 500,
                    OutgoingShortChannelId = "900000x1x0",
                    OutgoingChannelId = new string('d', 64),
                    OutgoingHtlcId = 11,
                    OutgoingAmountMsat = 100_000,
                    OutgoingCltvExpiry = 480,
                    FeeMsat = 20_000,
                    PaymentHash = new string('a', 64),
                    CreatedAtUnixSeconds = 1_790_812_800,
                    ResolvedAtUnixSeconds = 1_790_812_860,
                    Status = 2,
                    IncomingChannelScid = "800000x12x0",
                    OutgoingChannelScid = "900000x1x0"
                }
            ],
            Summary = new ForwardSummaryIpcResponse
            {
                Pending = 0,
                Offered = 0,
                Fulfilled = 1,
                Failed = 0,
                FulfilledFeesMsat = 20_000,
                RefusedTotal = 3,
                RefusedByReason =
                [
                    new RefusedReasonCountIpc { Reason = "ShutdownDrain", Count = 2 },
                    new RefusedReasonCountIpc { Reason = "UnknownNextChannel", Count = 1 }
                ]
            }
        };

        // Act
        new ListForwardsPrinter(output).Print(response);

        // Assert
        Assert.Equal("Forwards:\n"
                   + s_separator + "\n"
                   + "  Status:      fulfilled\n"
                   + "  Created:     2026-10-01 00:00:00 UTC   Resolved: 2026-10-01 00:01:00 UTC\n"
                   + "  In:          800000x12x0  HTLC 1  120000 msat  cltv 500\n"
                   + "  Out:         900000x1x0  100000 msat  cltv 480\n"
                   + "  Fee:         20000 msat   Hash: " + new string('a', 16) + "…\n"
                   + s_separator + "\n"
                   + "  Totals over the filtered set: 1 forward(s) - pending 0, offered 0, fulfilled 1, "
                   + "failed 0; fees earned 20000 msat\n"
                   + "  Refused before forwarding since start: 3 (ShutdownDrain 2, UnknownNextChannel 1)\n",
                     output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Given_AForwardWithoutScids_When_Printed_Then_TheChannelIdsAreShown()
    {
        // Arrange: an older daemon sends no scid fields, so the channel ids stand in
        using var output = new StringWriter();
        var response = new ListForwardsIpcResponse
        {
            Forwards =
            [
                new ForwardInfoIpcResponse
                {
                    IncomingChannelId = new string('c', 64),
                    IncomingHtlcId = 1,
                    IncomingAmountMsat = 120_000,
                    IncomingCltvExpiry = 500,
                    OutgoingShortChannelId = "900000x1x0",
                    OutgoingChannelId = new string('d', 64),
                    OutgoingHtlcId = 11,
                    OutgoingAmountMsat = 100_000,
                    OutgoingCltvExpiry = 480,
                    FeeMsat = 20_000,
                    PaymentHash = new string('a', 64),
                    CreatedAtUnixSeconds = 1_790_812_800,
                    Status = 2
                }
            ],
            Summary = new ForwardSummaryIpcResponse
            {
                Pending = 0,
                Offered = 0,
                Fulfilled = 1,
                Failed = 0,
                FulfilledFeesMsat = 20_000,
                RefusedTotal = 0,
                RefusedByReason = []
            }
        };

        // Act
        new ListForwardsPrinter(output).Print(response);

        // Assert
        var lines = output.ToString().ReplaceLineEndings("\n").Split('\n');
        Assert.Contains("  In:          " + new string('c', 64) + "  HTLC 1  120000 msat  cltv 500", lines);
        // NL-597: the forward was offered on a channel we could not resolve a scid for, so its channel id stands in
        Assert.Contains("  Out:         " + new string('d', 64) + "  100000 msat  cltv 480", lines);
        Assert.Contains("  Refused before forwarding since start: 0", lines);
    }

    [Theory]
    [InlineData("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd", "  Failed:      downstream of 900000x1x0")]
    [InlineData(null, "  Failed:      not offered on 900000x1x0")]
    public void Given_AFailedForward_When_Printed_Then_ItEarnsNoFeeAndSaysWhereItFailed(string? outgoingChannelId,
                                                                                        string expectedFailedLine)
    {
        // Arrange: offered and failed downstream, or refused before the offer went out
        using var output = new StringWriter();
        var response = new ListForwardsIpcResponse
        {
            Forwards =
            [
                new ForwardInfoIpcResponse
                {
                    IncomingChannelId = new string('c', 64),
                    IncomingChannelScid = "800000x1x0",
                    IncomingHtlcId = 1,
                    IncomingAmountMsat = 2_000,
                    IncomingCltvExpiry = 500,
                    OutgoingShortChannelId = "900000x1x0",
                    OutgoingChannelId = outgoingChannelId,
                    OutgoingChannelScid = outgoingChannelId is null ? null : "900000x1x0",
                    OutgoingAmountMsat = 1_000,
                    OutgoingCltvExpiry = 460,
                    FeeMsat = 1_000,
                    PaymentHash = new string('a', 64),
                    CreatedAtUnixSeconds = 1_790_812_800,
                    Status = 3,
                    FailureSource = new string('d', 64),
                    FailureSourceScid = "900000x1x0"
                }
            ],
            Summary = new ForwardSummaryIpcResponse
            {
                Pending = 0,
                Offered = 0,
                Fulfilled = 0,
                Failed = 1,
                FulfilledFeesMsat = 0,
                RefusedTotal = 0,
                RefusedByReason = []
            }
        };

        // Act
        new ListForwardsPrinter(output).Print(response);

        // Assert
        var lines = output.ToString().ReplaceLineEndings("\n").Split('\n');
        Assert.Contains("  Fee:         none (failed)   Hash: " + new string('a', 16) + "…", lines);
        Assert.Contains(expectedFailedLine, lines);
    }

    [Fact]
    public void Given_ATrampolineRelay_When_Printed_Then_ItIsListedWithItsKindPartsAndFee()
    {
        // Arrange (NL-875 TR3-T3)
        using var output = new StringWriter();
        var response = new ListForwardsIpcResponse
        {
            Forwards = [],
            Summary = new ForwardSummaryIpcResponse
            {
                Pending = 0,
                Offered = 0,
                Fulfilled = 0,
                Failed = 0,
                FulfilledFeesMsat = 0,
                RefusedTotal = 0,
                RefusedByReason = []
            },
            TrampolineRelays =
            [
                new TrampolineRelayIpcResponse
                {
                    PaymentHash = new string('a', 64),
                    Status = 2,
                    Parts = 2,
                    IncomingChannelIds = [new string('1', 64)],
                    IncomingChannelScids = ["800000x12x0"],
                    IncomingAmountMsat = 1_010_000,
                    IncomingTotalMsat = 1_010_000,
                    AmountOutMsat = 1_000_000,
                    FeeEarnedMsat = 5_000,
                    NextNodeId = "03" + new string('4', 64),
                    CreatedAtUnixSeconds = 1_790_812_800
                }
            ]
        };

        // Act
        new ListForwardsPrinter(output).Print(response);

        // Assert
        var text = output.ToString().ReplaceLineEndings("\n");
        Assert.Contains("Trampoline relays:\n", text);
        Assert.Contains("  Kind:        trampoline   Status: fulfilled\n", text);
        Assert.Contains("  In:          2 part(s), 1010000 of 1010000 msat  on 800000x12x0\n", text);
        Assert.Contains("  Out:         1000000 msat to 03" + new string('4', 64) + "\n", text);
        Assert.Contains("  Fee:         5000 msat   Hash: aaaaaaaaaaaaaaaa…\n", text);
    }
}