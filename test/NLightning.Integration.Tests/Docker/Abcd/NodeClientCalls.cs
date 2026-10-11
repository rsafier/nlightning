using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Abcd;

using Daemon.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Utils;

/// <summary>
/// What the Docker payment tests ask an in-process node, always through the daemon's client command handlers (the
/// same path as <c>nltg createinvoice/payinvoice/listinvoices/listpayments/listchannels</c>), never through the
/// services behind them.
/// </summary>
public static class NodeClientCalls
{
    /// <summary>
    /// How many invoices or payments the lookups scan (newest first). The shared regtest network keeps nodes alive
    /// for a whole collection run, so this is far above what one run creates.
    /// </summary>
    private const int LookupWindow = 1_000;

    /// <summary>
    /// <c>createinvoice</c>: a persisted, payable invoice for <paramref name="amount"/>.
    /// </summary>
    public static async Task<InvoiceInfoClientResponse> CreateInvoiceAsync(this NLightningTestNode node,
                                                                          LightningMoney amount, string description,
                                                                          CancellationToken cancellationToken)
    {
        var response = await HandleAsync<CreateInvoiceClientRequest, CreateInvoiceClientResponse>(
                           node, new CreateInvoiceClientRequest { Amount = amount, Description = description },
                           cancellationToken);
        return response.Invoice;
    }

    /// <summary>
    /// <c>payinvoice</c>: pays <paramref name="bolt11"/> and waits up to <paramref name="timeoutSeconds"/> for the
    /// outcome (the payment is still <c>InFlight</c> when the wait ends first).
    /// </summary>
    public static async Task<PaymentInfoClientResponse> PayInvoiceAsync(this NLightningTestNode node, string bolt11,
                                                                       CancellationToken cancellationToken,
                                                                       uint timeoutSeconds = 60)
    {
        var response = await HandleAsync<PayInvoiceClientRequest, PayInvoiceClientResponse>(
                           node, new PayInvoiceClientRequest(bolt11) { TimeoutSeconds = timeoutSeconds },
                           cancellationToken);
        return response.Payment;
    }

    /// <summary>
    /// <c>listinvoices</c>, filtered to <paramref name="paymentHash"/>; null when the node never issued it.
    /// </summary>
    public static async Task<InvoiceInfoClientResponse?> GetInvoiceAsync(this NLightningTestNode node,
                                                                        Hash paymentHash,
                                                                        CancellationToken cancellationToken)
    {
        var response = await HandleAsync<ListInvoicesClientRequest, ListInvoicesClientResponse>(
                           node, new ListInvoicesClientRequest { Take = LookupWindow }, cancellationToken);
        return response.Invoices.FirstOrDefault(i => i.PaymentHash == paymentHash);
    }

    /// <summary>
    /// <c>listpayments</c>, filtered to <paramref name="paymentHash"/>; null when the node never paid it.
    /// </summary>
    public static async Task<PaymentInfoClientResponse?> GetPaymentAsync(this NLightningTestNode node,
                                                                        Hash paymentHash,
                                                                        CancellationToken cancellationToken)
    {
        var response = await HandleAsync<ListPaymentsClientRequest, ListPaymentsClientResponse>(
                           node, new ListPaymentsClientRequest { Take = LookupWindow }, cancellationToken);
        return response.Payments.FirstOrDefault(p => p.PaymentHash == paymentHash);
    }

    /// <summary>
    /// <c>listforwards</c>, filtered to <paramref name="paymentHash"/>; null when the node never forwarded it
    /// (NL-597).
    /// </summary>
    public static async Task<ForwardInfoClientResponse?> GetForwardAsync(this NLightningTestNode node,
                                                                         Hash paymentHash,
                                                                         CancellationToken cancellationToken)
    {
        var response = await HandleAsync<ListForwardsClientRequest, ListForwardsClientResponse>(
                           node, new ListForwardsClientRequest { Take = LookupWindow }, cancellationToken);
        return response.Forwards.FirstOrDefault(f => f.PaymentHash == paymentHash);
    }

    /// <summary>
    /// <c>getroute</c>: the route a payment of <paramref name="amount"/> to <paramref name="destination"/> would take.
    /// </summary>
    public static async Task<GetRouteClientResponse> GetRouteAsync(this NLightningTestNode node,
                                                                    CompactPubKey destination, LightningMoney amount,
                                                                    CancellationToken cancellationToken,
                                                                    ushort? finalCltvDelta = null)
    {
        return await HandleAsync<GetRouteClientRequest, GetRouteClientResponse>(
                   node, new GetRouteClientRequest(destination, amount) { FinalCltvDelta = finalCltvDelta },
                   cancellationToken);
    }

    /// <summary>
    /// <c>payroute</c> (NL-1082): pays the invoice over exactly <paramref name="routes"/>, through the daemon's own
    /// IPC handler — the same path the CLI takes.
    /// </summary>
    public static async Task<PayRouteClientResponse> PayRouteAsync(this NLightningTestNode node, string bolt11,
                                                                   IReadOnlyList<PayRouteRouteClientInfo> routes,
                                                                   CancellationToken cancellationToken,
                                                                   uint timeoutSeconds = 120)
    {
        return await HandleAsync<PayRouteClientRequest, PayRouteClientResponse>(
                   node, new PayRouteClientRequest { Bolt11 = bolt11, Routes = routes, TimeoutSeconds = timeoutSeconds },
                   cancellationToken);
    }

