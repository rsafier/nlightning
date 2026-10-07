using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace NLightning.LndGrpc.Services;

using Application.Payments.Routing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Models;
using Domain.Money;
using Domain.Payments.Keysend;
using Lnrpc;

public sealed partial class LightningService
{
    /// <summary>LND's error when no route fits; ln-service reads it as "no route" (an empty answer).</summary>
    internal const string NoPathError = "unable to find a path to destination";

    /// <summary>LND's default final CLTV delta when <c>final_cltv_delta</c> is 0 (<c>zpay32</c>).</summary>
    private const ushort DefaultQueryFinalCltvDelta = 18;

    /// <summary>
    /// <c>QueryRoutes</c> (NL-1242): one route, planned by the node's payment planner as a one-part payment's first
    /// round would plan it (our live channels first, then route hints, then the gossip graph with mission control),
    /// nothing sent. Honoured as LND does: <c>pub_key</c> (this node for a circular route back over another of our
    /// channels), <c>amt</c>/<c>amt_msat</c>, <c>final_cltv_delta</c> (18 when 0; the destination's expiry is exactly the
    /// height plus it, no padding), <c>fee_limit</c> (fixed, fixed_msat, percent; unset: 100 % up to 1,000 sat, else
    /// 5 %), <c>ignored_nodes</c>, <c>ignored_pairs</c> (that direction of every channel between the two nodes),
    /// <c>use_mission_control</c>, <c>cltv_limit</c>, <c>outgoing_chan_ids</c>, <c>last_hop_pubkey</c>,
    /// <c>route_hints</c> and <c>dest_custom_records</c> (types of 65536 or more, returned on the final hop).
    /// <c>dest_features</c> and <c>time_pref</c> are accepted and do not change the route. Refused:
    /// <c>source_pub_key</c> other than ours (we route only from our live channels), <c>blinded_payment_paths</c>,
    /// the deprecated <c>ignored_edges</c>. No route answers LND's <c>unable to find a path to destination</c>.
    /// The route has no <c>mpp_record</c>, as LND's: the caller adds the payment address and total
    /// (<c>SendToRouteV2</c>).
    /// </summary>
    public override async Task<QueryRoutesResponse> QueryRoutes(QueryRoutesRequest request, ServerCallContext context)
    {
        if (_routeQuery is null)
            throw Unimplemented("route queries are not available on this node");

        var query = ParseRouteQuery(request);
        RouteQuote quote;
        try
        {
            quote = await _routeQuery.QueryRouteAsync(query, context.CancellationToken);
        }
        catch (ArgumentException e)
        {
            throw InvalidArgument(e.Message);
        }
        catch (InvalidOperationException e)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("QueryRoutes to {Destination}: {Reason}", request.PubKey, e.Message);
            throw new RpcException(new Status(StatusCode.Unknown, NoPathError));
        }

