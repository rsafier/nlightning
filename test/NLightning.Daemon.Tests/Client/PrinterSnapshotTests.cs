namespace NLightning.Daemon.Tests.Client;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.Onion.Enums;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// Snapshots of the CLI output for the invoice/payment commands, <c>listchannels</c> and the graph listings. The output is
/// culture-invariant, so scripts can parse it; change a snapshot only on purpose.
/// </summary>
public class PrinterSnapshotTests
{
    private const string Separator =
        "----------------------------------------------------------------------------------";

    private static readonly Hash s_paymentHash = new(Enumerable.Repeat((byte)0xab, 32).ToArray());
    private static readonly Secret s_preimage = new(Enumerable.Repeat((byte)0xcd, 32).ToArray());
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x07, 32).ToArray());
    private static readonly CompactPubKey s_payee = new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);
    private static readonly DateTimeOffset s_createdAt = new(2026, 9, 25, 12, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void Given_CreatedInvoice_When_Printed_Then_MatchesSnapshot()
    {
        // Arrange
        var response = new CreateInvoiceIpcResponse
        {
            Invoice = new InvoiceInfoIpcResponse
            {
                Bolt11 = "lnbcrt500u1ptest",
                PaymentHash = s_paymentHash,
                Amount = LightningMoney.MilliSatoshis(50_000_123),
                Description = "coffee",
                Status = InvoiceStatus.Open,
                CreatedAt = s_createdAt,
                ExpiresAt = s_createdAt.AddHours(1)
            }
        };

        // Act
        var output = Print(w => new CreateInvoicePrinter(w).Print(response));

        // Assert
        Assert.Equal(Lines(
                         "Invoice:",
                         "  Bolt11:             lnbcrt500u1ptest",
                         $"  Payment Hash:       {Hex(0xab)}",
                         "  Amount (msat):      50000123",
                         "  Description:        coffee",
                         "  Status:             Open",
                         "  Created:            2026-09-25 10:00:00Z",
                         "  Expires:            2026-09-25 11:00:00Z"), output);
    }

    [Fact]
    public void Given_InvoiceList_When_Printed_Then_MatchesSnapshot()
    {
        // Arrange
        var response = new ListInvoicesIpcResponse
        {
            Invoices =
            [
                new InvoiceInfoIpcResponse
                {
                    Bolt11 = "lnbcrt1settled",
                    PaymentHash = s_paymentHash,
                    Amount = LightningMoney.MilliSatoshis(1_000),
                    Status = InvoiceStatus.Settled,
                    CreatedAt = s_createdAt,
                    ExpiresAt = s_createdAt.AddHours(1),
                    AmountReceived = LightningMoney.MilliSatoshis(1_001),
                    SettledAt = s_createdAt.AddMinutes(5)
                },
                new InvoiceInfoIpcResponse
                {
                    Bolt11 = "lnbcrt1expired",
                    PaymentHash = s_paymentHash,
                    Status = InvoiceStatus.Open,
                    CreatedAt = s_createdAt,
                    ExpiresAt = s_createdAt.AddHours(1),
                    IsExpired = true
                }
            ]
        };

        // Act
        var output = Print(w => new ListInvoicesPrinter(w).Print(response));

        // Assert
        Assert.Equal(Lines(
                         "Invoices:",
                         Separator,
                         "  Bolt11:             lnbcrt1settled",
                         $"  Payment Hash:       {Hex(0xab)}",
                         "  Amount (msat):      1000",
                         "  Description:        -",
                         "  Status:             Settled",
                         "  Created:            2026-09-25 10:00:00Z",
                         "  Expires:            2026-09-25 11:00:00Z",
                         "  Received (msat):    1001",
                         "  Settled:            2026-09-25 10:05:00Z",
                         Separator,
                         "  Bolt11:             lnbcrt1expired",
                         $"  Payment Hash:       {Hex(0xab)}",
                         "  Amount (msat):      any",
                         "  Description:        -",
                         "  Status:             Open (expired)",
                         "  Created:            2026-09-25 10:00:00Z",
                         "  Expires:            2026-09-25 11:00:00Z",
                         Separator), output);
    }

    [Fact]
    public void Given_NoInvoicesOrPayments_When_Printed_Then_None()
    {
        // Act
        var invoices = Print(w => new ListInvoicesPrinter(w).Print(new ListInvoicesIpcResponse { Invoices = [] }));
        var payments = Print(w => new ListPaymentsPrinter(w).Print(new ListPaymentsIpcResponse { Payments = [] }));

        // Assert
        Assert.Equal(Lines("Invoices:", "  None"), invoices);
        Assert.Equal(Lines("Payments:", "  None"), payments);
    }

    [Fact]
    public void Given_SucceededPayment_When_Printed_Then_MatchesSnapshot()
    {
        // Arrange
        var response = new PayInvoiceIpcResponse
        {
            Payment = new PaymentInfoIpcResponse
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
            },
            Attempts = 3,
            Parts = 2
        };

        // Act
        var output = Print(w => new PayInvoicePrinter(w).Print(response));

        // Assert
        Assert.Equal(Lines(
                         "Payment:",
                         $"  Payment Hash:       {Hex(0xab)}",
                         "  Status:             Succeeded",
                         $"  Payee:              02{Hex(0x11)}",
                         "  Amount (msat):      50000123",
                         "  Fee (msat):         3025",
                         $"  Preimage:           {Hex(0xcd)}",
                         $"  Outgoing HTLC:      {Hex(0x07)} #4",
                         "  Created:            2026-09-25 10:00:00Z",
                         "  Completed:          2026-09-25 10:00:03Z",
                         "  Bolt11:             lnbcrt1pay",
                         "  Attempts: 3 HTLC(s), at most 2 in flight at once"), output);
    }

    [Fact]
    public void Given_FailedAndInFlightPayments_When_Printed_Then_MatchesSnapshot()
    {
        // Arrange
        var failed = new PaymentInfoIpcResponse
        {
            PaymentHash = s_paymentHash,
            PayeeNodeId = s_payee,
            Amount = LightningMoney.MilliSatoshis(1_000),
            Fee = LightningMoney.Zero,
            Status = PaymentStatus.Failed,
            FailureCode = FailureCode.IncorrectOrUnknownPaymentDetails,
            FailureSourceIndex = 3,
            FailureReason = "payee refused",
            CreatedAt = s_createdAt,
            CompletedAt = s_createdAt.AddSeconds(1)
        };
        var unreadable = new PaymentInfoIpcResponse
        {
            PaymentHash = s_paymentHash,
            PayeeNodeId = s_payee,
            Amount = LightningMoney.MilliSatoshis(1_000),
            Fee = LightningMoney.Zero,
            Status = PaymentStatus.Failed,
            FailureCode = (FailureCode)0x4099,
            CreatedAt = s_createdAt,
            CompletedAt = s_createdAt.AddSeconds(1)
        };

        // Act
        var list = Print(w => new ListPaymentsPrinter(w).Print(new ListPaymentsIpcResponse
        {
            Payments = [failed, unreadable]
        }));
        var inFlight = Print(w => new PayInvoicePrinter(w).Print(new PayInvoiceIpcResponse
        {
            Payment = new PaymentInfoIpcResponse
            {
                PaymentHash = s_paymentHash,
                PayeeNodeId = s_payee,
                Amount = LightningMoney.MilliSatoshis(1_000),
                Fee = LightningMoney.Zero,
                Status = PaymentStatus.InFlight,
                CreatedAt = s_createdAt
            }
        }));

        // Assert
        Assert.Equal(Lines(
                         "Payments:",
                         Separator,
                         $"  Payment Hash:       {Hex(0xab)}",
                         "  Status:             Failed",
                         $"  Payee:              02{Hex(0x11)}",
                         "  Amount (msat):      1000",
                         "  Fee (msat):         0",
                         "  Failure:            IncorrectOrUnknownPaymentDetails (0x400f) at hop 3",
                         "  Failure Reason:     payee refused",
                         "  Created:            2026-09-25 10:00:00Z",
                         "  Completed:          2026-09-25 10:00:01Z",
                         Separator,
                         $"  Payment Hash:       {Hex(0xab)}",
                         "  Status:             Failed",
                         $"  Payee:              02{Hex(0x11)}",
                         "  Amount (msat):      1000",
                         "  Fee (msat):         0",
                         "  Failure:            Unknown (0x4099)",
                         "  Created:            2026-09-25 10:00:00Z",
                         "  Completed:          2026-09-25 10:00:01Z",
                         Separator), list);
        Assert.Equal(Lines(
                         "Payment:",
                         $"  Payment Hash:       {Hex(0xab)}",
                         "  Status:             InFlight",
                         $"  Payee:              02{Hex(0x11)}",
                         "  Amount (msat):      1000",
                         "  Fee (msat):         0",
                         "  Created:            2026-09-25 10:00:00Z",
                         "  The payment is still in flight; check it later with listpayments."), inFlight);
    }

    [Fact]
    public void Given_Channel_When_Printed_Then_MatchesSnapshotWithReestablishedAndFeePolicy()
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
                    IsInitiator = true,
                    IsPeerConnected = true,
                    ShortChannelId = (120UL << 40) | (3UL << 16) | 1UL,
                    Capacity = LightningMoney.Satoshis(2_000_000),
                    LocalBalance = LightningMoney.MilliSatoshis(1_949_996_852),
                    RemoteBalance = LightningMoney.MilliSatoshis(50_003_148),
                    LocalCommitmentNumber = 2,
                    RemoteCommitmentNumber = 2,
                    OfferedHtlcCount = 0,
                    ReceivedHtlcCount = 1,
                    IsReestablished = true,
                    FeeBaseMsat = 1_000,
                    FeePpm = 100
                }
            ]
        };

        // Act
        var output = Print(w => new ListChannelsPrinter(w).Print(response));

        // Assert
        Assert.Equal(Lines(
                         "Channels:",
                         Separator,
                         $"  Id:                 {Hex(0x07)}",
                         $"  Peer:               02{Hex(0x11)} (connected)",
                         "  State:              Open",
                         "  Reestablished:      Yes",
                         "  Initiator:          Yes",
                         "  Short Channel Id:   120x3x1",
                         "  Funding Output:     -",
                         "  Capacity (sat):     2000000",
                         "  Local (msat):       1949996852",
                         "  Remote (msat):      50003148",
                         "  Commitment (l/r):   2/2",
                         "  HTLCs (out/in):     0/1",
                         "  Fee (base/ppm):     1000 msat/100",
                         Separator), output);
    }

    [Fact]
    public void Given_GraphNodesAndChannels_When_Printed_Then_MatchesSnapshot()
    {
        // Arrange (BOLT 7 G2-T6)
        var nodes = new ListNodesIpcResponse
        {
            Nodes =
            [
                new GraphNodeIpcInfo
                {
                    NodeId = s_payee,
                    Alias = "alice",
                    Color = "#0a0b0c",
                    Addresses = ["127.0.0.1:9735", "[::1]:9736"],
                    Features = "0102",
                    Timestamp = 1_700_000_000,
                    ChannelCount = 2
                }
            ]
        };
        var channels = new ListGraphChannelsIpcResponse
        {
            Channels =
            [
                new GraphChannelIpcInfo
                {
                    ShortChannelId = (110UL << 40) | (1UL << 16),
                    NodeId1 = s_payee,
                    NodeId2 = s_payee,
                    CapacitySat = 1_000_000,
                    Verification = "Verified",
                    SpentAtHeight = 250,
                    Features = "",
                    Policy1 = new GraphPolicyIpcInfo
                    {
                        Timestamp = 1_700_000_100,
                        ChannelFlags = 0,
                        CltvExpiryDelta = 40,
                        HtlcMinimumMsat = 1_000,
                        HtlcMaximumMsat = 990_000_000,
                        FeeBaseMsat = 1_000,
                        FeeProportionalMillionths = 100
                    },
                    Policy2 = new GraphPolicyIpcInfo { Timestamp = 1_700_000_200, ChannelFlags = 3, CltvExpiryDelta = 80 }
                }
            ]
        };

        // Act
        var nodesOutput = Print(w => new ListNodesPrinter(w).Print(nodes));
        var channelsOutput = Print(w => new ListGraphChannelsPrinter(w).Print(channels));
        var emptyOutput = Print(w => new ListGraphChannelsPrinter(w).Print(new ListGraphChannelsIpcResponse
        {
            Channels = []
        }));

        // Assert
        Assert.Equal(Lines(
                         "Graph nodes: 1",
                         Separator,
                         $"  Node Id:            02{Hex(0x11)}",
                         "  Alias:              alice",
                         "  Color:              #0a0b0c",
                         "  Addresses:          127.0.0.1:9735, [::1]:9736",
                         "  Features:           0102",
                         "  Timestamp:          1700000000 (2023-11-14 22:13:20 UTC)",
                         "  Channels:           2",
                         Separator), nodesOutput);
        Assert.Equal(Lines(
                         "Graph channels: 1",
                         Separator,
                         "  Short Channel Id:   110x1x0",
                         $"  Node 1:             02{Hex(0x11)}",
                         $"  Node 2:             02{Hex(0x11)}",
                         "  Capacity (sat):     1000000",
                         "  Verification:       Verified",
                         "  Spent at block:     250 (closed)",
                         "  Policy 1 -> 2:      fee 1000 msat + 100 ppm, cltv delta 40, htlc 1000-990000000 msat, "
                       + "updated 1700000100 (2023-11-14 22:15:00 UTC)",
                         "  Policy 2 -> 1:      fee 0 msat + 0 ppm, cltv delta 80, htlc 0-0 msat, DISABLED, "
                       + "updated 1700000200 (2023-11-14 22:16:40 UTC)",
                         Separator), channelsOutput);
        Assert.Equal(Lines("Graph channels: 0"), emptyOutput);
    }

    private static string Print(Action<TextWriter> print)
    {
        using var writer = new StringWriter();
        writer.NewLine = "\n";
        print(writer);
        return writer.ToString();
    }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(l => l + "\n"));

    private static string Hex(byte fill) => Convert.ToHexStringLower(Enumerable.Repeat(fill, 32).ToArray());
}