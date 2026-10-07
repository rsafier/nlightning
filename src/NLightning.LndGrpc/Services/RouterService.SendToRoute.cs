using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace NLightning.LndGrpc.Services;

using Application.Payments.Send;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Lnrpc;
using Routerrpc;
using RpcCode = Lnrpc.Failure.Types.FailureCode;
using WireCode = Domain.Protocol.Onion.Enums.FailureCode;

public sealed partial class RouterService
{
    /// <summary>
    /// <c>SendToRouteV2</c> (NL-1242): one HTLC over exactly the caller's route (as <c>QueryRoutes</c> returns it),
    /// through the node's <c>payroute</c> (<see cref="IPaymentService.PayRouteAsync"/>), answered when it settles or
    /// fails, as LND does. The route is read as LND reads it: <c>total_amt_msat</c>/<c>total_time_lock</c> are our
    /// HTLC, <c>hops[0].chan_id</c> our channel, each hop's <c>amt_to_forward_msat</c>/<c>expiry</c> what it forwards on
    /// and the final hop's <c>mpp_record</c> the payment address and total (absent: the all-zero secret, which is
    /// what a probe sends). A <c>keysend_preimage</c> record (5482373484) in the final hop's <c>custom_records</c>
    /// makes it a keysend payment with the other custom records; custom records without it are refused.
    /// A failed HTLC is an <c>HTLCAttempt</c> with LND's <c>failure</c> (<c>code</c>, <c>failure_source_index</c>: 0
    /// is this node, 1 our peer, the hop count the destination); our channel unable to carry the HTLC is
    /// <c>TEMPORARY_CHANNEL_FAILURE</c> at index 0, as LND reports it. The attempt goes on if the caller leaves.
    /// A call with an <c>mpp_record</c> and the hash of a <c>payroute</c> payment in flight is one more shard of it
    /// (NL-1276, LND's MPP <c>SendToRouteV2</c> over several calls): it must carry the same <c>mpp_record</c> (payment
    /// address and total; a mismatch is <c>InvalidArgument</c>), the shards in flight may not deliver more than the
    /// total together, and each call answers with its own HTLC's outcome (a held shard resolves when the payee settles
    /// or fails the set). A call without an <c>mpp_record</c> never joins: a payment of the hash in flight refuses it
    /// (<c>FailedPrecondition</c>, LND's <c>ErrPaymentInFlight</c>). As in LND, a failed shard sent without
    /// <c>skip_temp_err</c> (the default), or any shard failing with a failure the node would never retry, fails the
    /// payment pending: later shards are refused (<c>FailedPrecondition</c>, LND's "payment pending failed") until
    /// the shards in flight are resolved; with <c>skip_temp_err</c> a temporary failure leaves the payment open for a
    /// replacement shard. No fee limit applies to the shards, as in LND's <c>sendToRoute</c>. Each attempt reports
    /// its own HTLC's offer and resolution times. Refused: <c>first_hop_custom_records</c>, AMP, blinded hops, a route
    /// back to this node. Not filled: <c>failure.channel_update</c>.
    /// </summary>
    public override async Task<HTLCAttempt> SendToRouteV2(SendToRouteRequest request, ServerCallContext context)
    {
        if (request.FirstHopCustomRecords.Count > 0)
            throw Unimplemented("first_hop_custom_records are not supported");
        if (request.PaymentHash.Length != 32)
            throw InvalidArgument("payment_hash must be 32 bytes");
        if (request.Route is not { Hops.Count: > 0 } route)
            throw InvalidArgument("the route has no hops");

        var payRoute = ParseRoute(request, route);
        PayRouteResult result;
        try
        {
            result = await Task.Run(() => _paymentService.PayRouteAsync(payRoute, new PayInvoiceOptions
            {
                Timeout = Timeout.InfiniteTimeSpan
            }, CancellationToken.None))
                                .WaitAsync(context.CancellationToken);
        }
        catch (PayRouteLiquidityException e)
        {
            // LND: the local channel cannot carry the HTLC, a temporary channel failure at our own node
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("SendToRouteV2 refused locally: {Reason}", e.Message);
            return new HTLCAttempt
            {
                Status = HTLCAttempt.Types.HTLCStatus.Failed,
                Route = route,
                AttemptTimeNs = LightningService.UnixNanos(_timeProvider.GetUtcNow()),
                ResolveTimeNs = LightningService.UnixNanos(_timeProvider.GetUtcNow()),
                Failure = new Failure
                {
                    Code = RpcCode.TemporaryChannelFailure,
                    FailureSourceIndex = 0,
                    HtlcMsat = (ulong)route.TotalAmtMsat
                }
            };
        }
        catch (ArgumentException e)
        {
            throw InvalidArgument(e.Message);
        }
        catch (InvalidOperationException e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }

        var payment = result.Payment;
        var outcome = result.Outcomes.Count > 0 ? result.Outcomes[0] : null;
        var attempt = new HTLCAttempt
        {
            AttemptId = outcome?.HtlcId ?? 0,
            Route = route,
            // The shard's own times (NL-1276): another shard of the set may have been offered first, and the set may
            // still be in flight when this one failed
            AttemptTimeNs = LightningService.UnixNanos(outcome?.OfferedAt ?? payment.CreatedAt),
            ResolveTimeNs = outcome?.ResolvedAt is { } resolved
                                ? LightningService.UnixNanos(resolved)
                                : outcome is null or { Status: not PaymentPartState.InFlight }
                                    ? LightningService.UnixNanos(_timeProvider.GetUtcNow())
                                    : 0
        };
        switch (outcome?.Status)
        {
            case PaymentPartState.Succeeded:
                attempt.Status = HTLCAttempt.Types.HTLCStatus.Succeeded;
                if (payment.Preimage is { } preimage)
                    attempt.Preimage = ByteString.CopyFrom((byte[])preimage);
                break;
            case PaymentPartState.InFlight:
                attempt.Status = HTLCAttempt.Types.HTLCStatus.InFlight;
                break;
            default:
                attempt.Status = HTLCAttempt.Types.HTLCStatus.Failed;
                attempt.Failure = ToLndFailure(outcome, route);
                break;
        }

        return attempt;
    }