        var response = new QueryRoutesResponse { SuccessProb = quote.Probability };
        response.Routes.Add(ToLndRoute(quote, request.DestCustomRecords));
        return response;
    }

    /// <summary>The planner's restrictions from an LND request (see <see cref="QueryRoutes"/>).</summary>
    private RouteQueryRequest ParseRouteQuery(QueryRoutesRequest request)
    {
        var ourNodeId = _signer.GetNodePublicKey();
        if (request.SourcePubKey.Length > 0 && !string.Equals(request.SourcePubKey, ourNodeId.ToString(),
                                                              StringComparison.OrdinalIgnoreCase))
            throw InvalidArgument("source_pub_key other than this node is not supported");
        if (request.BlindedPaymentPaths.Count > 0)
            throw Unimplemented("blinded_payment_paths are not supported");
        if (request.IgnoredEdges.Count > 0)
            throw InvalidArgument("ignored_edges is deprecated; use ignored_pairs");
        if (request.Amt != 0 && request.AmtMsat != 0)
            throw InvalidArgument("amount must be specified either in sat or msat, not both");
        if (request.Amt < 0 || request.AmtMsat < 0)
            throw InvalidArgument("amount cannot be negative");
        if (request.FinalCltvDelta < 0 || request.FinalCltvDelta > ushort.MaxValue)
            throw InvalidArgument("final_cltv_delta is out of range");

        var amount = request.AmtMsat != 0
                         ? LightningMoney.MilliSatoshis((ulong)request.AmtMsat)
                         : LightningMoney.Satoshis(request.Amt);
        if (amount.IsZero)
            throw InvalidArgument("amount must be greater than zero");

        // Checked like a keysend payment's records: the custom range only, the keysend preimage allowed
        foreach (var type in request.DestCustomRecords.Keys)
        {
            if (type < CustomRecordCodec.MinType)
                throw InvalidArgument($"invalid custom record type {type}: types below {CustomRecordCodec.MinType} "
                                    + "are reserved");
        }

        var payee = ParsePubKey(request.PubKey, "pub_key");
        CompactPubKey? lastHop = request.LastHopPubkey.Length > 0
                                     ? ParsePubKey(request.LastHopPubkey.ToByteArray(), "last_hop_pubkey")
                                     : null;
        var ignoredNodes = request.IgnoredNodes.Select(n => ParsePubKey(n.ToByteArray(), "ignored_nodes"))
                                  .ToHashSet();
        var ignoredPairs = request.IgnoredPairs.Select(p => (ParsePubKey(p.From.ToByteArray(), "ignored_pairs.from"),
                                                             ParsePubKey(p.To.ToByteArray(), "ignored_pairs.to")))
                                  .ToList();
        var hints = new List<IReadOnlyList<RoutingInfo>>();
        foreach (var hint in request.RouteHints)
        {
            var entries = new List<RoutingInfo>();
            foreach (var hop in hint.HopHints)
            {
                if (hop.CltvExpiryDelta > ushort.MaxValue)
                    throw InvalidArgument("route hint cltv_expiry_delta is out of range");
                entries.Add(new RoutingInfo(ParsePubKey(hop.NodeId, "route_hints.node_id"),
                                            new ShortChannelId(hop.ChanId), hop.FeeBaseMsat,
                                            hop.FeeProportionalMillionths, (ushort)hop.CltvExpiryDelta));
            }

            if (entries.Count > 0)
                hints.Add(entries);
        }

        return new RouteQueryRequest(payee, amount)
        {
            MaxFee = FeeLimitFor(request.FeeLimit, amount),
            FinalCltvDelta = request.FinalCltvDelta == 0
                                 ? DefaultQueryFinalCltvDelta
                                 : (ushort)request.FinalCltvDelta,
            MaxTotalCltvDelta = request.CltvLimit == 0 ? null : request.CltvLimit,
            IgnoredNodes = ignoredNodes,
            IgnoredPairs = ignoredPairs,
            OutgoingChannels = request.OutgoingChanIds.Count > 0
                                   ? request.OutgoingChanIds.Select(id => new ShortChannelId(id)).ToHashSet()
                                   : null,
            LastHop = lastHop,
            RouteHints = hints,
            UseMissionControl = request.UseMissionControl
        };
    }

    /// <summary>LND's <c>CalculateFeeLimit</c>: fixed, fixed_msat or percent of the amount; unset: the whole amount
    /// up to 1,000 sat, 5 % above.</summary>
    internal static LightningMoney FeeLimitFor(FeeLimit? limit, LightningMoney amount)
    {
        switch (limit?.LimitCase ?? FeeLimit.LimitOneofCase.None)
        {
            case FeeLimit.LimitOneofCase.Fixed:
                if (limit!.Fixed < 0)
                    throw InvalidArgument("fee_limit cannot be negative");
                return LightningMoney.Satoshis(limit.Fixed);
            case FeeLimit.LimitOneofCase.FixedMsat:
                if (limit!.FixedMsat < 0)
                    throw InvalidArgument("fee_limit cannot be negative");
                return LightningMoney.MilliSatoshis((ulong)limit.FixedMsat);
            case FeeLimit.LimitOneofCase.Percent:
                if (limit!.Percent < 0)
                    throw InvalidArgument("fee_limit cannot be negative");
                return LightningMoney.MilliSatoshis(amount.MilliSatoshi * (ulong)limit.Percent / 100);
            default:
                return amount.MilliSatoshi <= 1_000_000
                           ? amount
                           : LightningMoney.MilliSatoshis(amount.MilliSatoshi * 5 / 100);
        }
    }

    /// <summary>
    /// LND's <c>Route</c> for a quote: <c>hops[i]</c> is the node at the end of <c>chan_id</c> (our channel for the
    /// first), what it forwards (<c>amt_to_forward</c>; the destination: what it receives), the fee it keeps and the
    /// expiry of the HTLC it offers next (<c>expiry</c>; the destination's: its own); <c>total_time_lock</c> and
    /// <c>total_amt</c> are our HTLC's.
    /// </summary>
    private Route ToLndRoute(RouteQuote quote, IDictionary<ulong, ByteString>? destCustomRecords)
    {
        var route = quote.Route;
        var graph = Graph;
        var ourChannel = _channels.FindChannels(c => c.ChannelId == quote.Channel.ChannelId).FirstOrDefault();
        var result = new Route
        {
            TotalTimeLock = route.FirstHopCltvExpiry,
            TotalAmt = Sat(route.FirstHopAmount),
            TotalAmtMsat = (long)route.FirstHopAmount.MilliSatoshi,
            TotalFees = Sat(route.Fee),
            TotalFeesMsat = (long)route.Fee.MilliSatoshi,
            FirstHopAmountMsat = (long)route.FirstHopAmount.MilliSatoshi
        };
        for (var i = 0; i < route.Hops.Count; i++)
        {
            var hop = route.Hops[i];
            var received = i == 0 ? route.FirstHopAmount : route.Hops[i - 1].AmountToForward;
            var fee = received - hop.AmountToForward;
            var scid = i == 0 ? quote.Channel.ShortChannelId : route.Hops[i - 1].OutgoingShortChannelId!.Value;
            long capacity = 0;
            if (i == 0)
                capacity = ourChannel?.FundingOutput?.Amount.Satoshi ?? 0;
            else if (graph is not null && graph.TryGetChannel(scid, out var channel) && channel.CapacitySat is { } sat)
                capacity = (long)sat;

            var lndHop = new Hop
            {
                ChanId = ToChanId(scid),
                ChanCapacity = capacity,
                AmtToForward = Sat(hop.AmountToForward),
                AmtToForwardMsat = (long)hop.AmountToForward.MilliSatoshi,
                Fee = Sat(fee),
                FeeMsat = (long)fee.MilliSatoshi,
                Expiry = hop.OutgoingCltvValue,
                PubKey = hop.NodeId.ToString(),
                TlvPayload = true
            };
            if (i == route.Hops.Count - 1 && destCustomRecords is not null)
                lndHop.CustomRecords.Add(destCustomRecords);
            result.Hops.Add(lndHop);
        }

        return result;
    }

    private static CompactPubKey ParsePubKey(string hex, string field)
    {
        try
        {
            return ParsePubKey(Convert.FromHexString(hex), field);
        }
        catch (FormatException)
        {
            throw InvalidArgument($"{field} is not a hex public key");
        }
    }

    private static CompactPubKey ParsePubKey(byte[] bytes, string field)
    {
        if (bytes.Length != 33 || !NBitcoin.PubKey.TryCreatePubKey(bytes, out _))
            throw InvalidArgument($"{field} is not a valid compressed public key");
        return new CompactPubKey(bytes);
    }
}