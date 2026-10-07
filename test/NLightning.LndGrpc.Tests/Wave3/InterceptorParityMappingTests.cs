using Google.Protobuf;
using Grpc.Core;

namespace NLightning.LndGrpc.Tests.Wave3;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Interception;
using Domain.Payments.Keysend;
using LndGrpc.Routerrpc;
using LndGrpc.Services;

/// <summary>NL-1182: LND v0.21.4's <c>RESUME_MODIFIED</c>, <c>in_wire_custom_records</c>, on-chain deadline and
/// <c>requireinterceptor</c> mappings.</summary>
public sealed class InterceptorParityMappingTests
{
    [Fact]
    public void Given_AResumeModified_When_Mapped_Then_TheAmountsAndRecordsAreKept()
    {
        // Act
        var (scid, htlcId, resolution) = RouterService.ToResolution(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = new CircuitKey
            {
                ChanId = LightningService.ToChanId(new ShortChannelId(150, 1, 0)),
                HtlcId = 4
            },
            Action = ResolveHoldForwardAction.ResumeModified,
            InAmountMsat = 12_000,
            OutAmountMsat = 11_000,
            OutWireCustomRecords = { [70_001] = ByteString.CopyFrom(9), [65_537] = ByteString.CopyFrom(1, 2) }
        });

        // Assert: the records sorted by type
        Assert.Equal(new ShortChannelId(150, 1, 0), scid);
        Assert.Equal(4UL, htlcId);
        Assert.Equal(ForwardInterceptAction.ResumeModified, resolution.Action);
        Assert.Equal(LightningMoney.MilliSatoshis(12_000), resolution.InAmount);
        Assert.Equal(LightningMoney.MilliSatoshis(11_000), resolution.OutAmount);
        Assert.Equal([65_537UL, 70_001UL], resolution.OutWireCustomRecords!.Select(r => r.Type));
    }

    [Fact]
    public void Given_AResumeModifiedWithoutChanges_When_Mapped_Then_ItForwardsAsIs()
    {
        // Act: LND reads zero amounts and an empty map as "not set"
        var (_, _, resolution) = RouterService.ToResolution(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = new CircuitKey(),
            Action = ResolveHoldForwardAction.ResumeModified
        });

        // Assert
        Assert.Equal(ForwardInterceptAction.ResumeModified, resolution.Action);
        Assert.Null(resolution.InAmount);
        Assert.Null(resolution.OutAmount);
        Assert.Null(resolution.OutWireCustomRecords);
    }

    [Fact]
    public void Given_ARecordBelowTheMinimum_When_Mapped_Then_InvalidArgumentWithLndsText()
    {
        // Act
        var e = Assert.Throws<RpcException>(() => RouterService.ToResolution(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = new CircuitKey(),
            Action = ResolveHoldForwardAction.ResumeModified,
            OutWireCustomRecords = { [100] = ByteString.CopyFrom(1) }
        }));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, e.StatusCode);
        Assert.Equal("failed to validate custom records: custom records entry with TLV type below min: 65536",
                     e.Status.Detail);
    }

    [Fact]
    public void Given_RecordsTooLargeForAnUpdateAddHtlc_When_Mapped_Then_InvalidArgumentBeforeAnythingIsForwarded()
    {
        // Arrange: about 65 KB of records, which gRPC accepts but no update_add_htlc can carry (BOLT 8, 65,535 bytes)
        var response = new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = new CircuitKey(),
            Action = ResolveHoldForwardAction.ResumeModified
        };
        for (var i = 0; i < 65; i++)
            response.OutWireCustomRecords[65_537UL + (ulong)(2 * i)] = ByteString.CopyFrom(new byte[1_000]);

        // Act
        var e = Assert.Throws<RpcException>(() => RouterService.ToResolution(response));

        // Assert
        Assert.Equal(StatusCode.InvalidArgument, e.StatusCode);
        Assert.StartsWith("failed to validate custom records: custom records take ", e.Status.Detail);
    }

    [Fact]
    public void Given_AForwardHeldOnChainWithWireRecords_When_SentToTheInterceptor_Then_TheDeadlineAndRecordsAreLnds()
    {
        // Arrange: on chain the auto_fail_height field carries the settle deadline (the incoming expiry)
        var forward = new InterceptedForward(new ChannelId(new byte[32]), 2, new ShortChannelId(150, 1, 0),
                                             new ShortChannelId(160, 2, 1), null, new Hash(new byte[32]),
                                             LightningMoney.MilliSatoshis(10_100), LightningMoney.MilliSatoshis(10_000),
                                             500, 460, 500, new byte[1366], [],
                                             [new CustomRecord(65_543, [3, 4])], IsOnChain: true);

        // Act
        var rpc = RouterService.ToRpc(forward);

        // Assert
        Assert.Equal(500, rpc.AutoFailHeight);
        Assert.Equal([3, 4], rpc.InWireCustomRecords[65_543].ToByteArray());
        Assert.Empty(rpc.CustomRecords);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void Given_TheOptions_When_TheHubSettingsAreBuilt_Then_RequireInterceptorOnlyWhileTheServerRuns(
        bool enabled, bool require, bool expected)
    {
        // Arrange
        var options = new LndGrpcOptions
        {
            Enabled = enabled,
            RequireInterceptor = require,
            InterceptorCltvRejectDelta = 20,
            InterceptorCltvInterceptDelta = 30,
            MaxHeldHtlcs = 7
        };

        // Act
        var settings = options.ToInterceptorSettings();

        // Assert
        Assert.Equal(expected, settings.RequireInterceptor);
        Assert.Equal(20U, settings.CltvRejectDelta);
        Assert.Equal(30U, settings.CltvInterceptDelta);
        Assert.Equal(7, settings.MaxHeld);
    }
}