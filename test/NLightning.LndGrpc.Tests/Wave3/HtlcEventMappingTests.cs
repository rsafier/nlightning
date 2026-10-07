namespace NLightning.LndGrpc.Tests.Wave3;

using Domain.Payments.Events;
using Domain.Protocol.Onion.Enums;
using LndGrpc.Services;
using Lnrpc;
using Routerrpc;

public class HtlcEventMappingTests
{
    private static HtlcActivityEvent Activity(HtlcActivityKind kind) => new(kind, HtlcActivityRole.Forward,
        123, 7, 456, 8, DateTimeOffset.UnixEpoch.AddTicks(123),
        11_000, 700, 10_000, 660, Enumerable.Repeat((byte)3, 32).ToArray(),
        (ushort)FailureCode.FeeInsufficient, "FeeInsufficient", true);

    [Fact]
    public void Given_Forward_When_Mapping_Then_CircuitPairAmountsTimelocksAndNanosecondsMatchLnd()
    {
        var rpc = RouterService.MapHtlcActivity(Activity(HtlcActivityKind.Forward));
        Assert.Equal(123UL, rpc.IncomingChannelId);
        Assert.Equal(456UL, rpc.OutgoingChannelId);
        Assert.Equal(7UL, rpc.IncomingHtlcId);
        Assert.Equal(8UL, rpc.OutgoingHtlcId);
        Assert.Equal(12_300UL, rpc.TimestampNs);
        Assert.Equal(HtlcEvent.Types.EventType.Forward, rpc.EventType);
        Assert.Equal(11_000UL, rpc.ForwardEvent.Info.IncomingAmtMsat);
        Assert.Equal(10_000UL, rpc.ForwardEvent.Info.OutgoingAmtMsat);
        Assert.Equal(700U, rpc.ForwardEvent.Info.IncomingTimelock);
        Assert.Equal(660U, rpc.ForwardEvent.Info.OutgoingTimelock);
    }

    [Fact]
    public void Given_LocalWireFailure_When_Mapping_Then_ProtobufEnumUsesLndMappingRatherThanBoltNumericCode()
    {
        var rpc = RouterService.MapHtlcActivity(Activity(HtlcActivityKind.LinkFail));
        Assert.Equal(Failure.Types.FailureCode.FeeInsufficient, rpc.LinkFailEvent.WireFailure);
        Assert.Equal(FailureDetail.NoDetail, rpc.LinkFailEvent.FailureDetail);
        Assert.Equal("FeeInsufficient", rpc.LinkFailEvent.FailureString);
    }

    [Fact]
    public void Given_Settle_When_Mapping_Then_OnlySettlePayloadCarriesThePreimage()
    {
        var settle = RouterService.MapHtlcActivity(Activity(HtlcActivityKind.Settle));
        Assert.Equal(Enumerable.Repeat((byte)3, 32).ToArray(), settle.SettleEvent.Preimage.ToByteArray());
        var failure = RouterService.MapHtlcActivity(Activity(HtlcActivityKind.ForwardFail));
        Assert.NotNull(failure.ForwardFailEvent);
        Assert.Null(failure.SettleEvent);
    }

    [Fact]
    public void Given_FinalIncomingRemoval_When_Mapping_Then_IncomingOnlyUnknownRoleAndOffchainMatchLnd()
    {
        var rpc = RouterService.MapHtlcActivity(Activity(HtlcActivityKind.Final));
        Assert.Equal(123UL, rpc.IncomingChannelId);
        Assert.Equal(7UL, rpc.IncomingHtlcId);
        Assert.Equal(0UL, rpc.OutgoingChannelId);
        Assert.Equal(0UL, rpc.OutgoingHtlcId);
        Assert.Equal(HtlcEvent.Types.EventType.Unknown, rpc.EventType);
        Assert.True(rpc.FinalHtlcEvent.Settled);
        Assert.True(rpc.FinalHtlcEvent.Offchain);
    }
}