using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Application.Payments.Interception;
using Domain.Channels.ValueObjects;
using Domain.Payments.Interception;
using Domain.Protocol.Onion.Enums;
using Routerrpc;
using LnrpcFailureCode = Lnrpc.Failure.Types.FailureCode;

public sealed partial class RouterService
{
    /// <summary>LND's sentinel outgoing channel id of a forward to a node id (blinded route): all bits set.</summary>
    public const ulong NodeIdForwardScid = ulong.MaxValue;

    /// <summary>The length of an interceptor's own error packet (LND: failure message 256 + hmac 32 + 2 + 2).</summary>
    public const int ErrorPacketLength = 256 + 32 + 2 + 2;

    /// <summary>
    /// LND's <c>HtlcInterceptor</c> (NL-1183): one client at a time; while it is connected every forward is held and
    /// sent to it, and its answers resume, fail or settle them. A malformed answer, an unknown circuit or a wrong
    /// preimage ends the stream with an error (LND does the same), which resumes every held forward.
    /// </summary>
    public override async Task HtlcInterceptor(IAsyncStreamReader<ForwardHtlcInterceptResponse> requestStream,
                                               IServerStreamWriter<ForwardHtlcInterceptRequest> responseStream,
                                               ServerCallContext context)
    {
        var client = new StreamClient();
        IDisposable connection;
        try
        {
            connection = _interceptorHub.Connect(client, new HtlcInterceptorSettings
            {
                CltvRejectDelta = _routerOptions.InterceptorCltvRejectDelta,
                CltvInterceptDelta = _routerOptions.InterceptorCltvInterceptDelta,
                MaxHeld = _routerOptions.MaxHeldHtlcs
            });
        }
        catch (InvalidOperationException)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, "interceptor already exists"));
        }

        var cancellationToken = context.CancellationToken;
        var writer = Task.Run(async () =>
        {
            await foreach (var request in client.Outbound.Reader.ReadAllAsync(cancellationToken))
                await responseStream.WriteAsync(request, cancellationToken);
        }, CancellationToken.None);
        try
        {
            while (await requestStream.MoveNext(cancellationToken))
            {
                var response = requestStream.Current;
                var (scid, htlcId, resolution) = ToResolution(response);
                switch (await _interceptorHub.ResolveAsync(scid, htlcId, resolution))
                {
                    case InterceptResolveResult.NotFound:
                        throw new RpcException(new Status(StatusCode.NotFound,
                                                          $"forward does not exist: {scid}/{htlcId}"));
                    case InterceptResolveResult.PreimageMismatch:
                        throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                          "preimage does not match hash"));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away
        }
        finally
        {
            connection.Dispose();
            client.Outbound.Writer.TryComplete();
            try
            {
                await writer;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException
                                          or RpcException)
            {
                // The stream is gone: nothing more to send
            }
        }
    }

    /// <summary>An answer as a hub resolution (LND's <c>resolveFromClient</c> rules).</summary>
    internal static (ShortChannelId Scid, ulong HtlcId, ForwardInterceptResolution Resolution) ToResolution(
        ForwardHtlcInterceptResponse response)
    {
        if (response.IncomingCircuitKey is not { } key)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              "CircuitKey missing from ForwardHtlcInterceptResponse"));

        var scid = new ShortChannelId(key.ChanId);
        ForwardInterceptResolution resolution;
        switch (response.Action)
        {
            case ResolveHoldForwardAction.Resume:
                resolution = ForwardInterceptResolution.Resume;
                break;
            case ResolveHoldForwardAction.ResumeModified:
                throw new RpcException(new Status(StatusCode.Unimplemented,
                                                  "RESUME_MODIFIED is not supported by this node"));
            case ResolveHoldForwardAction.Fail when response.FailureMessage.Length > 0:
                if (response.FailureCode != 0)
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                      "failure message and failure code are mutually exclusive"));
                if (response.FailureMessage.Length != ErrorPacketLength)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "failure message length invalid"));
                resolution = new ForwardInterceptResolution(ForwardInterceptAction.Fail,
                                                            ErrorPacket: response.FailureMessage.ToByteArray());
                break;
            case ResolveHoldForwardAction.Fail:
                resolution = new ForwardInterceptResolution(ForwardInterceptAction.Fail, FailureCode: response.FailureCode switch
                {
                    LnrpcFailureCode.InvalidOnionHmac => FailureCode.InvalidOnionHmac,
                    LnrpcFailureCode.InvalidOnionKey => FailureCode.InvalidOnionKey,
                    LnrpcFailureCode.InvalidOnionVersion => FailureCode.InvalidOnionVersion,
                    LnrpcFailureCode.Reserved or LnrpcFailureCode.TemporaryChannelFailure =>
                        FailureCode.TemporaryChannelFailure,
                    _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                           $"unsupported failure code: {response.FailureCode}"))
                });
                break;
            case ResolveHoldForwardAction.Settle:
                if (response.Preimage.Length == 0)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "missing preimage"));
                if (response.Preimage.Length != 32)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid preimage length"));
                resolution = new ForwardInterceptResolution(ForwardInterceptAction.Settle,
                                                            response.Preimage.ToByteArray());
                break;
            default:
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                  $"unrecognized resolve action {response.Action}"));
        }

        return (scid, key.HtlcId, resolution);
    }

    /// <summary>A held forward as LND sends it to its interceptor.</summary>
    internal static ForwardHtlcInterceptRequest ToRpc(InterceptedForward forward)
    {
        var request = new ForwardHtlcInterceptRequest
        {
            IncomingCircuitKey = new CircuitKey
            {
                ChanId = LightningService.ToChanId(forward.IncomingShortChannelId),
                HtlcId = forward.IncomingHtlcId
            },
            IncomingAmountMsat = (ulong)forward.IncomingAmount.MilliSatoshi,
            IncomingExpiry = forward.IncomingExpiry,
            PaymentHash = ByteString.CopyFrom((byte[])forward.PaymentHash),
            OutgoingRequestedChanId = forward.OutgoingRequestedNodeId is null
                                          ? LightningService.ToChanId(forward.OutgoingRequestedShortChannelId)
                                          : NodeIdForwardScid,
            OutgoingAmountMsat = (ulong)forward.OutgoingAmount.MilliSatoshi,
            OutgoingExpiry = forward.OutgoingExpiry,
            OnionBlob = ByteString.CopyFrom(forward.NextOnion.Span),
            AutoFailHeight = (int)Math.Min(forward.AutoFailHeight, int.MaxValue)
        };
        if (forward.OutgoingRequestedNodeId is { } nodeId)
            request.OutgoingRequestedNodeId = ByteString.CopyFrom((byte[])nodeId);
        foreach (var record in forward.CustomRecords)
            request.CustomRecords[record.Type] = ByteString.CopyFrom(record.Value.Span);
        return request;
    }

    /// <summary>The stream's side of the hub: offers are queued for the writer task, never awaited.</summary>
    private sealed class StreamClient : IHtlcInterceptorClient
    {
        public Channel<ForwardHtlcInterceptRequest> Outbound { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<ForwardHtlcInterceptRequest>();

        public bool TryOffer(InterceptedForward forward) => Outbound.Writer.TryWrite(ToRpc(forward));
    }
}