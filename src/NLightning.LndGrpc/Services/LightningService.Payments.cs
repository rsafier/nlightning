using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Domain.Channels.ValueObjects;
using Domain.Gossip.Graph;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Lnrpc;

public sealed partial class LightningService
{
    /// <summary>LND's default page of <c>ForwardingHistory</c> (and lncli's of <c>ListPayments</c>).</summary>
    private const ulong DefaultPaymentPage = 100;

    /// <summary>LND's largest <c>ForwardingHistory</c> page.</summary>
    private const uint MaxForwardingEvents = 50_000;

    /// <summary>
    /// <c>ListPayments</c> with LND's paging: <c>payment_index</c> is dense (NL-1165); forward after
    /// <c>index_offset</c> oldest first, <c>reversed</c> before it (0: from the newest), answered oldest first;
    /// without <c>include_incomplete</c> only succeeded payments (LND). Without <c>max_payments</c> LND's RPC returns
    /// every payment (lncli's 100 is the client's default, not the server's): this one returns up to its page cap
    /// (10,000; NL-1239). <c>creation_date_start</c>/<c>creation_date_end</c> bound the creation time in Unix seconds,
    /// both inclusive. Amounts in sat are the msat values truncated, as LND's <c>ToSatoshis</c>. Trampoline relay legs
    /// are never listed. The recorded route is one HTLC attempt unless <c>omit_hops</c>.
    /// </summary>
    public override async Task<ListPaymentsResponse> ListPayments(ListPaymentsRequest request,
                                                                  ServerCallContext context)
    {
        var query = PageQuery(request.IndexOffset, request.Reversed, request.MaxPayments, MaxPage,
                              request.CreationDateStart, request.CreationDateEnd);
        await using var scope = CreateScope();
        var repository = UnitOfWork(scope).PaymentDbRepository;
        var payments = await repository.ListByIndexAsync(query, !request.IncludeIncomplete);
        var ordered = request.Reversed ? payments.Reverse() : payments;
        var response = new ListPaymentsResponse();
        response.Payments.Add(ordered.Select(p => ToLndPayment(p, request.OmitHops)));
        if (response.Payments.Count > 0)
        {
            response.FirstIndexOffset = response.Payments[0].PaymentIndex;
            response.LastIndexOffset = response.Payments[^1].PaymentIndex;
        }

        if (request.CountTotalPayments)
            response.TotalNumPayments = (ulong)await repository.CountAsync(false);
        return response;
    }

    /// <summary>
    /// <c>ForwardingHistory</c>: our fulfilled forwards oldest first, <c>index_offset</c> counting forwards from the
    /// oldest that match <c>start_time</c>/<c>end_time</c> (Unix seconds against when the forward started; unset: no
    /// bound), <c>num_max_events</c> (default 100, at most 50,000),
    /// the channel filters on the page. <c>timestamp</c> is the settle time. Trampoline relays are not listed here.
    /// </summary>
    public override async Task<ForwardingHistoryResponse> ForwardingHistory(ForwardingHistoryRequest request,
                                                                            ServerCallContext context)
    {
        var take = (int)Math.Min(request.NumMaxEvents == 0 ? (uint)DefaultPaymentPage : request.NumMaxEvents,
                                 MaxForwardingEvents);
        DateTimeOffset? since = request.StartTime == 0 ? null : FromUnixSeconds(request.StartTime);
        DateTimeOffset? until = request.EndTime == 0 ? null : FromUnixSeconds(request.EndTime);
        await using var scope = CreateScope();
        var repository = UnitOfWork(scope).ForwardCircuitDbRepository;
        var all = new ForwardCircuitListQuery(0, 1, since, until, ForwardCircuitStatus.Fulfilled);
        var total = (await repository.SummarizeAsync(all, context.CancellationToken)).Fulfilled;
        var offset = (int)Math.Min(request.IndexOffset, int.MaxValue);
        var response = new ForwardingHistoryResponse { LastOffsetIndex = request.IndexOffset };
        if (offset >= total)
            return response;

        // The repository pages newest first: the oldest-first page [offset, offset + take) is its tail
        var count = Math.Min(take, total - offset);
        var page = await repository.ListAsync(all with { Skip = total - offset - count, Take = count },
                                              context.CancellationToken);
        var scids = ChannelScids();
        var graph = request.PeerAliasLookup ? Graph : null;
        var incoming = request.IncomingChanIds.ToHashSet();
        var outgoing = request.OutgoingChanIds.ToHashSet();
        foreach (var circuit in page.Reverse())
        {
            var chanIn = scids.TryGetValue(circuit.IncomingChannelId, out var inScid) ? inScid : 0;
            var chanOut = ToChanId(circuit.OutgoingShortChannelId);
            if ((incoming.Count > 0 && !incoming.Contains(chanIn)) || (outgoing.Count > 0 && !outgoing.Contains(chanOut)))
                continue;

            var time = circuit.ResolvedAt ?? circuit.CreatedAt;
            var forward = new ForwardingEvent
            {
                Timestamp = (ulong)time.ToUnixTimeSeconds(),
                TimestampNs = (ulong)UnixNanos(time),
                ChanIdIn = chanIn,
                ChanIdOut = chanOut,
                AmtIn = circuit.IncomingAmount.MilliSatoshi / 1000,
                AmtOut = circuit.OutgoingAmount.MilliSatoshi / 1000,
                AmtInMsat = circuit.IncomingAmount.MilliSatoshi,
                AmtOutMsat = circuit.OutgoingAmount.MilliSatoshi,
                Fee = circuit.Fee.MilliSatoshi / 1000,
                FeeMsat = circuit.Fee.MilliSatoshi,
                IncomingHtlcId = circuit.IncomingHtlcId
            };
            if (circuit.OutgoingHtlcId is { } outgoingHtlcId)
                forward.OutgoingHtlcId = outgoingHtlcId;
            if (graph is not null)
            {
                forward.PeerAliasIn = AliasOfChannel(graph, circuit.IncomingChannelId);
                forward.PeerAliasOut = circuit.OutgoingChannelId is { } outId ? AliasOfChannel(graph, outId) : "";
            }

            response.ForwardingEvents.Add(forward);
        }

        response.LastOffsetIndex = (uint)(offset + count);
        return response;
    }