    /// <summary>The <c>payroute</c> request for an LND route (see <see cref="SendToRouteV2"/>).</summary>
    private PayRouteRequest ParseRoute(SendToRouteRequest request, Route route)
    {
        var final = route.Hops[^1];
        if (route.Hops.Any(h => h.AmpRecord is not null))
            throw Unimplemented("AMP is not supported");
        if (route.Hops.Any(h => h.EncryptedData.Length > 0 || h.BlindingPoint.Length > 0))
            throw Unimplemented("routes into blinded paths are not supported");
        if (route.Hops.Take(route.Hops.Count - 1).Any(h => h.CustomRecords.Count > 0 || h.MppRecord is not null))
            throw InvalidArgument("only the final hop may carry custom records or an mpp_record");

        var firstHopAmount = route.TotalAmtMsat > 0 ? LightningMoney.MilliSatoshis((ulong)route.TotalAmtMsat)
                             : route.TotalAmt > 0 ? LightningMoney.Satoshis(route.TotalAmt)
                             : throw InvalidArgument("the route has no total_amt_msat");
        var hops = new List<PayRouteHop>(route.Hops.Count);
        for (var i = 0; i < route.Hops.Count; i++)
        {
            var hop = route.Hops[i];
            CompactPubKey nodeId;
            try
            {
                var bytes = Convert.FromHexString(hop.PubKey);
                if (bytes.Length != 33)
                    throw new FormatException();
                _ = new NBitcoin.PubKey(bytes);
                nodeId = new CompactPubKey(bytes);
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                throw InvalidArgument($"hop {i} has no valid pub_key");
            }

            var forward = hop.AmtToForwardMsat > 0 ? LightningMoney.MilliSatoshis((ulong)hop.AmtToForwardMsat)
                                                   : LightningMoney.Satoshis(hop.AmtToForward);
            var next = i + 1 < route.Hops.Count
                           ? new ShortChannelId(route.Hops[i + 1].ChanId)
                           : (ShortChannelId?)null;
            hops.Add(new PayRouteHop(nodeId, next, forward, hop.Expiry));
        }

        var records = final.CustomRecords.ToDictionary(p => p.Key, p => p.Value);
        Secret? keysendPreimage = null;
        if (records.Remove(CustomRecordCodec.KeysendPreimageType, out var preimage))
        {
            if (preimage.Length != 32)
                throw InvalidArgument("the keysend preimage must be 32 bytes");
            keysendPreimage = new Secret(preimage.ToByteArray());
        }

        if (records.Count > 0 && keysendPreimage is null)
            throw Unimplemented("custom records are only sent with a keysend preimage");

        Secret? paymentSecret = null;
        LightningMoney? total = null;
        if (final.MppRecord is { } mpp)
        {
            if (keysendPreimage is not null)
                throw InvalidArgument("a keysend route carries no mpp_record");
            if (mpp.PaymentAddr.Length is not (0 or 32))
                throw InvalidArgument("mpp_record.payment_addr must be 32 bytes");
            if (mpp.PaymentAddr.Length == 32)
                paymentSecret = new Secret(mpp.PaymentAddr.ToByteArray());
            if (mpp.TotalAmtMsat > 0)
                total = LightningMoney.MilliSatoshis((ulong)mpp.TotalAmtMsat);
        }

        return new PayRouteRequest
        {
            PaymentHash = new Hash(request.PaymentHash.ToByteArray()),
            PaymentSecret = paymentSecret,
            TotalAmount = total,
            KeysendPreimage = keysendPreimage,
            CustomRecords = records.Select(p => new CustomRecord(p.Key, p.Value.Span)).ToList(),
            Routes =
            [
                new PayRouteRoute(OutgoingChannel(route.Hops[0].ChanId), firstHopAmount, route.TotalTimeLock, hops)
            ],
            // NL-1276: an MPP set sent over several calls (ln-service's multi-path pay sends its shards in parallel
            // and replaces a failed one) joins the payroute payment of the hash in flight, as LND registers each
            // call as one more attempt of the payment. LND tolerates a payment in flight only for an MPP shard
            // (InitPayment's ErrPaymentInFlight is refused without an mpp_record)
            Attach = final.MppRecord is null ? PayRouteAttachMode.Never : PayRouteAttachMode.IfInFlight,
            IndependentShards = true,
            SkipTemporaryFailures = request.SkipTempErr
        };
    }

    /// <summary>
    /// LND's <c>Failure</c> for a failed route: our index is 0 for our peer, LND's 0 for this node, so the erring hop
    /// moves up by one; an HTLC never offered (our channel refused it) or failed by this node is index 0.
    /// </summary>
    private static Failure ToLndFailure(RouteOutcome? outcome, Route route)
    {
        if (outcome is null || (outcome.HtlcId is null && outcome.FailureCode is null))
            return new Failure
            {
                Code = RpcCode.TemporaryChannelFailure,
                FailureSourceIndex = 0,
                HtlcMsat = (ulong)route.TotalAmtMsat
            };

        if (outcome.FailureCode is not { } code)
            return new Failure { Code = RpcCode.UnreadableFailure, FailureSourceIndex = 0 };

        return new Failure
        {
            Code = code == WireCode.TemporaryTrampolineFailure ? RpcCode.TemporaryNodeFailure
                                                               : MapHtlcFailure((ushort)code),
            FailureSourceIndex = outcome.FailureSourceIndex is { } index ? (uint)(index + 1) : 0
        };
    }
}