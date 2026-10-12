namespace NLightning.Daemon.Tests.Client;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using NLightning.Client.Printers;
using Transport.Ipc.Responses;

/// <summary>
/// NL-899: <c>listpayments</c> marks a relay's outgoing leg, prints a trampoline payment's node and inner route, and
/// says how many relay legs it left out.
/// </summary>
public class ListPaymentsPrinterTests
{
    private static readonly Hash s_paymentHash = new(Enumerable.Repeat((byte)0xab, 32).ToArray());
    private static readonly CompactPubKey s_payee = new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);
    private static readonly CompactPubKey s_trampoline = new([0x03, .. Enumerable.Repeat((byte)0x22, 32)]);
    private static readonly DateTimeOffset s_createdAt = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Given_ATrampolinePaymentAndARelayLeg_When_Printed_Then_TheyAreMarkedAndTheHiddenLegsCounted()
    {
        // Arrange
        using var output = new StringWriter();
        var response = new ListPaymentsIpcResponse
        {
            Payments =
            [
                Payment(trampolineNodeId: s_trampoline, trampolineAttempts: 2,
                        trampolineRoute:
                        [
                            new PaymentTrampolineHopIpcInfo
                            {
                                NodeId = s_trampoline,
                                AmountMsat = 1_004_000,
                                CltvExpiry = 950
                            },
                            new PaymentTrampolineHopIpcInfo
                            {
                                NodeId = s_payee,
                                AmountMsat = 1_000_000,
                                CltvExpiry = 820
                            }
                        ]),
                Payment(isTrampolineRelay: true)
            ],
            HiddenRelayLegs = 4
        };

        // Act
        new ListPaymentsPrinter(output).Print(response);

        // Assert
        var lines = output.ToString().ReplaceLineEndings("\n").Split('\n');
        Assert.Contains($"  Trampoline Node:    {s_trampoline} (2 attempts, route of the last)", lines);
        Assert.Contains($"  Trampoline Hop:     {s_trampoline} 1004000 msat cltv 950", lines);
        Assert.Contains($"  Trampoline Hop:     {s_payee} 1000000 msat cltv 820", lines);
        Assert.Single(lines, l => l == "  Trampoline Relay:   outgoing leg of a payment we relayed (see listforwards)");
        Assert.Contains("  4 outgoing leg(s) of trampoline relays not shown (--include-relay-legs lists them; "
                      + "listforwards lists the relays)", lines);
    }

    [Fact]
    public void Given_NoPaymentButHiddenLegs_When_Printed_Then_NoneAndTheHiddenLegsAreSaid()
    {
        // Arrange
        using var output = new StringWriter();

        // Act
        new ListPaymentsPrinter(output).Print(new ListPaymentsIpcResponse { Payments = [], HiddenRelayLegs = 1 });

        // Assert
        Assert.Equal("Payments:\n  None\n  1 outgoing leg(s) of trampoline relays not shown (--include-relay-legs "
                   + "lists them; listforwards lists the relays)\n",
                     output.ToString().ReplaceLineEndings("\n"));
    }

    private static PaymentInfoIpcResponse Payment(bool isTrampolineRelay = false,
                                                  CompactPubKey? trampolineNodeId = null, int trampolineAttempts = 0,
                                                  List<PaymentTrampolineHopIpcInfo>? trampolineRoute = null) =>
        new()
        {
            PaymentHash = s_paymentHash,
            PayeeNodeId = s_payee,
            Amount = LightningMoney.MilliSatoshis(1_000_000),
            Fee = LightningMoney.MilliSatoshis(4_000),
            Status = PaymentStatus.Succeeded,
            CreatedAt = s_createdAt,
            IsTrampolineRelay = isTrampolineRelay,
            TrampolineNodeId = trampolineNodeId,
            TrampolineAttempts = trampolineAttempts,
            TrampolineRoute = trampolineRoute
        };
}