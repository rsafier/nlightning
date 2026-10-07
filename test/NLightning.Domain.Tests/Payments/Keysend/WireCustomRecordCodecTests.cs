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
/// 65536, the keysend type included), the BOLT 8 size bound of <c>RESUME_MODIFIED</c>, the storage form, and the engine keeping them
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
    public void Given_TheUpdateAddHtlcBounds_When_Computed_Then_TheyLeaveBolt8sPlaintextForTheRecords()
    {
        // Act / Assert: 65,535 - (2 + 1,450) - 35 with a blinded_path
        Assert.Equal(1_452, WireCustomRecordCodec.UpdateAddHtlcFixedLength);
        Assert.Equal(64_083, WireCustomRecordCodec.MaxEncodedLength(false));
        Assert.Equal(64_048, WireCustomRecordCodec.MaxEncodedLength(true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_RecordsAtTheBound_When_EncodingForAnUpdateAddHtlc_Then_Accepted(bool withBlindedPath)
    {
        // Arrange
        var max = WireCustomRecordCodec.MaxEncodedLength(withBlindedPath);
        var records = RecordsOfEncodedLength(max);

        // Act
        var encoded = WireCustomRecordCodec.EncodeForUpdateAddHtlc(records, withBlindedPath);

        // Assert
        Assert.Equal(max, encoded.Length);
        Assert.Equal(max, WireCustomRecordCodec.EncodedLength(records));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_RecordsOneByteOverTheBound_When_EncodingForAnUpdateAddHtlc_Then_Refused(bool withBlindedPath)
    {
        // Arrange: an add this large could be committed and persisted but never sent (BOLT 8)
        var records = RecordsOfEncodedLength(WireCustomRecordCodec.MaxEncodedLength(withBlindedPath) + 1);

        // Act
        var exception = Assert.Throws<ArgumentException>(() =>
            WireCustomRecordCodec.EncodeForUpdateAddHtlc(records, withBlindedPath));

        // Assert
        Assert.Contains("an update_add_htlc has room for at most", exception.Message);
    }

    [Fact]
    public void Given_RecordsSpreadOverManyTypesTooLargeForAnAdd_When_BuildingAResumeModified_Then_Refused()
    {
        // Arrange: 100 records of 1,000 bytes each (about 100 KB), which gRPC accepts
        var records = Enumerable.Range(0, 100)
                                .Select(i => new CustomRecord(65_537UL + (ulong)(2 * i), new byte[1_000]))
                                .ToArray();

        // Act / Assert
        Assert.Throws<ArgumentException>(() =>
            ForwardInterceptResolution.Modified(null, null, records));
    }

    [Fact]
    public void Given_RecordsThatFitWithABlindedPath_When_BuildingAResumeModified_Then_Kept()
    {
        // Arrange: the resolution is checked against the stricter bound (the outgoing add may carry a blinded_path)
        var records = RecordsOfEncodedLength(WireCustomRecordCodec.MaxEncodedLength(true));

        // Act
        var resolution = ForwardInterceptResolution.Modified(null, null, records);

        // Assert
        Assert.Equal(records, resolution.OutWireCustomRecords);
        Assert.Throws<ArgumentException>(() => ForwardInterceptResolution.Modified(
            null, null, RecordsOfEncodedLength(WireCustomRecordCodec.MaxEncodedLength(true) + 1)));
    }

    /// <summary>One record of type 65537 (5-byte BigSize type, 3-byte BigSize length) of the given encoded size.</summary>
    internal static CustomRecord[] RecordsOfEncodedLength(int encodedLength) =>
        [new CustomRecord(65_537, new byte[encodedLength - 8])];

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