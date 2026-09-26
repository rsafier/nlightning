using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT4;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Tlv;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Serialization.Onion;

/// <summary>
/// BOLT 4 attributable failures (M3b): <c>attribution_data</c> of the inline "Returning Errors" trace and the
/// "Returning success" trace (with and without a <c>fulfillment_payload</c>), created, wrapped and verified at every
/// hop byte for byte, plus origin-side blame for tampered or partially supported routes.
/// </summary>
public class AttributionVectorTests
{
    private const int TraceFailurePadLength = 1024;
    private const ulong TraceTlvType = 34001;

    private readonly FailureOnionService _failureOnionService;
    private readonly AttributionDataService _attributionDataService;

    public AttributionVectorTests()
    {
        _failureOnionService = new FailureOnionService(new FailureMessageSerializer());
        _attributionDataService = new AttributionDataService(_failureOnionService);
    }

    #region Returning Errors

    [Fact]
    public void Given_ReturningErrorsTrace_When_CreatingAndWrappingAtEveryHop_Then_PacketAndAttributionMatch()
    {
        // Arrange: hops[0] is the erring node 4; the node at return position i reports hold time i + 1
        var trace = Bolt4Vectors.LoadReturningErrorsTrace();

        // Act
        var result = _attributionDataService.CreateErrorPacket(trace.ErringSharedSecret, BuildTraceFailureMessage(),
                                                               1, TraceFailurePadLength);

        // Assert
        Assert.Equal(Convert.ToHexStringLower(trace.Hops[0].ErrorPacket), Convert.ToHexStringLower(result.Reason));
        Assert.Equal(Convert.ToHexStringLower(trace.Hops[0].AttributionData),
                     Convert.ToHexStringLower(result.AttributionData));

        for (var i = 1; i < trace.Hops.Count; i++)
        {
            var hop = trace.Hops[i];
            result = _attributionDataService.WrapErrorPacket(hop.SharedSecret, result.Reason, result.AttributionData,
                                                             (uint)(i + 1));

            Assert.Equal(Convert.ToHexStringLower(hop.ErrorPacket), Convert.ToHexStringLower(result.Reason));
            Assert.Equal(Convert.ToHexStringLower(hop.AttributionData),
                         Convert.ToHexStringLower(result.AttributionData));
        }
    }

