using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Payments.Keysend;

using Application.Payments.FinalHop;
using Application.Payments.Keysend;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Receiving keysend (lane lh1-l3): <see cref="KeysendReceiver"/> makes the record, <see cref="FinalHopProcessor"/>
/// checks the HTLC against it (single part, any amount, the record's CLTV delta, the onion's preimage).
/// </summary>
public class KeysendReceiveTests
{
    private const uint Height = 800;
    private const ulong AmountMsat = 21_000;

    private static readonly byte[] s_preimage = Enumerable.Repeat((byte)0x42, 32).ToArray();
    private static readonly Hash s_paymentHash = SHA256.HashData(s_preimage);
    private static readonly DateTimeOffset s_now = DateTimeOffset.UtcNow;

    private readonly FinalHopProcessor _processor = new(NullLogger<FinalHopProcessor>.Instance);

    private static HopPayload CreatePayload(byte[]? preimage = null, uint outgoingCltv = Height + 40,
                                            ulong? totalMsat = null, params BaseTlv[] extra)
    {
        var tlvs = new List<BaseTlv>
        {
            new AmtToForwardTlv(LightningMoney.MilliSatoshis(AmountMsat)),
            new OutgoingCltvValueTlv(outgoingCltv),
            new BaseTlv(OnionPayloadTlvTypes.KeysendPreimage, preimage ?? s_preimage)
        };
        if (totalMsat is { } total)
            tlvs.Add(new PaymentDataTlv(new Secret(Enumerable.Repeat((byte)0x99, 32).ToArray()),
                                        LightningMoney.MilliSatoshis(total)));
        tlvs.AddRange(extra);
        return new HopPayload(tlvs.ToArray());
    }

    private static KeysendReceiver CreateReceiver(bool accept = true) =>
        new(new KeysendOptions { Accept = accept }, new FixedClock(s_now));

    private FinalHopResult Evaluate(InvoiceModel? invoice, HopPayload payload, uint htlcCltv = Height + 40) =>
        _processor.Evaluate(invoice, s_paymentHash, LightningMoney.MilliSatoshis(AmountMsat), htlcCltv, payload,
                            Height);

    [Fact]
    public void Given_KeysendPayload_When_RecordCreated_Then_KeysendRecordWithCustomRecords()
    {
        // Arrange
        var payload = CreatePayload(extra: [new BaseTlv(new BigSize(7629169), "boost"u8.ToArray())]);

        // Act
        var record = CreateReceiver().TryCreateInvoice(s_paymentHash, payload, out var reason);

        // Assert
        Assert.Null(reason);
        Assert.NotNull(record);
        Assert.Equal(InvoiceKind.Keysend, record.Kind);
        Assert.Equal(InvoiceStatus.Open, record.Status);
        Assert.Null(record.Amount);
        Assert.Null(record.Bolt11);
        Assert.Equal(s_preimage, (byte[])record.Preimage);
        Assert.Equal(s_now, record.CreatedAt);
        Assert.Equal((ushort)18, record.MinFinalCltvExpiry);
        var custom = Assert.Single(record.Keysend!.CustomRecords);
        Assert.Equal((7629169UL, "boost"), (custom.Type, System.Text.Encoding.UTF8.GetString(custom.Value.Span)));
    }

    [Fact]
    public void Given_KeysendRecord_When_Evaluated_Then_AcceptedWithThePayersPreimage()
    {
        // Arrange
        var payload = CreatePayload();
        var record = CreateReceiver().TryCreateInvoice(s_paymentHash, payload, out _);

        // Act
        var result = Evaluate(record, payload);

        // Assert
        Assert.True(result.IsAccepted, result.Reason);
        Assert.Equal(s_preimage, (byte[])result.Preimage!.Value);
        Assert.Equal(AmountMsat, result.TotalMsat!.MilliSatoshi);
        Assert.False(result.IsMultiPart);
    }