    /// <summary>
    /// <c>listchannels</c>, the channel <paramref name="channelId"/> (fails the test when it is not listed).
    /// </summary>
    public static async Task<ChannelInfoClientResponse> GetChannelAsync(this NLightningTestNode node,
                                                                       ChannelId channelId,
                                                                       CancellationToken cancellationToken)
    {
        var channels = await node.ListChannelsAsync(cancellationToken);
        return Assert.Single(channels.Channels, c => c.ChannelId == channelId);
    }

    /// <summary>
    /// Whether the channel accepts HTLCs: <c>Open</c>, the peer connected and <c>channel_reestablish</c> exchanged on
    /// the current connection.
    /// </summary>
    public static bool IsUsable(this ChannelInfoClientResponse channel) =>
        channel is { State: ChannelState.Open, IsPeerConnected: true, IsReestablished: true };

    /// <summary>
    /// One line describing the channel, for timeout messages and the failure dump.
    /// </summary>
    public static string Describe(this ChannelInfoClientResponse channel) =>
        $"{channel.ChannelId} {channel.State} scid={channel.ShortChannelId?.ToString() ?? "-"} "
      + $"connected={channel.IsPeerConnected} reestablished={channel.IsReestablished} "
      + $"local={channel.LocalBalance.MilliSatoshi} remote={channel.RemoteBalance.MilliSatoshi} "
      + $"commit={channel.LocalCommitmentNumber}/{channel.RemoteCommitmentNumber} "
      + $"htlcs={channel.OfferedHtlcCount}/{channel.ReceivedHtlcCount}";

    /// <summary>
    /// The BOLT 7 <c>u64</c> form of a short channel id (LND's <c>chan_id</c>, the route-hint <c>chan_id</c>).
    /// </summary>
    public static ulong ToUInt64(this ShortChannelId shortChannelId) =>
        ((ulong)shortChannelId.BlockHeight << 40)
      | ((ulong)shortChannelId.TransactionIndex << 16)
      | shortChannelId.OutputIndex;

    /// <summary>
    /// LND's <c>txid:index</c> for a channel we funded: the txid in display order, which is our stored (internal
    /// order) txid reversed.
    /// </summary>
    public static string ChannelPoint(this OpenChannelClientSubscriptionResponse channel)
    {
        Assert.NotNull(channel.TxId);
        var displayOrder = ((byte[])channel.TxId.Value).Reverse().ToArray();
        return $"{Convert.ToHexString(displayOrder).ToLowerInvariant()}:{channel.Index}";
    }

    /// <summary>
    /// <c>createholdinvoice</c> (NL-995): a persisted hold invoice for a caller-chosen
    /// <paramref name="paymentHash"/> whose preimage we do not know; its paying HTLC set is held once complete.
    /// </summary>
    public static async Task<InvoiceInfoClientResponse> CreateHoldInvoiceAsync(this NLightningTestNode node,
                                                                               Hash paymentHash,
                                                                               LightningMoney amount,
                                                                               string description,
                                                                               CancellationToken cancellationToken)
    {
        var response = await HandleAsync<CreateHoldInvoiceClientRequest, HoldInvoiceClientResponse>(
                           node, new CreateHoldInvoiceClientRequest
                           {
                               PaymentHash = paymentHash,
                               Amount = amount,
                               Description = description
                           },
                           cancellationToken);
        return response.Invoice;
    }

    /// <summary>
    /// <c>settleholdinvoice</c> (NL-995): settles a held invoice with the outside <paramref name="preimage"/> and
    /// fulfills every held part with it.
    /// </summary>
    public static async Task<InvoiceInfoClientResponse> SettleHoldInvoiceAsync(this NLightningTestNode node,
                                                                               Hash paymentHash, Secret preimage,
                                                                               CancellationToken cancellationToken)
    {
        var response = await HandleAsync<SettleHoldInvoiceClientRequest, HoldInvoiceClientResponse>(
                           node, new SettleHoldInvoiceClientRequest { PaymentHash = paymentHash, Preimage = preimage },
                           cancellationToken);
        return response.Invoice;
    }

    /// <summary>
    /// <c>cancelholdinvoice</c> (NL-995): cancels an open or held hold invoice; a held set's parts are failed back.
    /// </summary>
    public static async Task<InvoiceInfoClientResponse> CancelHoldInvoiceAsync(this NLightningTestNode node,
                                                                               Hash paymentHash,
                                                                               CancellationToken cancellationToken)
    {
        var response = await HandleAsync<CancelHoldInvoiceClientRequest, HoldInvoiceClientResponse>(
                           node, new CancelHoldInvoiceClientRequest { PaymentHash = paymentHash },
                           cancellationToken);
        return response.Invoice;
    }

    private static async Task<TResponse> HandleAsync<TRequest, TResponse>(NLightningTestNode node, TRequest request,
                                                                          CancellationToken cancellationToken)
    {
        using var scope = node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, cancellationToken);
    }
}