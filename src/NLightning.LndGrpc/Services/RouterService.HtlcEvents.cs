using Google.Protobuf;
using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Domain.Payments.Events;
using Routerrpc;
using RpcCode = Lnrpc.Failure.Types.FailureCode;
using WireCode = Domain.Protocol.Onion.Enums.FailureCode;

public sealed partial class RouterService
{
    /// <summary>LND-compatible live HTLC observations; no replay, cursor, or operational interception.</summary>
    public override async Task SubscribeHtlcEvents(SubscribeHtlcEventsRequest request,
                                                  IServerStreamWriter<HtlcEvent> responseStream,
                                                  ServerCallContext context)
    {
        if (_htlcEvents is null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "HTLC event source unavailable"));
        using var subscription = _htlcEvents.Subscribe();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, subscription.OverflowCancellationToken);
        try
        {
            // Pinned LND's acknowledgement contains no timestamp or circuit, and forms the subscription barrier.
            await responseStream.WriteAsync(new HtlcEvent { SubscribedEvent = new SubscribedEvent() }, linked.Token);
            await foreach (var activity in subscription.ReadAllAsync(linked.Token))
            {
                if (subscription.Overflowed)
                    throw HtlcOverflow();
                await responseStream.WriteAsync(MapHtlcActivity(activity), linked.Token);
            }
        }
        catch (OperationCanceledException) when (subscription.Overflowed && !context.CancellationToken.IsCancellationRequested)
        {
            throw HtlcOverflow();
        }
        if (subscription.Overflowed && !context.CancellationToken.IsCancellationRequested)
            throw HtlcOverflow();
    }

    private static RpcException HtlcOverflow() => new(new Status(StatusCode.ResourceExhausted,
        "HTLC subscription overflowed; reconnect and reconcile current state"));

    internal static HtlcEvent MapHtlcActivity(HtlcActivityEvent activity)
    {
        var result = new HtlcEvent
        {
            IncomingChannelId = activity.IncomingChannelId,
            IncomingHtlcId = activity.IncomingHtlcId,
            OutgoingChannelId = activity.OutgoingChannelId,
            OutgoingHtlcId = activity.OutgoingHtlcId,
            TimestampNs = checked((ulong)(activity.OccurredAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100),
            EventType = activity.Role switch
            {
                HtlcActivityRole.Send => HtlcEvent.Types.EventType.Send,
                HtlcActivityRole.Receive => HtlcEvent.Types.EventType.Receive,
                HtlcActivityRole.Forward => HtlcEvent.Types.EventType.Forward,
                _ => HtlcEvent.Types.EventType.Unknown
            }
        };
        var info = new HtlcInfo
        {
            IncomingAmtMsat = activity.IncomingAmountMsat,
            IncomingTimelock = activity.IncomingTimelock,
            OutgoingAmtMsat = activity.OutgoingAmountMsat,
            OutgoingTimelock = activity.OutgoingTimelock
        };
        switch (activity.Kind)
        {
            case HtlcActivityKind.Forward:
                result.ForwardEvent = new ForwardEvent { Info = info };
                break;
            case HtlcActivityKind.ForwardFail:
                result.ForwardFailEvent = new ForwardFailEvent();
                break;
            case HtlcActivityKind.Settle:
                result.SettleEvent = new SettleEvent { Preimage = ByteString.CopyFrom(activity.Preimage.Span) };
                break;
            case HtlcActivityKind.LinkFail:
                result.LinkFailEvent = new LinkFailEvent
                {
                    Info = info,
                    WireFailure = MapHtlcFailure(activity.WireFailure),
                    FailureDetail = activity.WireFailure is null ? FailureDetail.Unknown : FailureDetail.NoDetail,
                    FailureString = activity.FailureString
                };
                break;
            case HtlcActivityKind.Final:
                // LND FinalHtlcEvent has only the incoming circuit and no SEND/RECEIVE/FORWARD classification.
                result.EventType = HtlcEvent.Types.EventType.Unknown;
                result.OutgoingChannelId = 0;
                result.OutgoingHtlcId = 0;
                result.FinalHtlcEvent = new FinalHtlcEvent { Settled = activity.Settled, Offchain = activity.Offchain };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(activity));
        }
        return result;
    }

    private static RpcCode MapHtlcFailure(ushort? code) => (WireCode?)code switch
    {
        WireCode.IncorrectOrUnknownPaymentDetails => RpcCode.IncorrectOrUnknownPaymentDetails,
        WireCode.IncorrectPaymentAmount => RpcCode.IncorrectPaymentAmount,
        WireCode.FinalIncorrectCltvExpiry => RpcCode.FinalIncorrectCltvExpiry,
        WireCode.FinalIncorrectHtlcAmount => RpcCode.FinalIncorrectHtlcAmount,
        WireCode.FinalExpiryTooSoon => RpcCode.FinalExpiryTooSoon,
        WireCode.ExpiryTooSoon => RpcCode.ExpiryTooSoon,
        WireCode.InvalidOnionVersion => RpcCode.InvalidOnionVersion,
        WireCode.InvalidOnionHmac => RpcCode.InvalidOnionHmac,
        WireCode.InvalidOnionKey => RpcCode.InvalidOnionKey,
        WireCode.AmountBelowMinimum => RpcCode.AmountBelowMinimum,
        WireCode.FeeInsufficient => RpcCode.FeeInsufficient,
        WireCode.IncorrectCltvExpiry => RpcCode.IncorrectCltvExpiry,
        WireCode.ChannelDisabled => RpcCode.ChannelDisabled,
        WireCode.TemporaryChannelFailure => RpcCode.TemporaryChannelFailure,
        WireCode.RequiredNodeFeatureMissing => RpcCode.RequiredNodeFeatureMissing,
        WireCode.RequiredChannelFeatureMissing => RpcCode.RequiredChannelFeatureMissing,
        WireCode.UnknownNextPeer => RpcCode.UnknownNextPeer,
        WireCode.TemporaryNodeFailure => RpcCode.TemporaryNodeFailure,
        WireCode.PermanentNodeFailure => RpcCode.PermanentNodeFailure,
        WireCode.PermanentChannelFailure => RpcCode.PermanentChannelFailure,
        WireCode.ExpiryTooFar => RpcCode.ExpiryTooFar,
        WireCode.MppTimeout => RpcCode.MppTimeout,
        WireCode.InvalidOnionPayload => RpcCode.InvalidOnionPayload,
        WireCode.InvalidOnionBlinding => RpcCode.InvalidOnionBlinding,
        _ => RpcCode.UnknownFailure
    };
}