    [Fact]
    public void Given_ReturningErrorsTrace_When_OriginDecrypts_Then_Node4AndEveryHopsHoldTime()
    {
        // Arrange
        var trace = Bolt4Vectors.LoadReturningErrorsTrace();
        var routeSecrets = RouteSecrets(trace);
        var origin = trace.Hops[^1];

        // Act
        var result = _attributionDataService.DecryptErrorPacket(routeSecrets, origin.ErrorPacket,
                                                                origin.AttributionData);

        // Assert
        Assert.NotNull(result.Failure);
        Assert.Equal(4, result.Failure.ErringHopIndex);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, result.Failure.Code);
        Assert.Equal(trace.FailureMessage, result.Failure.RawMessage.ToArray());
        Assert.True(result.Attribution.IsPresent);
        Assert.Null(result.Attribution.InvalidHopIndex);
        Assert.Equal([5u, 4u, 3u, 2u, 1u], result.Attribution.HoldTimes);
        Assert.Equal(TimeSpan.FromMilliseconds(500), result.Attribution.GetHoldTime(0));
        Assert.Equal(4, result.BlamedHopIndex);
    }

    [Fact]
    public void Given_ReturningErrorsTrace_When_OriginDecryptsWithoutAttributionData_Then_LegacyResultAndAbsent()
    {
        // Arrange
        var trace = Bolt4Vectors.LoadReturningErrorsTrace();

        // Act
        var result = _attributionDataService.DecryptErrorPacket(RouteSecrets(trace), trace.Hops[^1].ErrorPacket,
                                                                ReadOnlySpan<byte>.Empty);

        // Assert
        Assert.NotNull(result.Failure);
        Assert.Equal(4, result.Failure.ErringHopIndex);
        Assert.False(result.Attribution.IsPresent);
        Assert.Empty(result.Attribution.HoldTimes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Given_AttributionDataTamperedBetweenTwoHops_When_OriginDecrypts_Then_DownstreamHopOfThePairFails(
        int tamperedAfterReturnPosition)
    {
        // Arrange: the node receiving the data from return position p flips a bit of the sender's hold time (always
        // covered by the sender's HMAC) before wrapping it
        var trace = Bolt4Vectors.LoadReturningErrorsTrace();
        var result = _attributionDataService.CreateErrorPacket(trace.ErringSharedSecret, BuildTraceFailureMessage(),
                                                               1, TraceFailurePadLength);
        for (var i = 1; i < trace.Hops.Count; i++)
        {
            var attribution = result.AttributionData;
            if (i - 1 == tamperedAfterReturnPosition)
            {
                attribution = (byte[])attribution.Clone();
                attribution[3] ^= 0x01;
            }

            result = _attributionDataService.WrapErrorPacket(trace.Hops[i].SharedSecret, result.Reason, attribution,
                                                             (uint)(i + 1));
        }

        // Act
        var decrypted = _attributionDataService.DecryptErrorPacket(RouteSecrets(trace), result.Reason,
                                                                   result.AttributionData);

        // Assert: the return packet is untouched, so the legacy HMAC still names node 4, but the attribution chain
        // breaks at the node whose output was modified
        var tamperedNode = trace.Hops[tamperedAfterReturnPosition].Node;
        Assert.NotNull(decrypted.Failure);
        Assert.Equal(4, decrypted.Failure.ErringHopIndex);
        Assert.Equal(tamperedNode, decrypted.Attribution.InvalidHopIndex);
        Assert.Equal(tamperedNode, decrypted.Attribution.VerifiedHopCount);
    }

    #endregion

    #region Returning success

    [Fact]
    public void Given_ReturningSuccessTrace_When_FulfillingWithoutPayload_Then_EveryHopsAttributionMatches()
    {
        // Arrange
        var trace = Bolt4Vectors.LoadReturningSuccessTrace();

        // Act
        var result = _attributionDataService.CreateFulfillment(trace.Hops[0].SharedSecret, 1);

        // Assert
        Assert.Null(result.FulfillmentPayload);
        Assert.Equal(Convert.ToHexStringLower(trace.Hops[0].AttributionDataWithoutPayload),
                     Convert.ToHexStringLower(result.AttributionData));

        for (var i = 1; i < trace.Hops.Count; i++)
        {
            result = _attributionDataService.WrapFulfillment(trace.Hops[i].SharedSecret, result.AttributionData,
                                                             ReadOnlySpan<byte>.Empty, (uint)(i + 1));

            Assert.Null(result.FulfillmentPayload);
            Assert.Equal(Convert.ToHexStringLower(trace.Hops[i].AttributionDataWithoutPayload),
                         Convert.ToHexStringLower(result.AttributionData));
        }
    }

    [Fact]
    public void Given_ReturningSuccessTrace_When_FulfillingWithPayload_Then_EveryHopsPayloadAndAttributionMatch()
    {
        // Arrange
        var trace = Bolt4Vectors.LoadReturningSuccessTrace();
        var record = new BaseTlv(trace.RecordType, trace.RecordValue);

        // Act
        var result = _attributionDataService.CreateFulfillment(trace.Hops[0].SharedSecret, 1, [record]);

        // Assert
        Assert.NotNull(result.FulfillmentPayload);
        Assert.Equal(272, result.FulfillmentPayload.Length);
        Assert.Equal(Convert.ToHexStringLower(trace.Hops[0].FulfillmentPayload),
                     Convert.ToHexStringLower(result.FulfillmentPayload));
        Assert.Equal(Convert.ToHexStringLower(trace.Hops[0].AttributionDataWithPayload),
                     Convert.ToHexStringLower(result.AttributionData));

        for (var i = 1; i < trace.Hops.Count; i++)
        {
            result = _attributionDataService.WrapFulfillment(trace.Hops[i].SharedSecret, result.AttributionData,
                                                             result.FulfillmentPayload, (uint)(i + 1));

            Assert.NotNull(result.FulfillmentPayload);
            Assert.Equal(Convert.ToHexStringLower(trace.Hops[i].FulfillmentPayload),
                         Convert.ToHexStringLower(result.FulfillmentPayload));
            Assert.Equal(Convert.ToHexStringLower(trace.Hops[i].AttributionDataWithPayload),
                         Convert.ToHexStringLower(result.AttributionData));
        }
    }

    [Fact]
    public void Given_ReturningSuccessTraceWithPayload_When_OriginVerifies_Then_HoldTimesAndRecordRecovered()
    {
        // Arrange
        var trace = Bolt4Vectors.LoadReturningSuccessTrace();
        var origin = trace.Hops[^1];

        // Act
        var result = _attributionDataService.VerifyFulfillment(RouteSecrets(trace), origin.AttributionDataWithPayload,
                                                               origin.FulfillmentPayload);

        // Assert
        Assert.True(result.Attribution.IsPresent);
        Assert.Null(result.Attribution.InvalidHopIndex);
        Assert.Equal(trace.HoldTimesByNode, result.Attribution.HoldTimes);
        Assert.Equal(FulfillmentPayloadStatus.Valid, result.PayloadStatus);
        var record = Assert.Single(result.PayloadRecords);
        Assert.Equal(trace.RecordType, record.Type.Value);
        Assert.Equal(trace.RecordValue, record.Value);
    }

    [Fact]
    public void Given_ReturningSuccessTraceWithoutPayload_When_OriginVerifies_Then_HoldTimesAndNoPayload()
    {
        // Arrange
        var trace = Bolt4Vectors.LoadReturningSuccessTrace();

        // Act
        var result = _attributionDataService.VerifyFulfillment(RouteSecrets(trace),
                                                               trace.Hops[^1].AttributionDataWithoutPayload,
                                                               ReadOnlySpan<byte>.Empty);

        // Assert
        Assert.Null(result.Attribution.InvalidHopIndex);
        Assert.Equal(trace.HoldTimesByNode, result.Attribution.HoldTimes);
        Assert.Equal(FulfillmentPayloadStatus.None, result.PayloadStatus);
        Assert.Empty(result.PayloadRecords);
    }

    [Fact]
    public void Given_PayloadModifiedByAnIntermediateHop_When_OriginVerifies_Then_ThatPairIsBlamedAndPayloadIgnored()
    {
        // Arrange: node 2 (return position 2) receives node 3's payload, flips a byte and then wraps it honestly
        var trace = Bolt4Vectors.LoadReturningSuccessTrace();
        var tampered = (byte[])trace.Hops[1].FulfillmentPayload.Clone();
        tampered[10] ^= 0x80;
        var result = _attributionDataService.WrapFulfillment(trace.Hops[2].SharedSecret,
                                                             trace.Hops[1].AttributionDataWithPayload, tampered, 3);
        for (var i = 3; i < trace.Hops.Count; i++)
            result = _attributionDataService.WrapFulfillment(trace.Hops[i].SharedSecret, result.AttributionData,
                                                             result.FulfillmentPayload!, (uint)(i + 1));

        // Act
        var verified = _attributionDataService.VerifyFulfillment(RouteSecrets(trace), result.AttributionData,
                                                                 result.FulfillmentPayload!);

        // Assert: node 2's HMACs cover the modified bytes, node 3's cover the original ones
        Assert.Equal(3, verified.Attribution.InvalidHopIndex);
        Assert.Equal([5u, 4u, 3u], verified.Attribution.HoldTimes);
        Assert.Equal(FulfillmentPayloadStatus.Invalid, verified.PayloadStatus);
        Assert.Empty(verified.PayloadRecords);
    }

    #endregion

    #region Wrap at each hop and verify at the origin (own routes)

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void Given_FailureFromAnyHop_When_WrappedAndDecryptedAtOrigin_Then_ErringHopAndHoldTimesUpToIt(
        int erringNode)
    {
        // Arrange
        var routeSecrets = RouteSecrets(Bolt4Vectors.LoadReturningErrorsTrace());
        var message = FailureMessage.TemporaryChannelFailure();
        var result = _attributionDataService.CreateErrorPacket(routeSecrets[erringNode], message, 70);
        for (var node = erringNode - 1; node >= 0; node--)
            result = _attributionDataService.WrapErrorPacket(routeSecrets[node], result.Reason, result.AttributionData,
                                                             (uint)(10 + node));

        // Act
        var decrypted = _attributionDataService.DecryptErrorPacket(routeSecrets, result.Reason,
                                                                   result.AttributionData);

        // Assert
        Assert.NotNull(decrypted.Failure);
        Assert.Equal(erringNode, decrypted.Failure.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryChannelFailure, decrypted.Failure.Code);
        Assert.Null(decrypted.Attribution.InvalidHopIndex);
        var expected = Enumerable.Range(0, erringNode).Select(n => (uint)(10 + n)).Append(70u);
        Assert.Equal(expected, decrypted.Attribution.HoldTimes);
    }

    [Fact]
    public void Given_DownstreamHopWithoutAttributionSupport_When_OriginDecrypts_Then_AttributionUpToThatHop()
    {
        // Arrange: node 4 errs and node 3 only wraps the legacy packet, so node 2 starts from an all-zero block
        var routeSecrets = RouteSecrets(Bolt4Vectors.LoadReturningErrorsTrace());
        var created = _attributionDataService.CreateErrorPacket(routeSecrets[4], FailureMessage.TemporaryNodeFailure(),
                                                                1);
        var legacy = _failureOnionService.WrapErrorPacket(routeSecrets[3], created.Reason);
        var result = _attributionDataService.WrapErrorPacket(routeSecrets[2], legacy, ReadOnlySpan<byte>.Empty, 3);
        result = _attributionDataService.WrapErrorPacket(routeSecrets[1], result.Reason, result.AttributionData, 4);
        result = _attributionDataService.WrapErrorPacket(routeSecrets[0], result.Reason, result.AttributionData, 5);

        // Act
        var decrypted = _attributionDataService.DecryptErrorPacket(routeSecrets, result.Reason,
                                                                   result.AttributionData);

        // Assert
        Assert.NotNull(decrypted.Failure);
        Assert.Equal(4, decrypted.Failure.ErringHopIndex);
        Assert.Equal(3, decrypted.Attribution.InvalidHopIndex);
        Assert.Equal([5u, 4u, 3u], decrypted.Attribution.HoldTimes);
    }

    [Fact]
    public void Given_ReturnPacketCorrupted_When_OriginDecrypts_Then_AttributionIdentifiesTheSource()
    {
        // Arrange: node 2 garbles the return packet it received from node 3 but keeps the attribution chain
        var routeSecrets = RouteSecrets(Bolt4Vectors.LoadReturningErrorsTrace());
        var result = _attributionDataService.CreateErrorPacket(routeSecrets[4], FailureMessage.TemporaryNodeFailure(),
                                                               1);
        result = _attributionDataService.WrapErrorPacket(routeSecrets[3], result.Reason, result.AttributionData, 2);
        var garbled = (byte[])result.Reason.Clone();
        garbled[40] ^= 0xFF;
        result = _attributionDataService.WrapErrorPacket(routeSecrets[2], garbled, result.AttributionData, 3);
        result = _attributionDataService.WrapErrorPacket(routeSecrets[1], result.Reason, result.AttributionData, 4);
        result = _attributionDataService.WrapErrorPacket(routeSecrets[0], result.Reason, result.AttributionData, 5);

        // Act
        var decrypted = _attributionDataService.DecryptErrorPacket(routeSecrets, result.Reason,
                                                                   result.AttributionData);

        // Assert: no hop's legacy HMAC matches; node 3's attribution HMAC covers the original packet, so the pair
        // (2, 3) is blamed
        Assert.Null(decrypted.Failure);
        Assert.Equal(3, decrypted.Attribution.InvalidHopIndex);
        Assert.Equal(3, decrypted.BlamedHopIndex);
    }

    [Fact]
    public void Given_FulfillmentThroughTwoHopRoute_When_OriginVerifies_Then_HoldTimesInRouteOrder()
    {
        // Arrange
        var routeSecrets = RouteSecrets(Bolt4Vectors.LoadReturningErrorsTrace()).Take(2).ToList();
        var result = _attributionDataService.CreateFulfillment(routeSecrets[1], 7, []);
        result = _attributionDataService.WrapFulfillment(routeSecrets[0], result.AttributionData,
                                                         result.FulfillmentPayload!, 9);

        // Act
        var verified = _attributionDataService.VerifyFulfillment(routeSecrets, result.AttributionData,
                                                                 result.FulfillmentPayload!);

        // Assert
        Assert.Equal([9u, 7u], verified.Attribution.HoldTimes);
        Assert.Equal(FulfillmentPayloadStatus.Valid, verified.PayloadStatus);
        Assert.Empty(verified.PayloadRecords);
    }

    #endregion

    private static List<Secret> RouteSecrets(Bolt4ReturningErrorsTraceVector trace)
    {
        return trace.Hops.OrderBy(h => h.Node).Select(h => new Secret(h.SharedSecret)).ToList();
    }

    private static List<Secret> RouteSecrets(Bolt4ReturningSuccessTraceVector trace)
    {
        return trace.Hops.OrderBy(h => h.Node).Select(h => new Secret(h.SharedSecret)).ToList();
    }

    private static FailureMessage BuildTraceFailureMessage()
    {
        var extension = new TlvStream();
        extension.Add(new BaseTlv(TraceTlvType, Enumerable.Repeat((byte)0x80, 300).ToArray()));
        return FailureMessage.IncorrectOrUnknownPaymentDetails(LightningMoney.MilliSatoshis(100UL), 800_000)
                             .WithExtension(extension);
    }
}