    [Fact]
    public void Given_KeysendWithPaymentDataOfTheSameTotal_When_Evaluated_Then_AcceptedWithoutSecretCheck()
    {
        // Arrange
        var payload = CreatePayload(totalMsat: AmountMsat);
        var record = CreateReceiver().TryCreateInvoice(s_paymentHash, payload, out _);

        // Act
        var result = Evaluate(record, payload);

        // Assert
        Assert.True(result.IsAccepted, result.Reason);
    }

    [Fact]
    public void Given_MultiPartKeysend_When_Evaluated_Then_UnknownPaymentDetails()
    {
        // Arrange
        var payload = CreatePayload(totalMsat: AmountMsat * 2);
        var record = CreateReceiver().TryCreateInvoice(s_paymentHash, payload, out _);

        // Act
        var result = _processor.Evaluate(record, s_paymentHash, LightningMoney.MilliSatoshis(AmountMsat), Height + 40,
                                         payload, Height, acceptMultiPart: true);

        // Assert
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Failure!.Code);
    }

    [Fact]
    public void Given_KeysendCltvBelowTheRecordsDelta_When_Evaluated_Then_UnknownPaymentDetails()
    {
        // Arrange: 17 blocks left, the record wants 18
        var payload = CreatePayload(outgoingCltv: Height + 17);
        var record = CreateReceiver().TryCreateInvoice(s_paymentHash, payload, out _);

        // Act
        var result = Evaluate(record, payload, Height + 17);

        // Assert
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Failure!.Code);
    }

    [Fact]
    public void Given_WrongPreimage_When_RecordCreated_Then_RefusedAndHashUnknown()
    {
        // Arrange
        var payload = CreatePayload(Enumerable.Repeat((byte)0x43, 32).ToArray());

        // Act
        var record = CreateReceiver().TryCreateInvoice(s_paymentHash, payload, out var reason);
        var result = Evaluate(record, payload);

        // Assert
        Assert.Null(record);
        Assert.Contains("SHA256", reason);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Failure!.Code);
    }

    [Fact]
    public void Given_ShortPreimage_When_RecordCreated_Then_Refused()
    {
        // Act
        var record = CreateReceiver().TryCreateInvoice(s_paymentHash, CreatePayload(new byte[31]), out var reason);

        // Assert
        Assert.Null(record);
        Assert.Contains("31 bytes", reason);
    }

    [Fact]
    public void Given_KeysendOff_When_RecordCreated_Then_Refused()
    {
        // Act
        var record = CreateReceiver(accept: false).TryCreateInvoice(s_paymentHash, CreatePayload(), out var reason);

        // Assert
        Assert.Null(record);
        Assert.Contains("Accept", reason);
    }

    [Fact]
    public void Given_KeysendRecordAndAnHtlcWithoutItsPreimage_When_Evaluated_Then_UnknownPaymentDetails()
    {
        // Arrange: the record exists (an earlier keysend); this HTLC has payment_data but no keysend_preimage
        var record = CreateReceiver().TryCreateInvoice(s_paymentHash, CreatePayload(), out _);
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(AmountMsat)),
                                     new OutgoingCltvValueTlv(Height + 40),
                                     new PaymentDataTlv(new Secret(new byte[32]),
                                                        LightningMoney.MilliSatoshis(AmountMsat)));

        // Act
        var result = Evaluate(record, payload);

        // Assert
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Failure!.Code);
    }

    [Fact]
    public void Given_Bolt11InvoiceAndKeysendPayloadWithoutPaymentData_When_Evaluated_Then_UnknownPaymentDetails()
    {
        // Arrange: a keysend_preimage does not replace payment_data for an invoice we issued
        var invoice = new InvoiceModel(s_paymentHash, s_preimage, new Secret(new byte[32]), null, null, "lnbcrt1x",
                                       s_now, 3_600, 18);

        // Act
        var result = Evaluate(invoice, CreatePayload());

        // Assert
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Failure!.Code);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}