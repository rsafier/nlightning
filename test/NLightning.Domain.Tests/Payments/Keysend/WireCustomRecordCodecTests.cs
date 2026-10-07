namespace NLightning.Domain.Tests.Payments.Keysend;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Money;
using Domain.Payments.Interception;
using Domain.Payments.Keysend;
using Tests.Channels.Commitments;
using static Tests.Channels.Commitments.CommitmentsTestKit;

/// <summary>
/// NL-1182: the <c>update_add_htlc</c> custom records (LND's wire custom records): LND's validation (any type from
/// 65536, the keysend type included), the merge of <c>RESUME_MODIFIED</c>, the storage form, and the engine keeping them
/// on the HTLC record so a retransmission carries them.
/// </summary>
public class WireCustomRecordCodecTests
{
    [Fact]
    public void Given_UnsortedRecordsIncludingTheKeysendType_When_EncodingAndDecoding_Then_SortedRoundTrip()
    {
        // Arrange: unlike a final hop's onion records, a wire record may use any type from 65536 (LND)
        CustomRecord[] records = [new(CustomRecordCodec.KeysendPreimageType, [1]), new(65_536, [0xab, 0xcd])];

        // Act
        var bytes = WireCustomRecordCodec.Encode(records);
        var decoded = WireCustomRecordCodec.Decode(bytes);

        // Assert
        Assert.Equal("fe0001000002abcd" + "ff0000000146c6616c0101", Convert.ToHexStringLower(bytes));
        Assert.Equal(records.OrderBy(r => r.Type), decoded);
    }

    [Fact]
    public void Given_ARecordBelowTheMinimum_When_Validating_Then_RefusedWithLndsText()
    {
        // Act
        var exception = Assert.Throws<ArgumentException>(() => WireCustomRecordCodec.Validate([new CustomRecord(65_535, [])]));

        // Assert
        Assert.StartsWith("custom records entry with TLV type below min: 65536", exception.Message);
    }

    [Fact]
    public void Given_ExistingRecords_When_MergingOverrides_Then_SameTypesAreReplacedAndTheRestKept()
    {
        // Act: LND's CustomRecords.MergedCopy
        var merged = WireCustomRecordCodec.Merge([new CustomRecord(65_537, [1]), new CustomRecord(65_539, [3])],
                                                 [new CustomRecord(65_537, [9]), new CustomRecord(65_541, [5])]);

        // Assert
        Assert.Equal([new CustomRecord(65_537, [9]), new CustomRecord(65_539, [3]), new CustomRecord(65_541, [5])],
                     merged);
    }

    [Fact]
    public void Given_NoBytesOrBadBytes_When_Decoding_Then_NoRecords()
    {
        // Act / Assert: a stored row never breaks a channel load
        Assert.Empty(WireCustomRecordCodec.Decode(ReadOnlyMemory<byte>.Empty));
        Assert.Empty(WireCustomRecordCodec.Decode(new byte[] { 0xfe, 0x00 }));
        Assert.Empty(WireCustomRecordCodec.Encode([]));
    }

    [Fact]
    public void Given_WireRecords_When_SendingAndReceivingAnAdd_Then_TheHtlcRecordsKeepThem()
    {
        // Arrange
        var c = Create(600_000, 400_000);
        var records = WireCustomRecordCodec.Encode([new CustomRecord(65_537, [0xca, 0xfe])]);

        // Act
        var sent = c.SendAdd(10_000 * Sat, PaymentHash(1), 600, Onion, wireCustomRecords: records);
        var received = c.ReceiveAdd(0, 10_000 * Sat, PaymentHash(2), 600, Onion, wireCustomRecords: records);

        // Assert
        var outbound = Assert.IsType<OutboundAddHtlc>(Assert.Single(sent.Outbound)).Htlc;
        Assert.Equal(records, outbound.WireCustomRecords.ToArray());
        Assert.Equal(records, sent.Next.GetHtlc(HtlcDirection.Outgoing, 0)!.WireCustomRecords.ToArray());
        Assert.Equal(records, received.Next.GetHtlc(HtlcDirection.Incoming, 0)!.WireCustomRecords.ToArray());
        Assert.True(c.Add(10_000 * Sat).Next.GetHtlc(HtlcDirection.Outgoing, 0)!.WireCustomRecords.IsEmpty);
    }

    [Fact]
    public void Given_ZeroAmounts_When_BuildingAResumeModified_Then_TheyMeanUnchanged()
    {
        // Act: LND reads in_amount_msat / out_amount_msat 0 as "not set"
        var resolution = ForwardInterceptResolution.Modified(LightningMoney.Zero, LightningMoney.Zero, []);

        // Assert
        Assert.Equal(ForwardInterceptAction.ResumeModified, resolution.Action);
        Assert.Null(resolution.InAmount);
        Assert.Null(resolution.OutAmount);
        Assert.Null(resolution.OutWireCustomRecords);
    }
}