    /// <summary>Our channels' SCIDs as LND chan_ids, live ones from memory.</summary>
    private Dictionary<ChannelId, ulong> ChannelScids() =>
        _channels.FindChannels(c => c.ShortChannelId.BlockHeight != 0)
                 .ToDictionary(c => c.ChannelId, c => ToChanId(c.ShortChannelId));

    private string AliasOfChannel(IGraphView graph, ChannelId channelId) =>
        _channels.TryGetChannel(channelId, out var channel) && graph.TryGetNode(channel.RemoteNodeId, out var node)
            ? node.AliasText
            : string.Empty;

    /// <summary>
    /// An amount in whole sat as LND's <c>MilliSatoshi.ToSatoshis</c> gives it: truncated (<c>LightningMoney.Satoshi</c>
    /// rounds to the nearest, which made 1,600 msat 2 sat; NL-1239).
    /// </summary>
    internal static long Sat(LightningMoney amount) => (long)(amount.MilliSatoshi / 1000);

    /// <summary>LND's <c>payment_index</c> (dense, NL-1165); 0 for a row saved without one.</summary>
    internal static ulong PaymentIndex(PaymentModel payment) => payment.PaymentIndex ?? 0;

    internal static Payment ToLndPayment(PaymentModel payment, bool omitHops)
    {
        var status = payment.Status switch
        {
            PaymentStatus.Succeeded => Payment.Types.PaymentStatus.Succeeded,
            PaymentStatus.Failed => Payment.Types.PaymentStatus.Failed,
            _ => Payment.Types.PaymentStatus.InFlight
        };
        var preimage = payment.Preimage is { } secret ? Convert.ToHexStringLower((byte[])secret) : string.Empty;
        var item = new Payment
        {
            PaymentHash = payment.PaymentHash.ToString(),
            Value = Sat(payment.Amount),
            ValueSat = Sat(payment.Amount),
            ValueMsat = (long)payment.Amount.MilliSatoshi,
            CreationDate = payment.CreatedAt.ToUnixTimeSeconds(),
            CreationTimeNs = UnixNanos(payment.CreatedAt),
            Fee = Sat(payment.Fee),
            FeeSat = Sat(payment.Fee),
            FeeMsat = (long)payment.Fee.MilliSatoshi,
            PaymentPreimage = preimage,
            PaymentRequest = payment.Bolt11 ?? string.Empty,
            Status = status,
            PaymentIndex = PaymentIndex(payment),
            FailureReason = payment.Status != PaymentStatus.Failed
                                ? PaymentFailureReason.FailureReasonNone
                                : payment.FailureCode switch
                                {
                                    FailureCode.IncorrectOrUnknownPaymentDetails =>
                                        PaymentFailureReason.FailureReasonIncorrectPaymentDetails,
                                    null => PaymentFailureReason.FailureReasonNoRoute,
                                    _ => PaymentFailureReason.FailureReasonError
                                }
        };
        if (omitHops || payment.Route.Count == 0)
            return item;

        var route = new Route
        {
            TotalTimeLock = payment.Route[0].CltvExpiry,
            TotalAmt = Sat(payment.Route[0].Amount),
            TotalAmtMsat = (long)payment.Route[0].Amount.MilliSatoshi,
            TotalFees = Sat(payment.Fee),
            TotalFeesMsat = (long)payment.Fee.MilliSatoshi,
            FirstHopAmountMsat = (long)payment.Route[0].Amount.MilliSatoshi
        };
        for (var i = 0; i < payment.Route.Count; i++)
        {
            var hop = payment.Route[i];
            var next = i + 1 < payment.Route.Count ? payment.Route[i + 1] : hop;
            route.Hops.Add(new Hop
            {
                ChanId = ToChanId(hop.ShortChannelId),
                PubKey = hop.NodeId.ToString(),
                Expiry = next.CltvExpiry,
                AmtToForward = Sat(next.Amount),
                AmtToForwardMsat = (long)next.Amount.MilliSatoshi,
                Fee = (long)((hop.Amount.MilliSatoshi - next.Amount.MilliSatoshi) / 1000),
                FeeMsat = (long)(hop.Amount.MilliSatoshi - next.Amount.MilliSatoshi),
                TlvPayload = true
            });
        }

        item.Htlcs.Add(new HTLCAttempt
        {
            Status = status switch
            {
                Payment.Types.PaymentStatus.Succeeded => HTLCAttempt.Types.HTLCStatus.Succeeded,
                Payment.Types.PaymentStatus.Failed => HTLCAttempt.Types.HTLCStatus.Failed,
                _ => HTLCAttempt.Types.HTLCStatus.InFlight
            },
            Route = route,
            AttemptTimeNs = UnixNanos(payment.CreatedAt),
            ResolveTimeNs = payment.CompletedAt is { } completed ? UnixNanos(completed) : 0,
            Preimage = payment.Preimage is { } p
                           ? Google.Protobuf.ByteString.CopyFrom((byte[])p)
                           : Google.Protobuf.ByteString.Empty
        });
        return item;
    }
}